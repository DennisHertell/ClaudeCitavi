using System;
using System.Collections.Generic;
using System.Linq;

namespace ClaudeCitavi.Bridge
{
    /// <summary>Turns Citavi entities into plain dictionaries for JSON.</summary>
    internal static class Serializer
    {
        /// <summary>Simple text fields of a reference that can be read and written by name.</summary>
        public static readonly string[] ReferenceTextFields =
        {
            "Title", "Subtitle", "TitleSupplement", "ShortTitle", "Year", "Date", "Edition", "Volume", "Number",
            "NumberOfVolumes", "PageRange", "PageCount", "PlaceOfPublication", "Doi", "Isbn", "PubMedId",
            "OnlineAddress", "Language", "SeriesTitle", "Notes", "Evaluation", "TableOfContents",
            "BibTeXKey", "CitationKey", "AccessDate", "Organizations", "UniformTitle", "Specifics",
            "Rating", "HasLabel1", "HasLabel2",
            "CustomField1", "CustomField2", "CustomField3", "CustomField4", "CustomField5",
            "CustomField6", "CustomField7", "CustomField8", "CustomField9",
        };

        private static Dictionary<object, string> _typeKeys;

        /// <summary>Static property name of the reference type (e.g. "JournalArticle"), usable in references.create.</summary>
        public static string ReferenceTypeKey(object referenceType)
        {
            if (referenceType == null) return null;
            if (_typeKeys == null)
            {
                var map = new Dictionary<object, string>();
                foreach (var kv in Cv.StaticValuesOfOwnType(referenceType.GetType()))
                    if (kv.Value != null && !map.ContainsKey(kv.Value)) map[kv.Value] = kv.Key;
                _typeKeys = map;
            }
            return _typeKeys.TryGetValue(referenceType, out var key) ? key : null;
        }

        /// <summary>PDF highlights linked to a knowledge item (for copying between projects).</summary>
        public static List<Dictionary<string, object>> Highlights(object k)
        {
            var result = new List<Dictionary<string, object>>();
            if (!Cv.Has(k, "EntityLinks")) return result;
            foreach (var link in Cv.Items(Cv.Get(k, "EntityLinks")))
            {
                if (!ReferenceEquals(Cv.Get(link, "Source"), k)) continue;
                var annotation = Cv.Get(link, "Target");
                if (annotation == null || annotation.GetType().Name != "Annotation") continue;
                var quads = Cv.Items(Cv.Get(annotation, "Quads")).Select(q => new object[]
                {
                    Cv.Get(q, "PageIndex"), Cv.Get(q, "X1"), Cv.Get(q, "Y1"), Cv.Get(q, "X2"), Cv.Get(q, "Y2"),
                }).ToList();
                var color = Cv.Get(annotation, "OriginalColor") is System.Drawing.Color c ? System.Drawing.ColorTranslator.ToHtml(c) : null;
                result.Add(new Dictionary<string, object>
                {
                    ["annotationId"] = Cv.Get(annotation, "Id")?.ToString(),
                    ["locationId"] = Cv.Get(Cv.Get(annotation, "Location"), "Id")?.ToString(),
                    ["indication"] = Cv.GetString(link, "Indication"),
                    ["color"] = color,
                    ["quads"] = quads,
                });
            }
            return result;
        }

        public static Dictionary<string, object> ReferenceSummary(object r) => new Dictionary<string, object>
        {
            ["id"] = Cv.Get(r, "Id")?.ToString(),
            ["type"] = Cv.GetString(Cv.Get(r, "ReferenceType"), "Name") ?? Cv.Get(r, "ReferenceType")?.ToString(),
            ["typeKey"] = ReferenceTypeKey(Cv.Get(r, "ReferenceType")),
            ["isbn"] = Cv.GetString(r, "Isbn"),
            ["shortTitle"] = Cv.GetString(r, "ShortTitle"),
            ["title"] = Cv.GetString(r, "Title"),
            ["year"] = Cv.GetString(r, "Year") ?? Cv.GetString(r, "YearResolved"),
            ["authors"] = Persons(Cv.Get(r, "Authors")),
            ["doi"] = Cv.GetString(r, "Doi"),
        };

        public static Dictionary<string, object> ReferenceDetail(object r)
        {
            var d = ReferenceSummary(r);
            var fields = new Dictionary<string, object>();
            foreach (var f in ReferenceTextFields)
            {
                if (!Cv.Has(r, f)) continue;
                var v = Cv.GetString(r, f);
                // Citavi stores "KnowledgeItem_Mirroring:<Field>" when a field is mirrored from a knowledge item.
                if (v != null && !v.StartsWith("KnowledgeItem_Mirroring:", StringComparison.Ordinal)) fields[f] = v;
            }
            d["fields"] = fields;
            d["editors"] = Persons(Cv.Get(r, "Editors"));
            d["periodical"] = Cv.GetString(Cv.Get(r, "Periodical"), "Name");
            d["publishers"] = Cv.Items(Cv.Get(r, "Publishers")).Select(x => Cv.GetString(x, "Name")).ToList();
            d["abstract"] = Cv.GetString(Cv.Get(r, "Abstract"), "Text") ?? Cv.GetString(r, "AbstractText");
            d["keywords"] = Cv.Items(Cv.Get(r, "Keywords")).Select(x => Cv.GetString(x, "Name")).ToList();
            d["categories"] = Cv.Items(Cv.Get(r, "Categories")).Select(c => string.Join(" > ", CitaviContext.CategoryPath(c))).ToList();
            d["groups"] = Cv.Items(Cv.Get(r, "Groups")).Select(x => Cv.GetString(x, "Name")).ToList();
            d["attachments"] = Attachments.ElectronicLocations(r).Select(l => Attachments.Describe(l, r).ToJson()).ToList();
            d["knowledgeItemCount"] = Cv.Items(Cv.Get(r, "Quotations")).Count();
            return d;
        }

        public static Dictionary<string, object> KnowledgeItem(object k) => new Dictionary<string, object>
        {
            ["id"] = Cv.Get(k, "Id")?.ToString(),
            ["type"] = Cv.Get(k, "QuotationType")?.ToString() ?? Cv.Get(k, "KnowledgeItemType")?.ToString(),
            ["referenceId"] = Cv.Get(Cv.Get(k, "Reference"), "Id")?.ToString(),
            ["coreStatement"] = Cv.GetString(k, "CoreStatement"),
            ["text"] = Cv.GetString(k, "Text"),
            ["pageRange"] = Cv.GetString(k, "PageRange"),
            ["categories"] = Cv.Items(Cv.Get(k, "Categories")).Select(c => string.Join(" > ", CitaviContext.CategoryPath(c))).ToList(),
            ["keywords"] = Cv.Items(Cv.Get(k, "Keywords")).Select(x => Cv.GetString(x, "Name")).ToList(),
            ["groups"] = Cv.Items(Cv.Get(k, "Groups")).Select(x => Cv.GetString(x, "Name")).ToList(),
            ["relevance"] = Cv.Get(k, "Relevance"),
            ["isThought"] = Cv.Get(k, "Reference") == null,
        };

        /// <summary>Knowledge item including its PDF highlights (quads), used for export and project merge.</summary>
        public static Dictionary<string, object> KnowledgeItemFull(object k)
        {
            var d = KnowledgeItem(k);
            d["highlights"] = Highlights(k);
            return d;
        }

        public static Dictionary<string, object> Task(object t) => new Dictionary<string, object>
        {
            ["id"] = Cv.Get(t, "Id")?.ToString(),
            ["name"] = Cv.GetString(t, "Name"),
            ["referenceId"] = Cv.Get(Cv.Get(t, "Reference"), "Id")?.ToString(),
            ["referenceShortTitle"] = Cv.GetString(Cv.Get(t, "Reference"), "ShortTitle"),
            ["state"] = Cv.Get(t, "State")?.ToString(),
            ["importance"] = Cv.Get(t, "Importance")?.ToString(),
            ["dueDate"] = Cv.Get(t, "DueDate") is DateTime due ? due.ToString("yyyy-MM-dd") : null,
            ["assignedTo"] = Cv.GetString(t, "AssignedTo"),
            ["notes"] = Cv.GetString(t, "Notes"),
        };

        public static Dictionary<string, object> CategoryTree(object c) => new Dictionary<string, object>
        {
            ["id"] = Cv.Get(c, "Id")?.ToString(),
            ["name"] = Cv.GetString(c, "Name"),
            ["classification"] = Cv.GetString(c, "Classification"),
            ["children"] = CitaviContext.Children(c).Select(CategoryTree).ToList(),
        };

        public static List<Dictionary<string, string>> Persons(object collection) =>
            Cv.Items(collection).Select(p => new Dictionary<string, string>
            {
                ["lastName"] = Cv.GetString(p, "LastName"),
                ["firstName"] = Cv.GetString(p, "FirstName"),
            }).ToList();
    }
}
