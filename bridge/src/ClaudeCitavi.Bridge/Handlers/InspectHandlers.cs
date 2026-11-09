using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace ClaudeCitavi.Bridge.Handlers
{
    /// <summary>
    /// Diagnostics: dumps the real object graph of a knowledge item or location, including
    /// entity links and PDF annotations. Used to learn how Citavi stores PDF highlights
    /// (undocumented) from a highlight the user created in Citavi's UI.
    /// </summary>
    internal static class InspectHandlers
    {
        // Types that are huge or cyclic; shown by name only when nested.
        private static readonly string[] Opaque = { "Project", "Reference", "ProjectShell", "CitaviEngine", "MainForm", "Library", "Periodical" };

        public static void Register(RpcDispatcher d)
        {
            d.Read("debug.inspect",
                "Dumps a knowledge item (incl. entity links and linked annotations) or a location (incl. annotations). " +
                "params: knowledgeItemId | locationId, depth=3",
                (ctx, p) =>
                {
                    var depth = P.Int(p, "depth", 3);
                    var kiId = P.Str(p, "knowledgeItemId");
                    var locId = P.Str(p, "locationId");
                    object target = kiId != null ? ctx.KnowledgeItem(kiId) : locId != null ? FindLocation(ctx, locId) : null;
                    if (target == null) throw new BridgeException("bad_request", "Pass knowledgeItemId or locationId.");

                    var links = Cv.Items(Cv.Get(ctx.Project, "EntityLinks"))
                        .Where(l => ReferenceEquals(Cv.Get(l, "Source"), target) || ReferenceEquals(Cv.Get(l, "Target"), target))
                        .ToList();

                    return new
                    {
                        type = target.GetType().FullName,
                        @object = Dump(target, depth),
                        entityLinks = links.Select(l => Dump(l, depth)).ToList(),
                        entityLinksCollectionType = Cv.Get(ctx.Project, "EntityLinks")?.GetType().FullName,
                    };
                });
        }

        private static object FindLocation(CitaviContext ctx, string id)
        {
            foreach (var r in ctx.References)
            foreach (var l in Cv.Items(Cv.Get(r, "Locations")))
                if (string.Equals(Cv.Get(l, "Id")?.ToString(), id, StringComparison.OrdinalIgnoreCase)) return l;
            throw new BridgeException("not_found", $"Location {id} not found.", 404);
        }

        internal static object Dump(object o, int depth)
        {
            if (o == null) return null;
            var t = o.GetType();
            if (o is string s) return s.Length > 300 ? s.Substring(0, 300) + "…" : s;
            if (t.IsPrimitive || t.IsEnum || o is Guid || o is DateTime || o is decimal) return o.ToString();
            if (depth <= 0 || Opaque.Contains(t.Name)) return $"<{t.Name}> {Truncate(SafeToString(o))}";

            if (o is IEnumerable e && !(o is IDictionary))
            {
                var items = new List<object>();
                var count = 0;
                foreach (var item in e)
                {
                    if (count++ < 10) items.Add(Dump(item, depth - 1));
                }
                return new Dictionary<string, object> { ["$type"] = t.Name, ["$count"] = count, ["items"] = items };
            }

            var d = new Dictionary<string, object> { ["$type"] = t.FullName };
            foreach (var prop in t.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(x => x.GetIndexParameters().Length == 0))
            {
                if (prop.Name.StartsWith("DataContract", StringComparison.Ordinal)) continue;
                try { d[prop.Name] = Dump(prop.GetValue(o, null), depth - 1); }
                catch (Exception ex) { d[prop.Name] = "!" + ex.GetBaseException().GetType().Name; }
            }
            return d;
        }

        private static string SafeToString(object o)
        {
            try { return o.ToString(); } catch { return o.GetType().Name; }
        }

        private static string Truncate(string s) => s == null ? null : s.Length > 120 ? s.Substring(0, 120) + "…" : s;
    }
}
