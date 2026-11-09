using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace ClaudeCitavi.Bridge.Handlers
{
    /// <summary>Attachments (Standorte): attach local files, list and download (incl. cloud projects).</summary>
    internal static class AttachmentHandlers
    {
        public static void Register(RpcDispatcher d)
        {
            d.Write("attachments.add",
                "Attaches a local file to a reference. Citavi copies it into the project (cloud projects: uploads it). " +
                "params: referenceId, path, mode (copy|move|link, default copy), notes?",
                (ctx, p) =>
                {
                    var reference = ctx.Reference(P.Req(p, "referenceId"));
                    var source = P.Req(p, "path");
                    if (!File.Exists(source)) throw new BridgeException("not_found", $"File not found: {source}", 404);
                    var mode = (P.Str(p, "mode") ?? "copy").ToLowerInvariant();
                    var details = new Dictionary<string, object>();

                    // Citavi 6: Location(Reference, LocationType, LinkedResource, string callNumber).
                    // LinkedResource(string path, AttachmentAction, AttachmentNaming) lets Citavi itself copy/move
                    // the file into the project's attachments (and upload it for cloud projects).
                    var linkedType = Cv.RequireType("SwissAcademic.Citavi.LinkedResource");
                    object linked;
                    if (mode == "link")
                    {
                        linked = Cv.Create(linkedType, source);
                    }
                    else
                    {
                        var ctor = linkedType.GetConstructors().FirstOrDefault(c => c.GetParameters().Length == 3 && c.GetParameters()[0].ParameterType == typeof(string))
                                   ?? throw new BridgeException("api_mismatch", "LinkedResource(string, AttachmentAction, AttachmentNaming) not found.", 500);
                        var ps = ctor.GetParameters();
                        var action = Cv.EnumValue(ps[1].ParameterType, mode == "move" ? new[] { "Move" } : new[] { "Copy" });
                        var naming = Cv.EnumValue(ps[2].ParameterType, new[] { "Original", "OriginalFileName", "KeepOriginal", "None", "Default" }, fallbackToFirst: true);
                        details["attachmentAction"] = action?.ToString();
                        details["attachmentNaming"] = naming?.ToString();
                        details["attachmentActionValues"] = Enum.GetNames(ps[1].ParameterType);
                        details["attachmentNamingValues"] = Enum.GetNames(ps[2].ParameterType);
                        if (action == null) throw new BridgeException("api_mismatch", $"AttachmentAction has no '{mode}'. Values: {string.Join(", ", Enum.GetNames(ps[1].ParameterType))}", 500);
                        linked = ctor.Invoke(new[] { source, action, naming });
                    }

                    var locationType = Enum.Parse(Cv.RequireType("SwissAcademic.Citavi.LocationType"), "ElectronicAddress");
                    var location = Cv.Create(Cv.RequireType("SwissAcademic.Citavi.Location"), reference, locationType, linked, "");
                    var notes = P.Str(p, "notes");
                    if (notes != null) Cv.SetFromString(location, "Notes", notes);
                    location = Cv.AddTo(Cv.Get(reference, "Locations"), location);

                    return new
                    {
                        attachment = Attachments.Describe(location, reference).ToJson(),
                        details,
                        note = ctx.IsCloud ? "Cloud project: Citavi uploads the file in the background." : null,
                    };
                });

            d.Read("attachments.list",
                "Files/URLs of references incl. cloud status. params: referenceIds[]?, categoryPath?, query?, onlyMissing=false, onlyPdf=false, limit=500",
                (ctx, p) =>
                {
                    var infos = Select(ctx, p).ToList();
                    if (P.Bool(p, "onlyMissing", false)) infos = infos.Where(i => !i.AvailableLocally && i.Storage != "url").ToList();
                    if (P.Bool(p, "onlyPdf", false)) infos = infos.Where(IsPdf).ToList();
                    return new
                    {
                        projectIsCloud = ctx.IsCloud,
                        total = infos.Count,
                        missingLocally = infos.Count(i => !i.AvailableLocally && i.Storage != "url"),
                        items = infos.Take(P.Int(p, "limit", 500)).Select(i => i.ToJson()).ToList(),
                    };
                });

            // Does not change the Citavi project, therefore allowed in read-only mode.
            d.Read("attachments.download",
                "Downloads attachments (Citavi fetches cloud files with your Citavi login) and copies them to a local folder. " +
                "params: referenceIds[]?, locationIds[]?, categoryPath?, query?, targetFolder?, naming (citation|original, default citation), " +
                "onlyPdf=true, overwrite=false, timeoutSeconds=300, limit=200",
                (ctx, p) =>
                {
                    var infos = Select(ctx, p).Where(i => i.Storage != "url").ToList();
                    var locationIds = P.StrList(p, "locationIds");
                    if (locationIds.Count > 0) infos = infos.Where(i => locationIds.Contains(i.LocationId, StringComparer.OrdinalIgnoreCase)).ToList();
                    if (P.Bool(p, "onlyPdf", true)) infos = infos.Where(IsPdf).ToList();
                    infos = infos.Take(P.Int(p, "limit", 200)).ToList();

                    var targetFolder = P.Str(p, "targetFolder") ?? BridgeConfig.Current?.EffectiveDownloadFolder
                                       ?? throw new BridgeException("bad_request", "targetFolder is required.");
                    var naming = P.Str(p, "naming") ?? "citation";
                    var overwrite = P.Bool(p, "overwrite", false);
                    var timeout = TimeSpan.FromSeconds(P.Int(p, "timeoutSeconds", 300));

                    // Step 1 (UI thread): ask Citavi to download everything that is not local yet.
                    var jobs = infos.Select(i =>
                    {
                        var log = new List<string>();
                        Task task = null;
                        if (!i.AvailableLocally) task = Attachments.StartDownload(i.Location, ctx.Project, log);
                        return (Info: i, Task: task, Log: log, WasLocal: i.AvailableLocally);
                    }).ToList();

                    // Step 2 (worker thread): wait, re-resolve, copy.
                    return new Continuation(() =>
                    {
                        var deadline = DateTime.UtcNow + timeout;
                        var producedPaths = jobs.Select(j =>
                        {
                            var remaining = deadline - DateTime.UtcNow;
                            return Attachments.Wait(j.Task, remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, j.Log);
                        }).ToList();

                        var refreshed = ctx.Ui(() => jobs.Select(j => Attachments.Describe(j.Info.Location, j.Info.Reference)).ToList());

                        Directory.CreateDirectory(targetFolder);
                        var results = new List<object>();
                        var missing = 0;
                        for (var k = 0; k < jobs.Count; k++)
                        {
                            var info = refreshed[k];
                            var source = info.AvailableLocally ? info.LocalPath
                                       : producedPaths[k] != null && File.Exists(producedPaths[k]) ? producedPaths[k] : null;

                            if (source == null)
                            {
                                missing++;
                                results.Add(new
                                {
                                    status = "not_available",
                                    referenceId = info.ReferenceId,
                                    locationId = info.LocationId,
                                    fileName = info.FileName,
                                    storage = info.Storage,
                                    hint = "Citavi has not downloaded the file. Open it once in Citavi or check the cloud sync, then retry.",
                                    tried = jobs[k].Log,
                                });
                                continue;
                            }

                            var fileName = ctx.Ui(() => Attachments.TargetFileName(info, naming));
                            var target = Path.Combine(targetFolder, fileName);
                            if (File.Exists(target) && !overwrite)
                            {
                                if (new FileInfo(target).Length == new FileInfo(source).Length)
                                {
                                    results.Add(new { status = "already_there", referenceId = info.ReferenceId, locationId = info.LocationId, path = target });
                                    continue;
                                }
                                target = Attachments.UniquePath(targetFolder, fileName);
                            }
                            File.Copy(source, target, true);
                            results.Add(new
                            {
                                status = jobs[k].WasLocal ? "copied" : "downloaded",
                                referenceId = info.ReferenceId,
                                locationId = info.LocationId,
                                path = target,
                                citaviPath = info.LocalPath,
                            });
                        }

                        return new
                        {
                            targetFolder,
                            total = results.Count,
                            ok = results.Count - missing,
                            notAvailable = missing,
                            items = results,
                        };
                    });
                });
        }

        /// <summary>References selected by referenceIds / categoryPath / query (default: all), expanded to electronic locations.</summary>
        private static IEnumerable<AttachmentInfo> Select(CitaviContext ctx, JObject p)
        {
            IEnumerable<object> refs;
            var ids = P.StrList(p, "referenceIds");
            if (ids.Count > 0) refs = ids.Select(ctx.Reference);
            else
            {
                refs = ctx.References;
                var paths = P.CategoryPaths(p, "categoryPath");
                if (paths.Count > 0)
                {
                    var cat = ctx.RequireCategory(paths[0]);
                    refs = refs.Where(r => Cv.Contains(Cv.Get(r, "Categories"), cat));
                }
                var query = P.Str(p, "query");
                if (query != null)
                {
                    refs = refs.Where(r => ((Cv.GetString(r, "Title") ?? "") + " " + (Cv.GetString(r, "ShortTitle") ?? ""))
                        .IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0);
                }
            }
            return refs.SelectMany(r => Attachments.ElectronicLocations(r).Select(l => Attachments.Describe(l, r)));
        }

        private static bool IsPdf(AttachmentInfo i) =>
            (i.FileName ?? i.Address ?? "").EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);
    }
}
