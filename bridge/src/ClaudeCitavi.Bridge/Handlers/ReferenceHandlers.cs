using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace ClaudeCitavi.Bridge.Handlers
{
    internal static class ReferenceHandlers
    {
        public static void Register(RpcDispatcher d)
        {
            d.Read("references.types", "Available reference types (Dokumenttypen).", (ctx, p) =>
                Cv.StaticValuesOfOwnType(Cv.RequireType("SwissAcademic.Citavi.ReferenceType"))
                    .Select(kv => new { key = kv.Key, name = Cv.GetString(kv.Value, "Name") ?? kv.Value?.ToString() })
                    .ToList());

            d.Read("references.search",
                "Full-text search over title, short title, authors, DOI, ISBN, keywords. params: query, categoryPath?, limit=25",
                (ctx, p) =>
                {
                    var query = P.Str(p, "query");
                    var limit = P.Int(p, "limit", 25);
                    var categoryPaths = P.CategoryPaths(p, "categoryPath");
                    var category = categoryPaths.Count > 0 ? ctx.RequireCategory(categoryPaths[0]) : null;

                    var hits = ctx.References.Where(r => category == null || Cv.Contains(Cv.Get(r, "Categories"), category));
                    if (query != null)
                    {
                        var terms = query.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        hits = hits.Where(r =>
                        {
                            var haystack = SearchText(r);
                            return terms.All(t => haystack.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0);
                        });
                    }

                    var list = hits.ToList();
                    return new { total = list.Count, items = list.Take(limit).Select(Serializer.ReferenceSummary).ToList() };
                });

            d.Read("references.get", "Full details of one reference. params: id", (ctx, p) =>
                Serializer.ReferenceDetail(ctx.Reference(P.Req(p, "id"))));

            d.Write("references.create",
                "Creates a reference. params: type, fields{}, authors[], editors[], periodical, publishers[], abstract, keywords[], categories[]",
                (ctx, p) =>
                {
                    var refTypeType = Cv.RequireType("SwissAcademic.Citavi.ReferenceType");
                    var typeName = P.Str(p, "type") ?? "JournalArticle";
                    var refType = Cv.StaticValue(refTypeType, typeName)
                                  ?? throw new BridgeException("bad_request", $"Unknown reference type '{typeName}'. See references.types.");

                    var fields = p["fields"] as JObject ?? new JObject();
                    var title = (string)fields["Title"] ?? P.Str(p, "title") ?? "";

                    var referenceType = Cv.RequireType("SwissAcademic.Citavi.Reference");
                    var reference = Cv.CreateFirst(referenceType,
                        new[] { ctx.Project, refType, (object)title },
                        new[] { ctx.Project, refType });

                    reference = Cv.AddTo(Cv.Get(ctx.Project, "References"), reference);

                    var warnings = Apply(ctx, reference, p, replaceLists: true);
                    return new { reference = Serializer.ReferenceDetail(reference), warnings };
                });

            d.Write("references.update",
                "Updates fields of a reference. Authors/editors are replaced when given, keywords/categories are added. params: id, fields{}, ...",
                (ctx, p) =>
                {
                    var reference = ctx.Reference(P.Req(p, "id"));
                    var warnings = Apply(ctx, reference, p, replaceLists: true);
                    return new { reference = Serializer.ReferenceDetail(reference), warnings };
                });
        }

        private static string SearchText(object r)
        {
            var parts = new List<string>
            {
                Cv.GetString(r, "Title"), Cv.GetString(r, "Subtitle"), Cv.GetString(r, "ShortTitle"),
                Cv.GetString(r, "Year"), Cv.GetString(r, "Doi"), Cv.GetString(r, "Isbn"), Cv.GetString(r, "BibTeXKey"),
                Cv.GetString(Cv.Get(r, "Periodical"), "Name"),
            };
            parts.AddRange(Cv.Items(Cv.Get(r, "Authors")).Select(a => Cv.GetString(a, "LastName")));
            parts.AddRange(Cv.Items(Cv.Get(r, "Keywords")).Select(k => Cv.GetString(k, "Name")));
            return string.Join(" | ", parts.Where(x => x != null));
        }

        /// <summary>Applies all supported parameters to a reference and collects non-fatal problems.</summary>
        internal static List<string> Apply(CitaviContext ctx, object reference, JObject p, bool replaceLists)
        {
            var warnings = new List<string>();

            if (p["fields"] is JObject fields)
            {
                foreach (var f in fields.Properties())
                {
                    try { Cv.SetFromString(reference, f.Name, f.Value.Type == JTokenType.Null ? null : f.Value.ToString()); }
                    catch (Exception ex) { warnings.Add($"Field {f.Name}: {(ex.GetBaseException()).Message}"); }
                }
            }

            ApplyPersons(ctx, reference, p, "authors", "Authors", replaceLists, warnings);
            ApplyPersons(ctx, reference, p, "editors", "Editors", replaceLists, warnings);

            var periodical = P.Str(p, "periodical");
            if (periodical != null)
            {
                try { reference.GetType().GetProperty("Periodical")?.SetValue(reference, ctx.FindOrCreatePeriodical(periodical), null); }
                catch (Exception ex) { warnings.Add("Periodical: " + ex.GetBaseException().Message); }
            }

            foreach (var publisher in P.StrList(p, "publishers"))
            {
                try
                {
                    var pub = ctx.FindOrCreatePublisher(publisher);
                    var col = Cv.Get(reference, "Publishers");
                    if (!Cv.Contains(col, pub)) Cv.AddTo(col, pub);
                }
                catch (Exception ex) { warnings.Add("Publisher: " + ex.GetBaseException().Message); }
            }

            var abstractText = P.Str(p, "abstract");
            if (abstractText != null)
            {
                try
                {
                    var abs = Cv.Get(reference, "Abstract");
                    if (abs != null && Cv.Has(abs, "Text")) Cv.SetFromString(abs, "Text", abstractText);
                    else Cv.SetFromString(reference, "Abstract", abstractText);
                }
                catch (Exception ex) { warnings.Add("Abstract: " + ex.GetBaseException().Message); }
            }

            foreach (var kw in P.StrList(p, "keywords"))
            {
                try
                {
                    var k = ctx.FindOrCreateKeyword(kw);
                    var col = Cv.Get(reference, "Keywords");
                    if (!Cv.Contains(col, k)) Cv.AddTo(col, k);
                }
                catch (Exception ex) { warnings.Add($"Keyword {kw}: {ex.GetBaseException().Message}"); }
            }

            foreach (var g in P.StrList(p, "groups"))
            {
                try
                {
                    var group = ctx.FindOrCreateGroup(g);
                    var col = Cv.Get(reference, "Groups");
                    if (!Cv.Contains(col, group)) Cv.AddTo(col, group);
                }
                catch (Exception ex) { warnings.Add($"Group {g}: {ex.GetBaseException().Message}"); }
            }

            foreach (var path in P.CategoryPaths(p, "categories"))
            {
                try
                {
                    var c = ctx.FindOrCreateCategory(path, out _);
                    var col = Cv.Get(reference, "Categories");
                    if (!Cv.Contains(col, c)) Cv.AddTo(col, c);
                }
                catch (Exception ex) { warnings.Add($"Category {string.Join(" > ", path)}: {ex.GetBaseException().Message}"); }
            }

            return warnings;
        }

        private static void ApplyPersons(CitaviContext ctx, object reference, JObject p, string param, string property, bool replace, List<string> warnings)
        {
            if (!(p[param] is JArray persons)) return;
            try
            {
                var col = Cv.Get(reference, property);
                if (replace)
                {
                    foreach (var existing in Cv.Items(col).ToList()) Cv.RemoveFrom(col, existing);
                }
                foreach (var entry in persons)
                {
                    string last, first;
                    if (entry is JObject o)
                    {
                        last = (string)o["lastName"];
                        first = (string)o["firstName"];
                    }
                    else
                    {
                        // "Nachname, Vorname" or "Vorname Nachname"
                        var s = ((string)entry ?? "").Trim();
                        if (s.Contains(","))
                        {
                            var parts = s.Split(new[] { ',' }, 2);
                            last = parts[0].Trim();
                            first = parts[1].Trim();
                        }
                        else
                        {
                            var idx = s.LastIndexOf(' ');
                            last = idx < 0 ? s : s.Substring(idx + 1);
                            first = idx < 0 ? null : s.Substring(0, idx);
                        }
                    }
                    if (string.IsNullOrWhiteSpace(last)) continue;
                    Cv.AddTo(col, ctx.FindOrCreatePerson(last, first));
                }
            }
            catch (Exception ex) { warnings.Add($"{property}: {ex.GetBaseException().Message}"); }
        }
    }
}
