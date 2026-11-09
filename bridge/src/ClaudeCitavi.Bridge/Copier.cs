using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;

namespace ClaudeCitavi.Bridge
{
    /// <summary>
    /// Copies references, knowledge items and attachments with Citavi's own clone engine
    /// (CitaviEntityCloneExtensions.CloneWithResults, the logic behind copy and paste and the
    /// copy-to-project wizard). Used for merging duplicates and for merging a shared project
    /// (e.g. a student's) into the user's project.
    /// </summary>
    internal static class Copier
    {
        private static Type Ext => Cv.RequireType("SwissAcademic.Citavi.CitaviEntityCloneExtensions");
        private static Type OptionsType => Cv.RequireType("SwissAcademic.Citavi.CitaviEntityCloneOptions");

        /// <summary>
        /// Options based on Citavi's CopyPaste preset. Categories are off by default: the target
        /// project keeps its own category system, items are sorted in afterwards.
        /// </summary>
        public static object Options(JObject p)
        {
            var preset = Cv.StaticValue(OptionsType, "CopyPaste");
            var options = Activator.CreateInstance(OptionsType);
            foreach (var prop in OptionsType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!prop.CanWrite || prop.GetIndexParameters().Length > 0 || prop.Name == "BuiltIn") continue;
                try { prop.SetValue(options, prop.GetValue(preset, null), null); } catch { /* read-only on some versions */ }
            }
            Set(options, "CloneReferenceCategories", P.Bool(p, "copyCategories", false));
            Set(options, "CloneKnowledgeItemCategories", P.Bool(p, "copyCategories", false));
            Set(options, "CloneReferenceKeywords", P.Bool(p, "copyKeywords", true));
            Set(options, "CloneKnowledgeItemKeywords", P.Bool(p, "copyKeywords", true));
            Set(options, "CloneReferenceGroups", P.Bool(p, "copyGroups", true));
            Set(options, "CloneKnowledgeItemGroups", P.Bool(p, "copyGroups", true));
            Set(options, "CloneReferenceTasks", P.Bool(p, "copyTasks", true));
            Set(options, "CloneLocations", P.Bool(p, "copyAttachments", true));
            Set(options, "CloneQuotations", P.Bool(p, "copyKnowledgeItems", true));
            Set(options, "ResolveEntityLinks", true);
            Set(options, "MatchByKey", true);
            return options;
        }

        private static void Set(object options, string name, bool value) =>
            OptionsType.GetProperty(name)?.SetValue(options, value, null);

        /// <summary>CloneWithResults(entity, newParent, options) -> (clone, original->clone map).</summary>
        public static Tuple<object, Dictionary<object, object>> Clone(object entity, object newParent, object options)
        {
            foreach (var m in Ext.GetMethods(BindingFlags.Public | BindingFlags.Static).Where(x => x.Name == "CloneWithResults" && x.GetParameters().Length == 3))
            {
                if (!m.GetParameters()[1].ParameterType.IsInstanceOfType(newParent)) continue;
                MethodInfo method = m;
                if (m.IsGenericMethodDefinition)
                {
                    try { method = m.MakeGenericMethod(entity.GetType()); }
                    catch (ArgumentException) { continue; } // generic constraints not met
                }
                if (!method.GetParameters()[0].ParameterType.IsInstanceOfType(entity)) continue;

                var tuple = method.Invoke(null, new[] { entity, newParent, options });
                var clone = tuple.GetType().GetField("Item1").GetValue(tuple);
                var map = new Dictionary<object, object>();
                if (tuple.GetType().GetField("Item2").GetValue(tuple) is IEnumerable results)
                {
                    foreach (var r in results)
                    {
                        var original = r.GetType().GetField("Item1").GetValue(r);
                        var copy = r.GetType().GetField("Item2").GetValue(r);
                        if (original != null && copy != null && !map.ContainsKey(original)) map[original] = copy;
                    }
                }
                if (!map.ContainsKey(entity)) map[entity] = clone;
                return Tuple.Create(clone, map);
            }
            throw new BridgeException("api_mismatch", $"No CloneWithResults overload for {entity.GetType().Name} -> {newParent.GetType().Name}.", 500);
        }

        /// <summary>Copies a whole reference into another project and adds it there.</summary>
        public static Tuple<object, Dictionary<object, object>> CopyReference(object reference, object targetProject, object options)
        {
            var result = Clone(reference, targetProject, options);
            var refs = Cv.Get(targetProject, "References");
            var clone = result.Item1;
            if (!Cv.Contains(refs, clone)) clone = Cv.AddTo(refs, clone);
            RemoveForeignLinks(clone, targetProject);
            return Tuple.Create(clone, result.Item2);
        }

        /// <summary>
        /// Copies knowledge items (and optionally missing attachments) of one reference onto another
        /// reference, in the same or another project. PDF highlights are re-linked: annotations that came
        /// along with a copied attachment are reused, otherwise the highlight is recreated from its quads
        /// on the target's PDF.
        /// </summary>
        public static List<object> CopyIntoReference(CitaviContext target, object sourceReference, object targetReference,
            IList<object> knowledgeItems, object options, bool copyMissingAttachments, Dictionary<object, object> idMap)
        {
            var report = new List<object>();

            // 1. Attachments the target does not have yet (same file name or address = present).
            var annotationMap = new Dictionary<object, object>();
            if (copyMissingAttachments)
            {
                var existing = new HashSet<string>(Attachments.ElectronicLocations(targetReference).Select(AttachmentKey), StringComparer.OrdinalIgnoreCase);
                foreach (var location in Attachments.ElectronicLocations(sourceReference).ToList())
                {
                    if (existing.Contains(AttachmentKey(location))) continue;
                    var res = Clone(location, targetReference, options);
                    var locClone = res.Item1;
                    var locations = Cv.Get(targetReference, "Locations");
                    if (!Cv.Contains(locations, locClone)) locClone = Cv.AddTo(locations, locClone);
                    foreach (var kv in res.Item2) annotationMap[kv.Key] = kv.Value;
                    idMap[location] = locClone;
                    report.Add(new { attachmentCopied = AttachmentKey(location) });
                }
            }

            // 2. Knowledge items.
            var quotations = Cv.Get(targetReference, "Quotations");
            foreach (var ki in knowledgeItems)
            {
                var res = Clone(ki, targetReference, options);
                var kiClone = res.Item1;
                if (!Cv.Contains(quotations, kiClone)) kiClone = Cv.AddTo(quotations, kiClone);
                idMap[ki] = kiClone;
                RemoveForeignLinks(kiClone, target.Project);

                // 3. Highlights of the original knowledge item.
                var relinked = 0;
                foreach (var link in Cv.Items(Cv.Get(ki, "EntityLinks")).ToList())
                {
                    var annotation = Cv.Get(link, "Target");
                    if (!ReferenceEquals(Cv.Get(link, "Source"), ki) || annotation?.GetType().Name != "Annotation") continue;
                    var indication = Cv.GetString(link, "Indication") ?? "PdfKnowledgeItem";

                    object targetAnnotation = annotationMap.TryGetValue(annotation, out var mapped) ? mapped : null;
                    if (targetAnnotation == null)
                    {
                        var pdf = Attachments.ElectronicLocations(targetReference)
                            .FirstOrDefault(l => (Cv.GetString(Cv.Get(l, "Address"), "OriginalString") ?? Cv.Get(l, "Address")?.ToString() ?? "")
                                .EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) || AttachmentKey(l).EndsWith(".pdf", StringComparison.OrdinalIgnoreCase));
                        if (pdf == null) { report.Add(new { knowledgeItem = Cv.GetString(ki, "CoreStatement"), highlight = "skipped: target has no PDF" }); continue; }
                        targetAnnotation = SameQuads(pdf, annotation) ?? CreateAnnotationLike(pdf, annotation);
                    }
                    if (!HasLink(kiClone, targetAnnotation)) LinkHighlight(target.Project, kiClone, targetAnnotation, indication);
                    relinked++;
                }
                report.Add(new { knowledgeItem = Cv.GetString(ki, "CoreStatement") ?? Cv.GetString(ki, "Text"), newId = Cv.Get(kiClone, "Id")?.ToString(), highlights = relinked });
            }
            return report;
        }

        internal static string AttachmentKey(object location)
        {
            var info = Attachments.Describe(location, Cv.Get(location, "Reference"));
            return info.FileName ?? Cv.Get(location, "Address")?.ToString() ?? "";
        }

        /// <summary>Drops entity links of a copy that still point into another project.</summary>
        private static void RemoveForeignLinks(object entity, object project)
        {
            if (!Cv.Has(entity, "EntityLinks")) return;
            foreach (var link in Cv.Items(Cv.Get(entity, "EntityLinks")).ToList())
            {
                var other = Cv.Get(link, "Target");
                var otherProject = other == null ? null : Cv.Get(other, "Project");
                if (otherProject != null && !ReferenceEquals(otherProject, project))
                    Cv.RemoveFrom(Cv.Get(project, "EntityLinks"), link);
            }
        }

        private static IList<double[]> QuadList(object annotation) =>
            Cv.Items(Cv.Get(annotation, "Quads")).Select(q => new[]
            {
                System.Convert.ToDouble(Cv.Get(q, "PageIndex")), System.Convert.ToDouble(Cv.Get(q, "X1")), System.Convert.ToDouble(Cv.Get(q, "Y1")),
                System.Convert.ToDouble(Cv.Get(q, "X2")), System.Convert.ToDouble(Cv.Get(q, "Y2")),
            }).ToList();

        private static object SameQuads(object location, object annotation)
        {
            var wanted = QuadList(annotation);
            foreach (var a in Cv.Items(Cv.Get(location, "Annotations")))
            {
                var have = QuadList(a);
                if (have.Count == wanted.Count && have.Zip(wanted, (x, y) => x.Zip(y, (u, v) => Math.Abs(u - v) < 0.5).All(b => b)).All(b => b))
                    return a;
            }
            return null;
        }

        private static object CreateAnnotationLike(object location, object annotation)
        {
            var annotationType = annotation.GetType();
            var created = Cv.Create(annotationType, location);
            var quadsProp = annotationType.GetProperty("Quads", BindingFlags.Public | BindingFlags.Instance);
            var quadType = quadsProp.PropertyType.GetGenericArguments().First();
            var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(quadType));
            foreach (var q in Cv.Items(Cv.Get(annotation, "Quads"))) list.Add(q); // Quad is a value type: copied as is
            quadsProp.SetValue(created, list, null);
            annotationType.GetProperty("OriginalColor")?.SetValue(created, Cv.Get(annotation, "OriginalColor"), null);
            annotationType.GetProperty("Visible")?.SetValue(created, true, null);
            return Cv.AddTo(Cv.Get(location, "Annotations"), created);
        }

        private static bool HasLink(object ki, object annotation) =>
            Cv.Items(Cv.Get(ki, "EntityLinks")).Any(l => ReferenceEquals(Cv.Get(l, "Source"), ki) && ReferenceEquals(Cv.Get(l, "Target"), annotation));

        private static void LinkHighlight(object project, object ki, object annotation, string indication)
        {
            var linkType = Cv.RequireType("SwissAcademic.Citavi.EntityLink");
            var link = Cv.Create(linkType, project);
            linkType.GetProperty("Source").SetValue(link, ki, null);
            linkType.GetProperty("Target").SetValue(link, annotation, null);
            linkType.GetProperty("Indication").SetValue(link, indication, null);
            Cv.AddTo(Cv.Get(project, "EntityLinks"), link);
        }
    }
}
