using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace ClaudeCitavi.Bridge.Handlers
{
    /// <summary>Knowledge items (Wissenselemente): quotations, summaries, comments.</summary>
    internal static class KnowledgeHandlers
    {
        // Friendly names -> Citavi QuotationType enum names.
        private static readonly Dictionary<string, string> TypeAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["direct"] = "DirectQuotation", ["directQuotation"] = "DirectQuotation", ["zitat"] = "DirectQuotation",
            ["indirect"] = "IndirectQuotation", ["indirectQuotation"] = "IndirectQuotation", ["paraphrase"] = "IndirectQuotation",
            ["summary"] = "Summary", ["zusammenfassung"] = "Summary",
            ["comment"] = "Comment", ["kommentar"] = "Comment",
            ["quickReference"] = "QuickReference",
        };

        public static void Register(RpcDispatcher d)
        {
            d.Read("knowledgeItems.list",
                "Knowledge items, optionally filtered. params: referenceId?, categoryPath?, query?, limit=50",
                (ctx, p) =>
                {
                    var referenceId = P.Str(p, "referenceId");
                    var items = referenceId != null
                        ? Cv.Items(Cv.Get(ctx.Reference(referenceId), "Quotations"))
                        : P.Bool(p, "includeThoughts", true) ? ctx.AllKnowledgeItems : ctx.References.SelectMany(r => Cv.Items(Cv.Get(r, "Quotations")));

                    var paths = P.CategoryPaths(p, "categoryPath");
                    if (paths.Count > 0)
                    {
                        var cat = ctx.RequireCategory(paths[0]);
                        items = items.Where(k => Cv.Contains(Cv.Get(k, "Categories"), cat));
                    }

                    var query = P.Str(p, "query");
                    if (query != null)
                    {
                        items = items.Where(k =>
                            ((Cv.GetString(k, "Text") ?? "") + " " + (Cv.GetString(k, "CoreStatement") ?? ""))
                            .IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0);
                    }

                    var list = items.ToList();
                    return new { total = list.Count, items = list.Take(P.Int(p, "limit", 50)).Select(Serializer.KnowledgeItem).ToList() };
                });

            d.Write("knowledgeItems.create",
                "Creates a knowledge item on a reference. params: referenceId, type (direct|indirect|summary|comment), text, coreStatement, pageRange, categories[], keywords[]",
                (ctx, p) =>
                {
                    var reference = ctx.Reference(P.Req(p, "referenceId"));
                    var typeName = P.Str(p, "type") ?? "direct";
                    if (TypeAliases.TryGetValue(typeName, out var mapped)) typeName = mapped;

                    var quotationTypeEnum = Cv.RequireType("SwissAcademic.Citavi.QuotationType");
                    object quotationType;
                    try { quotationType = Enum.Parse(quotationTypeEnum, typeName, true); }
                    catch { throw new BridgeException("bad_request", $"Unknown knowledge item type '{typeName}'. Allowed: {string.Join(", ", Enum.GetNames(quotationTypeEnum))}"); }

                    var kiType = Cv.RequireType("SwissAcademic.Citavi.KnowledgeItem");
                    var ki = Cv.CreateFirst(kiType, new[] { reference, quotationType }, new[] { reference });
                    if (Cv.Has(ki, "QuotationType") && !Equals(Cv.Get(ki, "QuotationType"), quotationType))
                        kiType.GetProperty("QuotationType")?.SetValue(ki, quotationType, null);

                    ki = Cv.AddTo(Cv.Get(reference, "Quotations"), ki);
                    var warnings = Apply(ctx, ki, p);
                    return new { knowledgeItem = Serializer.KnowledgeItem(ki), warnings };
                });

            d.Write("knowledgeItems.update",
                "Updates a knowledge item. params: id, text?, coreStatement?, pageRange?, categories[] (added), keywords[] (added)",
                (ctx, p) =>
                {
                    var ki = ctx.KnowledgeItem(P.Req(p, "id"));
                    var warnings = Apply(ctx, ki, p);
                    return new { knowledgeItem = Serializer.KnowledgeItem(ki), warnings };
                });
        }

        internal static List<string> Apply(CitaviContext ctx, object ki, JObject p)
        {
            var warnings = new List<string>();
            foreach (var (param, prop) in new[] { ("text", "Text"), ("coreStatement", "CoreStatement"), ("pageRange", "PageRange") })
            {
                var v = P.Str(p, param);
                if (v == null) continue;
                try { Cv.SetFromString(ki, prop, v); }
                catch (Exception ex) { warnings.Add($"{prop}: {ex.GetBaseException().Message}"); }
            }

            foreach (var path in P.CategoryPaths(p, "categories"))
            {
                try
                {
                    var c = ctx.FindOrCreateCategory(path, out _);
                    var col = Cv.Get(ki, "Categories");
                    if (!Cv.Contains(col, c)) Cv.AddTo(col, c);
                }
                catch (Exception ex) { warnings.Add($"Category {string.Join(" > ", path)}: {ex.GetBaseException().Message}"); }
            }

            foreach (var kw in P.StrList(p, "keywords"))
            {
                try
                {
                    var k = ctx.FindOrCreateKeyword(kw);
                    var col = Cv.Get(ki, "Keywords");
                    if (!Cv.Contains(col, k)) Cv.AddTo(col, k);
                }
                catch (Exception ex) { warnings.Add($"Keyword {kw}: {ex.GetBaseException().Message}"); }
            }

            foreach (var g in P.StrList(p, "groups"))
            {
                try
                {
                    var group = ctx.FindOrCreateGroup(g);
                    var col = Cv.Get(ki, "Groups");
                    if (!Cv.Contains(col, group)) Cv.AddTo(col, group);
                }
                catch (Exception ex) { warnings.Add($"Group {g}: {ex.GetBaseException().Message}"); }
            }

            var relevance = P.Str(p, "relevance");
            if (relevance != null)
            {
                try { Cv.SetFromString(ki, "Relevance", relevance); }
                catch (Exception ex) { warnings.Add("Relevance: " + ex.GetBaseException().Message); }
            }
            return warnings;
        }
    }
}
