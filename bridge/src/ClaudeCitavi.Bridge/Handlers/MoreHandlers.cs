using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace ClaudeCitavi.Bridge.Handlers
{
    /// <summary>
    /// PDF text, reference links (Querverweise), order and subheadings in the knowledge organizer,
    /// saved searches, lists of persons/periodicals/publishers/series, RIS/BibTeX import and export.
    /// </summary>
    internal static class MoreHandlers
    {
        public static void Register(RpcDispatcher d)
        {
            RegisterPdfText(d);
            RegisterLinks(d);
            RegisterOrganizer(d);
            RegisterSavedSearches(d);
            RegisterLists(d);
            RegisterImportExport(d);
        }

        private static void RequireConfirm(JObject p, string what)
        {
            if (!P.Bool(p, "confirm", false))
                throw new BridgeException("confirm_required", $"{what} cannot be undone from here. Repeat the call with confirm=true after the user agreed.", 409);
        }

        // ---- PDF text ------------------------------------------------------------------------

        private static void RegisterPdfText(RpcDispatcher d)
        {
            d.Read("pdf.text",
                "Text of a reference's PDF page by page, with the printed page label where the PDF defines one. " +
                "params: referenceId | locationId, fromPage=1, toPage=0 (= last), maxChars=300000",
                (ctx, p) =>
                {
                    var info = PdfHandlers.ResolvePdf(ctx, P.Str(p, "referenceId"), P.Str(p, "locationId"));
                    var from = Math.Max(1, P.Int(p, "fromPage", 1));
                    var to = P.Int(p, "toPage", 0);
                    var maxChars = P.Int(p, "maxChars", 300000);
                    return new Continuation(() =>
                    {
                        var path = PdfHandlers.EnsureLocalPublic(ctx, info);
                        return ExtractText(path, from, to, maxChars, info.FileName);
                    });
                });
        }

        internal static object ExtractText(string path, int from, int to, int maxChars, string fileName)
        {
            var docType = Cv.FindType("pdftron.PDF.PDFDoc")
                          ?? throw new BridgeException("pdf_engine_not_loaded", "Citavi's PDF component is not loaded yet. Open any PDF once in Citavi's preview, then retry.", 409);
            var extractorType = Cv.RequireType("pdftron.PDF.TextExtractor");
            var doc = Activator.CreateInstance(docType, path);
            var pages = new List<object>();
            var total = 0;
            var truncated = false;
            try
            {
                Cv.Call(doc, "InitSecurityHandler");
                var count = (int)Cv.Call(doc, "GetPageCount");
                var last = to <= 0 || to > count ? count : to;
                for (var i = from; i <= last; i++)
                {
                    var page = Cv.Call(doc, "GetPage", i);
                    var te = Activator.CreateInstance(extractorType);
                    string text;
                    try
                    {
                        Cv.Call(te, "Begin", page);
                        text = (string)Cv.Call(te, "GetAsText");
                    }
                    finally { (te as IDisposable)?.Dispose(); }

                    string label = null;
                    try
                    {
                        var pl = Cv.Call(doc, "GetPageLabel", i);
                        if (pl != null && (bool)Cv.Call(pl, "IsValid")) label = Cv.NullIfEmpty((string)Cv.Call(pl, "GetLabelTitle", i));
                    }
                    catch { /* no page labels */ }

                    if (total + text.Length > maxChars) { truncated = true; break; }
                    total += text.Length;
                    pages.Add(new { page = i, label, text });
                }
                return new { fileName, pageCount = count, truncated, pages };
            }
            finally
            {
                try { Cv.Call(doc, "Close"); } catch { /* ignore */ }
            }
        }

        // ---- Reference links (Querverweise) ---------------------------------------------------

        private static void RegisterLinks(RpcDispatcher d)
        {
            d.Read("links.list", "Cross references of a reference (both directions). params: referenceId", (ctx, p) =>
            {
                var r = ctx.Reference(P.Req(p, "referenceId"));
                return ReferenceLinks(ctx).Where(l => ReferenceEquals(Cv.Get(l, "Source"), r) || ReferenceEquals(Cv.Get(l, "Target"), r))
                    .Select(l => LinkJson(l, r)).ToList();
            });

            d.Write("links.create",
                "Creates a cross reference: source refers to target. params: sourceId, targetId, relation? (Neutral|SomewhatPositive|Positive|SomewhatNegative|Negative), notes?, pages? (where in the source)",
                (ctx, p) =>
                {
                    var source = ctx.Reference(P.Req(p, "sourceId"));
                    var target = ctx.Reference(P.Req(p, "targetId"));
                    var linkType = Cv.RequireType("SwissAcademic.Citavi.EntityLink");
                    var link = Cv.Create(linkType, ctx.Project);
                    linkType.GetProperty("Source").SetValue(link, source, null);
                    linkType.GetProperty("Target").SetValue(link, target, null);
                    var indication = linkType.GetField("ReferenceLinkIndication", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as string ?? "ReferenceLink";
                    linkType.GetProperty("Indication").SetValue(link, indication, null);
                    var warnings = new List<string>();
                    foreach (var (param, prop) in new[] { ("relation", "RelationType"), ("notes", "Notes"), ("pages", "SourceData") })
                    {
                        var v = P.Str(p, param);
                        if (v == null) continue;
                        try { Cv.SetFromString(link, prop, v); } catch (Exception ex) { warnings.Add($"{prop}: {ex.GetBaseException().Message}"); }
                    }
                    link = Cv.AddTo(Cv.Get(ctx.Project, "EntityLinks"), link);
                    return new { link = LinkJson(link, source), warnings };
                });

            d.Write("links.delete", "Deletes cross references. params: linkIds[], confirm=true", (ctx, p) =>
            {
                RequireConfirm(p, "Deleting cross references");
                var ids = new HashSet<string>(P.StrList(p, "linkIds"), StringComparer.OrdinalIgnoreCase);
                var links = ReferenceLinks(ctx).Where(l => ids.Contains(Cv.Get(l, "Id")?.ToString() ?? "")).ToList();
                foreach (var l in links) Cv.RemoveFrom(Cv.Get(ctx.Project, "EntityLinks"), l);
                return new { deleted = links.Count };
            });
        }

        private static IEnumerable<object> ReferenceLinks(CitaviContext ctx) =>
            Cv.Items(Cv.Get(ctx.Project, "EntityLinks")).Where(l =>
                Cv.Get(l, "Source")?.GetType().Name == "Reference" && Cv.Get(l, "Target")?.GetType().Name == "Reference");

        private static object LinkJson(object link, object perspective)
        {
            var source = Cv.Get(link, "Source");
            var target = Cv.Get(link, "Target");
            var outgoing = ReferenceEquals(source, perspective);
            var other = outgoing ? target : source;
            return new
            {
                id = Cv.Get(link, "Id")?.ToString(),
                direction = outgoing ? "refersTo" : "referredFrom",
                otherId = Cv.Get(other, "Id")?.ToString(),
                otherShortTitle = Cv.GetString(other, "ShortTitle"),
                relation = Cv.Get(link, "RelationType")?.ToString(),
                notes = Cv.GetString(link, "Notes"),
                pages = Cv.GetString(link, "SourceData"),
            };
        }

        // ---- Knowledge organizer: order and subheadings ---------------------------------------

        private static void RegisterOrganizer(RpcDispatcher d)
        {
            d.Read("organizer.list", "Knowledge items of a category in the order of Citavi's knowledge organizer (incl. subheadings). params: path", (ctx, p) =>
            {
                var cat = ctx.RequireCategory(P.CategoryPath(p["path"]));
                return Cv.Items(Cv.Get(cat, "KnowledgeItems")).Select(k =>
                {
                    var s = Serializer.KnowledgeItem(k);
                    s["isSubheading"] = Cv.Get(k, "KnowledgeItemType")?.ToString() == "Subheading";
                    s["referenceShortTitle"] = Cv.GetString(Cv.Get(k, "Reference"), "ShortTitle");
                    return s;
                }).ToList();
            });

            d.Write("organizer.reorder",
                "Sets the manual order of knowledge items within a category. Listed ids come first in this order. params: path, order[] (knowledge item ids)",
                (ctx, p) =>
                {
                    var cat = ctx.RequireCategory(P.CategoryPath(p["path"]));
                    var col = Cv.Get(cat, "KnowledgeItems");
                    var wanted = P.StrList(p, "order").Select(ctx.KnowledgeItem).ToList();
                    foreach (var ki in wanted)
                        if (!Cv.Contains(col, ki)) throw new BridgeException("bad_request", $"Knowledge item {Cv.Get(ki, "Id")} is not in this category.");
                    var autoSortBefore = DisableAutoSort(col);
                    // Move(item, target) puts the item AFTER target (verified on 6.20); the first item goes to the top via a swap.
                    object previous = null;
                    foreach (var ki in wanted)
                    {
                        if (previous == null)
                        {
                            var first = Cv.Items(col).First();
                            if (!ReferenceEquals(first, ki)) { Move(col, ki, first); Move(col, first, ki); }
                        }
                        else Move(col, ki, previous);
                        previous = ki;
                    }
                    BridgeLog.Write($"organizer.reorder: autoSort was {autoSortBefore}, allowMove {Cv.Get(col, "AllowMove")}");
                    return Cv.Items(col).Select(k => Cv.Get(k, "Id")?.ToString()).ToList();
                });

            d.Write("organizer.addSubheading",
                "Inserts a subheading (Zwischenüberschrift) into a category of the knowledge organizer. params: path, text, afterKnowledgeItemId? (default: at the top)",
                (ctx, p) =>
                {
                    var cat = ctx.RequireCategory(P.CategoryPath(p["path"]));
                    var kiType = Cv.RequireType("SwissAcademic.Citavi.KnowledgeItem");
                    var subheading = Enum.Parse(Cv.RequireType("SwissAcademic.Citavi.KnowledgeItemType"), "Subheading");
                    var ki = Cv.Create(kiType, ctx.Project, subheading);
                    Cv.SetFromString(ki, "CoreStatement", P.Req(p, "text"));
                    ki = Cv.AddTo(Cv.Get(ctx.Project, "Thoughts"), ki);
                    // Assign via the item's own categories (persisted); the category's item list follows.
                    var kiCats = Cv.Get(ki, "Categories");
                    if (!Cv.Contains(kiCats, cat)) Cv.AddTo(kiCats, cat);
                    var col = Cv.Get(cat, "KnowledgeItems");
                    if (!Cv.Contains(col, ki)) Cv.AddTo(col, ki);
                    DisableAutoSort(col);
                    var after = P.Str(p, "afterKnowledgeItemId");
                    var items = Cv.Items(col).Where(x => !ReferenceEquals(x, ki)).ToList();
                    // Move(item, target) puts the item after target.
                    if (after == null) { if (items.Count > 0) { Move(col, ki, items[0]); Move(col, items[0], ki); } }
                    else Move(col, ki, ctx.KnowledgeItem(after));
                    return new { id = Cv.Get(ki, "Id")?.ToString(), order = Cv.Items(col).Select(k => Cv.Get(k, "Id")?.ToString()).ToList() };
                });
        }

        private static object DisableAutoSort(object collection)
        {
            var prop = collection.GetType().GetProperty("AutoSort");
            var before = prop?.GetValue(collection, null);
            if (prop != null && prop.CanWrite && Equals(before, true)) prop.SetValue(collection, false, null);
            return before;
        }

        private static void Move(object collection, object item, object after)
        {
            var move = collection.GetType().GetMethods().FirstOrDefault(m => m.Name == "Move" && m.GetParameters().Length == 2)
                       ?? throw new BridgeException("api_mismatch", "Move(item, after) not found.", 500);
            move.Invoke(collection, new[] { item, after });
        }

        // ---- Saved searches ---------------------------------------------------------------------

        private static void RegisterSavedSearches(RpcDispatcher d)
        {
            d.Read("savedSearches.list", "Saved searches of the project.", (ctx, p) =>
                Cv.Items(Cv.Get(Cv.Get(ctx.Project, "ProjectSettings"), "SavedSearches")).Select(s => new
                {
                    name = Cv.GetString(s, "Name"),
                    query = Cv.GetString(s, "Query"),
                    matchCase = Cv.Get(s, "MatchCase"),
                    matchWholeWord = Cv.Get(s, "MatchWholeWord"),
                }).ToList());

            d.Write("savedSearches.create", "Saves a search (Citavi search syntax). params: name, query, matchCase=false, matchWholeWord=false", (ctx, p) =>
            {
                var s = Cv.Create(Cv.RequireType("SwissAcademic.Citavi.SavedSearch"));
                Cv.SetFromString(s, "Name", P.Req(p, "name"));
                Cv.SetFromString(s, "Query", P.Req(p, "query"));
                Cv.SetFromString(s, "MatchCase", P.Bool(p, "matchCase", false).ToString());
                Cv.SetFromString(s, "MatchWholeWord", P.Bool(p, "matchWholeWord", false).ToString());
                Cv.AddTo(Cv.Get(Cv.Get(ctx.Project, "ProjectSettings"), "SavedSearches"), s);
                return new { name = Cv.GetString(s, "Name") };
            });

            d.Write("savedSearches.delete", "Deletes saved searches. params: names[], confirm=true", (ctx, p) =>
            {
                RequireConfirm(p, "Deleting saved searches");
                var col = Cv.Get(Cv.Get(ctx.Project, "ProjectSettings"), "SavedSearches");
                var names = new HashSet<string>(P.StrList(p, "names"), StringComparer.OrdinalIgnoreCase);
                var hits = Cv.Items(col).Where(s => names.Contains(Cv.GetString(s, "Name") ?? "")).ToList();
                foreach (var s in hits) Cv.RemoveFrom(col, s);
                return new { deleted = hits.Count };
            });
        }

        // ---- Persons, periodicals, publishers, series ------------------------------------------

        private static readonly Dictionary<string, (string Collection, string[] UsedIn, string[] Fields)> Lists =
            new Dictionary<string, (string, string[], string[])>(StringComparer.OrdinalIgnoreCase)
            {
                ["persons"] = ("Persons", new[] { "Authors", "Editors", "Collaborators", "Organizations", "OthersInvolved", "Translators", "SeriesTitleEditors" },
                    new[] { "LastName", "FirstName", "MiddleName", "Prefix", "Suffix", "Abbreviation", "Title", "Sex", "Notes" }),
                ["periodicals"] = ("Periodicals", new[] { "Periodical" }, new[] { "Name", "Issn", "Eissn", "StandardAbbreviation", "UserAbbreviation1", "UserAbbreviation2", "Notes" }),
                ["publishers"] = ("Publishers", new[] { "Publishers" }, new[] { "Name", "Notes" }),
                ["seriesTitles"] = ("SeriesTitles", new[] { "SeriesTitle" }, new[] { "Name", "Notes" }),
            };

        private static void RegisterLists(RpcDispatcher d)
        {
            d.Read("lists.get",
                "Entries of a project list with usage counts. params: kind (persons|periodicals|publishers|seriesTitles), query?, onlyUnused=false, limit=500",
                (ctx, p) =>
                {
                    var (collection, usedIn, fields) = ListDef(p);
                    var usage = Usage(ctx, usedIn);
                    var query = P.Str(p, "query");
                    var onlyUnused = P.Bool(p, "onlyUnused", false);
                    return Cv.Items(Cv.Get(ctx.Project, collection))
                        .Where(x => query == null || (Cv.GetString(x, "FullName") ?? Cv.GetString(x, "Name") ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                        .Where(x => !onlyUnused || !usage.ContainsKey(x))
                        .Take(P.Int(p, "limit", 500))
                        .Select(x =>
                        {
                            var o = new Dictionary<string, object> { ["id"] = Cv.Get(x, "Id")?.ToString(), ["fullName"] = Cv.GetString(x, "FullName") };
                            foreach (var f in fields) if (Cv.Has(x, f)) o[f] = Cv.GetString(x, f);
                            o["usedBy"] = usage.TryGetValue(x, out var n) ? n : 0;
                            return o;
                        }).ToList();
                });

            d.Write("lists.update", "Updates fields of a list entry (e.g. correct a name). params: kind, id, fields{}", (ctx, p) =>
            {
                var (collection, _, _) = ListDef(p);
                var item = Find(ctx, collection, P.Req(p, "id"));
                var warnings = new List<string>();
                if (p["fields"] is JObject fields)
                    foreach (var f in fields.Properties())
                    {
                        try { Cv.SetFromString(item, f.Name, f.Value.Type == JTokenType.Null ? null : f.Value.ToString()); }
                        catch (Exception ex) { warnings.Add($"{f.Name}: {ex.GetBaseException().Message}"); }
                    }
                return new { fullName = Cv.GetString(item, "FullName"), warnings };
            });

            d.Write("lists.merge", "Merges list entries into one (Citavi's Merge: all references then point to the target). params: kind, targetId, sourceIds[]", (ctx, p) =>
            {
                var (collection, _, _) = ListDef(p);
                var target = Find(ctx, collection, P.Req(p, "targetId"));
                var merged = 0;
                foreach (var id in P.StrList(p, "sourceIds"))
                {
                    var source = Find(ctx, collection, id);
                    if (ReferenceEquals(source, target)) continue;
                    var merge = target.GetType().GetMethods().FirstOrDefault(m => m.Name == "Merge" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType.IsInstanceOfType(source))
                                ?? throw new BridgeException("api_mismatch", $"{target.GetType().Name}.Merge not found.", 500);
                    merge.Invoke(target, new[] { source });
                    merged++;
                }
                return new { target = Cv.GetString(target, "FullName"), merged };
            });

            d.Write("lists.deleteUnused", "Deletes list entries no reference uses. params: kind, ids[]? (default: all unused), confirm=true", (ctx, p) =>
            {
                RequireConfirm(p, "Deleting list entries");
                var (collection, usedIn, _) = ListDef(p);
                var usage = Usage(ctx, usedIn);
                var ids = new HashSet<string>(P.StrList(p, "ids"), StringComparer.OrdinalIgnoreCase);
                var col = Cv.Get(ctx.Project, collection);
                var victims = Cv.Items(col).Where(x => !usage.ContainsKey(x) && (ids.Count == 0 || ids.Contains(Cv.Get(x, "Id")?.ToString() ?? ""))).ToList();
                foreach (var v in victims) Cv.RemoveFrom(col, v);
                return new { deleted = victims.Count };
            });
        }

        private static (string, string[], string[]) ListDef(JObject p)
        {
            var kind = P.Req(p, "kind");
            if (!Lists.TryGetValue(kind, out var def)) throw new BridgeException("bad_request", $"kind must be one of {string.Join(", ", Lists.Keys)}.");
            return def;
        }

        private static object Find(CitaviContext ctx, string collection, string id)
        {
            if (!Guid.TryParse(id, out var guid)) throw new BridgeException("bad_request", $"'{id}' is not a GUID.");
            return Cv.Items(Cv.Get(ctx.Project, collection)).FirstOrDefault(x => Equals(Cv.Get(x, "Id"), guid))
                   ?? throw new BridgeException("not_found", $"Entry {id} not found in {collection}.", 404);
        }

        private static Dictionary<object, int> Usage(CitaviContext ctx, string[] usedIn)
        {
            var usage = new Dictionary<object, int>();
            foreach (var r in ctx.References)
                foreach (var prop in usedIn)
                {
                    if (!Cv.Has(r, prop)) continue;
                    var v = Cv.Get(r, prop);
                    var items = v is IEnumerable e && !(v is string) ? e.Cast<object>() : v == null ? Enumerable.Empty<object>() : new[] { v };
                    foreach (var x in items) usage[x] = usage.TryGetValue(x, out var n) ? n + 1 : 1;
                }
            return usage;
        }

        // ---- RIS / BibTeX ---- (import runs in the MCP server: Citavi's Importer returns no references without its preview dialog in 6.20)

        private static void RegisterImportExport(RpcDispatcher d)
        {
            d.Read("references.exportFile",
                "Exports references to RIS or BibTeX with Citavi's exporter. params: path, format (ris|bibtex), referenceIds[]? (default: all), categoryPath?",
                (ctx, p) =>
                {
                    var path = P.Req(p, "path");
                    var format = (P.Str(p, "format") ?? "ris").ToLowerInvariant();
                    var ids = new HashSet<string>(P.StrList(p, "referenceIds"), StringComparer.OrdinalIgnoreCase);
                    var catPath = P.CategoryPath(p["categoryPath"]);
                    var cat = catPath.Count > 0 ? ctx.RequireCategory(catPath) : null;
                    var refs = ctx.References.Where(r => (ids.Count == 0 || ids.Contains(Cv.Get(r, "Id")?.ToString() ?? "")) &&
                                                         (cat == null || Cv.Contains(Cv.Get(r, "Categories"), cat))).ToList();

                    var engine = Cv.StaticValue(Cv.RequireType("SwissAcademic.Citavi.Shell.Program"), "Engine");
                    var transformers = Cv.Items(Cv.Get(Cv.Get(engine, "TransformerManager"), "AllTransformers")).ToList();
                    var wanted = format == "bibtex" ? "BibTeX" : "RIS";
                    var transformer = transformers.FirstOrDefault(t => string.Equals(Cv.GetString(t, "Name"), wanted, StringComparison.OrdinalIgnoreCase))
                                      ?? transformers.FirstOrDefault(t => (Cv.GetString(t, "Name") ?? "").StartsWith(wanted, StringComparison.OrdinalIgnoreCase))
                                      ?? throw new BridgeException("not_found", $"No {wanted} transformer. Available: {string.Join(", ", transformers.Select(t => Cv.GetString(t, "Name")).Take(40))}", 404);

                    var property = Cv.Create(Cv.RequireType("SwissAcademic.Citavi.DataExchange.FileExportDataExchangeProperty"));
                    var refType = Cv.RequireType("SwissAcademic.Citavi.Reference");
                    var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(refType));
                    foreach (var r in refs) list.Add(r);
                    property.GetType().GetProperty("ExportReferences").SetValue(property, list, null);
                    property.GetType().GetProperty("FileName").SetValue(property, path, null);
                    property.GetType().GetProperty("Transformer").SetValue(property, transformer, null);
                    if (property.GetType().GetProperty("ProjectName") is PropertyInfo pn) pn.SetValue(property, Cv.GetString(ctx.Project, "Name"), null);
                    if (format == "bibtex" && property.GetType().GetProperty("BibTeXExportSettings") is PropertyInfo bs && bs.GetValue(property, null) == null)
                    {
                        try { bs.SetValue(property, Activator.CreateInstance(bs.PropertyType), null); }
                        catch (Exception ex) { BridgeLog.Write("BibTeX settings: " + ex.GetBaseException().Message); }
                    }

                    var exporter = Cv.Create(Cv.RequireType("SwissAcademic.Citavi.DataExchange.Exporter"));
                    var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
                    var run = exporter.GetType().GetMethod("RunAsync");
                    Task task;
                    try { task = (Task)run.Invoke(exporter, new object[] { property, stream, CancellationToken.None, null }); }
                    catch { stream.Dispose(); throw; }

                    return new Continuation(() =>
                    {
                        try
                        {
                            if (!task.Wait(TimeSpan.FromMinutes(5))) throw new BridgeException("timeout", "Export timed out.", 504);
                            if (task.IsFaulted) throw new BridgeException("export_failed", task.Exception.GetBaseException().Message, 500);
                        }
                        finally { stream.Dispose(); }
                        return new { path, format = wanted, references = refs.Count, bytes = new FileInfo(path).Length };
                    });
                });
        }

        private static object TaskResult(Task t) => t.GetType().GetProperty("Result")?.GetValue(t, null);

        private static bool SameIdentifier(object a, object b)
        {
            foreach (var field in new[] { "Doi", "PubMedId", "ArxivId" })
            {
                var x = Cv.GetString(a, field);
                if (x != null && string.Equals(x, Cv.GetString(b, field), StringComparison.OrdinalIgnoreCase)) return true;
            }
            var ia = OrganizeHandlers.NormIsbn(Cv.GetString(a, "Isbn"));
            return ia != null && ia == OrganizeHandlers.NormIsbn(Cv.GetString(b, "Isbn"));
        }
    }
}
