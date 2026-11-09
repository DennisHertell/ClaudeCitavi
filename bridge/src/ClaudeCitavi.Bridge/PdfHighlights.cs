using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace ClaudeCitavi.Bridge
{
    /// <summary>One text occurrence in a PDF: page number and rectangles (PDF coordinates, origin bottom left).</summary>
    internal sealed class PdfHit
    {
        public int Page;
        public string Text;
        public string Context;
        public List<double[]> Rects = new List<double[]>(); // minX, minY, maxX, maxY

        public object ToJson() => new { page = Page, text = Text, context = Context, rects = Rects.Count };
    }

    /// <summary>
    /// PDF text search and Citavi highlight creation (verified API on Citavi 6):
    /// - text search with PDFTron (shipped with Citavi): pdftron.PDF.PDFDoc, TextSearch, Highlights
    /// - highlight: new Annotation(location) { Quads, OriginalColor, Visible } added to location.Annotations
    /// - link: new EntityLink(project) { Source = knowledge item, Target = annotation,
    ///   Indication = EntityLink.PdfKnowledgeItemIndication } added to project.EntityLinks
    /// All PDFTron access is via reflection because PDFNet.dll is loaded by Citavi at runtime.
    /// </summary>
    internal static class PdfHighlights
    {
        private const BindingFlags Inst = BindingFlags.Public | BindingFlags.Instance;
        private const int ModeHighlight = 32;       // TextSearch.SearchMode.e_highlight
        private const int ModeAmbientString = 64;   // e_ambient_string

        /// <summary>Searches a PDF file. Runs off the UI thread. page = 0 searches all pages.</summary>
        public static List<PdfHit> Find(string pdfPath, string text, int page, int maxHits)
        {
            if (!File.Exists(pdfPath)) throw new BridgeException("not_found", $"PDF not found: {pdfPath}", 404);
            var docType = Cv.FindType("pdftron.PDF.PDFDoc")
                          ?? throw new BridgeException("pdf_engine_not_loaded",
                              "Citavi's PDF component is not loaded yet. Open any PDF once in Citavi's preview, then retry.", 409);
            var searchType = Cv.RequireType("pdftron.PDF.TextSearch");
            var hltsType = Cv.RequireType("pdftron.PDF.Highlights");

            var pattern = NormalizeWhitespace(text);
            var doc = Activator.CreateInstance(docType, pdfPath);
            var hits = new List<PdfHit>();
            try
            {
                Cv.Call(doc, "InitSecurityHandler");
                var search = Activator.CreateInstance(searchType);
                var pageCount = (int)Cv.Call(doc, "GetPageCount");
                var start = page > 0 ? page : 1;
                var end = page > 0 ? page : pageCount;
                Cv.Call(search, "Begin", doc, pattern, ModeHighlight | ModeAmbientString, start, end);

                var run = searchType.GetMethod("Run", Inst);
                while (hits.Count < maxHits)
                {
                    var hlts = Activator.CreateInstance(hltsType);
                    var args = new object[] { 0, "", "", hlts };
                    var code = run.Invoke(search, args)?.ToString() ?? "";
                    if (code.EndsWith("found", StringComparison.OrdinalIgnoreCase))
                    {
                        var hit = new PdfHit { Page = (int)args[0], Text = (string)args[1], Context = Truncate((string)args[2], 200) };
                        Cv.Call(hlts, "Begin", doc);
                        while ((bool)Cv.Call(hlts, "HasNext"))
                        {
                            var quads = (double[])Cv.Call(hlts, "GetCurrentQuads");
                            for (var i = 0; i + 7 < quads.Length; i += 8)
                            {
                                var xs = new[] { quads[i], quads[i + 2], quads[i + 4], quads[i + 6] };
                                var ys = new[] { quads[i + 1], quads[i + 3], quads[i + 5], quads[i + 7] };
                                hit.Rects.Add(new[] { xs.Min(), ys.Min(), xs.Max(), ys.Max() });
                            }
                            Cv.Call(hlts, "Next");
                        }
                        hits.Add(hit);
                    }
                    else if (code.EndsWith("done", StringComparison.OrdinalIgnoreCase))
                    {
                        break;
                    }
                    // "page": search moved on to the next page; continue.
                }
            }
            finally
            {
                try { Cv.Call(doc, "Close"); } catch { /* ignore */ }
            }
            return hits;
        }

        /// <summary>Creates the Citavi highlight for a hit and links it to the knowledge item. Must run on the UI thread.</summary>
        public static object CreateHighlight(CitaviContext ctx, object knowledgeItem, object location, PdfHit hit, string colorHex) =>
            CreateHighlight(ctx, knowledgeItem, location, hit.Rects.Select(r => Tuple.Create(hit.Page, r)).ToList(), colorHex);

        /// <summary>Creates a highlight from explicit quads (page, minX, minY, maxX, maxY), e.g. copied from another project.</summary>
        public static object CreateHighlight(CitaviContext ctx, object knowledgeItem, object location, List<Tuple<int, double[]>> rects, string colorHex)
        {
            var annotationType = Cv.RequireType("SwissAcademic.Citavi.Annotation");
            var annotation = Cv.Create(annotationType, location);

            // Quads: IEnumerable<Quad>; element type taken from the property itself.
            var quadsProp = annotationType.GetProperty("Quads", Inst);
            var quadType = quadsProp.PropertyType.GetGenericArguments().FirstOrDefault()
                           ?? throw new BridgeException("api_mismatch", "Annotation.Quads element type unknown.", 500);
            var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(quadType));
            foreach (var r in rects) list.Add(CreateQuad(quadType, r.Item1, r.Item2));
            quadsProp.SetValue(annotation, list, null);

            var colorProp = annotationType.GetProperty("OriginalColor", Inst);
            if (colorProp != null && colorProp.PropertyType == typeof(System.Drawing.Color))
                colorProp.SetValue(annotation, ParseColor(colorHex), null);
            annotationType.GetProperty("Visible", Inst)?.SetValue(annotation, true, null);

            annotation = Cv.AddTo(Cv.Get(location, "Annotations"), annotation);

            var linkType = Cv.RequireType("SwissAcademic.Citavi.EntityLink");
            var link = Cv.Create(linkType, ctx.Project);
            linkType.GetProperty("Source", Inst).SetValue(link, knowledgeItem, null);
            linkType.GetProperty("Target", Inst).SetValue(link, annotation, null);
            var indication = linkType.GetField("PdfKnowledgeItemIndication", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as string
                             ?? "PdfKnowledgeItem";
            linkType.GetProperty("Indication", Inst).SetValue(link, indication, null);
            Cv.AddTo(Cv.Get(ctx.Project, "EntityLinks"), link);

            return new
            {
                annotationId = Cv.Get(annotation, "Id")?.ToString(),
                page = rects.Count > 0 ? rects[0].Item1 : 0,
                rects = rects.Count,
                indication,
            };
        }

        /// <summary>Quad(int pageIndex, bool isContainer, double x1, double y1, double x2, double y2) or similar.</summary>
        private static object CreateQuad(Type quadType, int page, double[] r)
        {
            foreach (var c in quadType.GetConstructors())
            {
                var ps = c.GetParameters().Select(x => x.ParameterType).ToArray();
                if (ps.Length == 6 && ps[0] == typeof(int) && ps[1] == typeof(bool) && ps.Skip(2).All(t => t == typeof(double)))
                    return c.Invoke(new object[] { page, false, r[0], r[1], r[2], r[3] });
                if (ps.Length == 5 && ps[0] == typeof(int) && ps.Skip(1).All(t => t == typeof(double)))
                    return c.Invoke(new object[] { page, r[0], r[1], r[2], r[3] });
            }
            var ctors = string.Join("; ", quadType.GetConstructors().Select(c => "(" + string.Join(", ", c.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name)) + ")"));
            throw new BridgeException("api_mismatch", $"No known constructor on {quadType.FullName}. Available: {ctors}", 500);
        }

        private static System.Drawing.Color ParseColor(string hex)
        {
            if (!string.IsNullOrWhiteSpace(hex))
            {
                try { return System.Drawing.ColorTranslator.FromHtml(hex.StartsWith("#") ? hex : "#" + hex); }
                catch { /* fall through */ }
            }
            return System.Drawing.Color.FromArgb(255, 255, 255, 0); // yellow, like Citavi's default quotation highlight
        }

        private static string NormalizeWhitespace(string s) =>
            string.Join(" ", (s ?? "").Split(new[] { ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries));

        private static string Truncate(string s, int n) => s == null ? null : s.Length > n ? s.Substring(0, n) + "…" : s;
    }
}
