using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace ClaudeCitavi.Bridge.Handlers
{
    /// <summary>Text search in attached PDFs and PDF highlights linked to knowledge items (#2).</summary>
    internal static class PdfHandlers
    {
        public static void Register(RpcDispatcher d)
        {
            d.Read("pdf.findText",
                "Searches text in a reference's PDF. params: referenceId | locationId, text, page=0 (all), limit=20",
                (ctx, p) =>
                {
                    var text = P.Req(p, "text");
                    var info = ResolvePdf(ctx, P.Str(p, "referenceId"), P.Str(p, "locationId"));
                    var page = P.Int(p, "page", 0);
                    var limit = P.Int(p, "limit", 20);
                    return new Continuation(() =>
                    {
                        var path = EnsureLocal(ctx, info);
                        var hits = PdfHighlights.Find(path, text, page, limit);
                        return new { locationId = info.LocationId, fileName = info.FileName, total = hits.Count, hits = hits.Select(h => h.ToJson()).ToList() };
                    });
                });

            d.Write("pdf.highlight",
                "Highlights text in the PDF and links it to an existing knowledge item, like marking it in Citavi. " +
                "params: knowledgeItemId, text? (default: the knowledge item's text), page?, occurrence=1, locationId?, color? (#RRGGBB)",
                (ctx, p) =>
                {
                    var ki = ctx.KnowledgeItem(P.Req(p, "knowledgeItemId"));
                    var reference = Cv.Get(ki, "Reference");

                    // Explicit quads (copied from another project): no text search needed.
                    if (p["quads"] is JArray quadArray && quadArray.Count > 0)
                    {
                        var target = ResolvePdf(ctx, Cv.Get(reference, "Id")?.ToString(), P.Str(p, "locationId"));
                        var rects = quadArray.Select(q => Tuple.Create((int)q[0], new[] { (double)q[1], (double)q[2], (double)q[3], (double)q[4] })).ToList();
                        var created = PdfHighlights.CreateHighlight(ctx, ki, target.Location, rects, P.Str(p, "color"));
                        return new { highlight = created, knowledgeItem = Serializer.KnowledgeItem(ki), warnings = new List<string>() };
                    }

                    var text = P.Str(p, "text") ?? Cv.GetString(ki, "Text")
                               ?? throw new BridgeException("bad_request", "The knowledge item has no text; pass 'text'.");
                    var info = ResolvePdf(ctx, Cv.Get(reference, "Id")?.ToString(), P.Str(p, "locationId"));
                    var page = P.Int(p, "page", 0);
                    var occurrence = Math.Max(1, P.Int(p, "occurrence", 1));
                    var color = P.Str(p, "color");

                    return new Continuation(() =>
                    {
                        var path = EnsureLocal(ctx, info);
                        var hits = PdfHighlights.Find(path, text, page, occurrence);
                        if (hits.Count < occurrence)
                            throw new BridgeException("text_not_found",
                                $"Text not found in {info.FileName}{(page > 0 ? $" on page {page}" : "")}. " +
                                "Check spelling, hyphenation and line breaks, or pass a shorter, exact passage.", 404);
                        var hit = hits[occurrence - 1];

                        return ctx.Ui(() =>
                        {
                            var highlight = PdfHighlights.CreateHighlight(ctx, ki, info.Location, hit, color);
                            var warnings = new List<string>();
                            if (Cv.GetString(ki, "PageRange") == null)
                            {
                                try { Cv.SetFromString(ki, "PageRange", hit.Page.ToString()); }
                                catch (Exception ex) { warnings.Add("PageRange: " + ex.GetBaseException().Message); }
                            }
                            return (object)new
                            {
                                highlight,
                                matchedText = hit.Text,
                                knowledgeItem = Serializer.KnowledgeItem(ki),
                                warnings,
                            };
                        });
                    });
                });
        }

        /// <summary>The PDF attachment of a reference (first PDF unless locationId is given).</summary>
        internal static AttachmentInfo ResolvePdf(CitaviContext ctx, string referenceId, string locationId)
        {
            IEnumerable<object> refs = referenceId != null ? new[] { ctx.Reference(referenceId) } : ctx.References;
            var infos = refs.SelectMany(r => Attachments.ElectronicLocations(r).Select(l => Attachments.Describe(l, r)));
            if (locationId != null)
            {
                return infos.FirstOrDefault(i => string.Equals(i.LocationId, locationId, StringComparison.OrdinalIgnoreCase))
                       ?? throw new BridgeException("not_found", $"Attachment {locationId} not found.", 404);
            }
            if (referenceId == null) throw new BridgeException("bad_request", "Pass referenceId or locationId.");
            return infos.FirstOrDefault(i => (i.FileName ?? "").EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                   ?? throw new BridgeException("not_found", "The reference has no PDF attachment.", 404);
        }

        /// <summary>Makes sure the PDF exists locally (downloads cloud attachments). Runs off the UI thread.</summary>
        internal static string EnsureLocalPublic(CitaviContext ctx, AttachmentInfo info) => EnsureLocal(ctx, info);

        private static string EnsureLocal(CitaviContext ctx, AttachmentInfo info)
        {
            if (info.AvailableLocally) return info.LocalPath;
            var log = new List<string>();
            var task = ctx.Ui(() => Attachments.StartDownload(info.Location, ctx.Project, log));
            Attachments.Wait(task, TimeSpan.FromMinutes(3), log);
            var refreshed = ctx.Ui(() => Attachments.Describe(info.Location, info.Reference));
            if (!refreshed.AvailableLocally)
                throw new BridgeException("not_available", "The PDF is not available on this computer: " + string.Join("; ", log), 409);
            return refreshed.LocalPath;
        }
    }
}
