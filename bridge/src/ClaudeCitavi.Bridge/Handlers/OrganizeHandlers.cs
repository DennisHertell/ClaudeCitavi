using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace ClaudeCitavi.Bridge.Handlers
{
    /// <summary>
    /// Restructuring and housekeeping (verified API on Citavi 6.20):
    /// category tree (rename, move, reorder, merge, delete), groups, keywords, thoughts, tasks,
    /// import by identifier (DOI/ISBN/PMID/arXiv via Citavi's own lookup), duplicates, deletion, export.
    /// Destructive methods require "confirm": true.
    /// </summary>
    internal static class OrganizeHandlers
    {
        public static void Register(RpcDispatcher d)
        {
            RegisterCategories(d);
            RegisterNamed(d, "keywords", "Keywords", "SwissAcademic.Citavi.Keyword");
            RegisterNamed(d, "groups", "Groups", "SwissAcademic.Citavi.Group");
            RegisterThoughts(d);
            RegisterTasks(d);
            RegisterReferences(d);
        }

        private static void RequireConfirm(JObject p, string what)
        {
            if (!P.Bool(p, "confirm", false))
                throw new BridgeException("confirm_required", $"{what} cannot be undone from here. Repeat the call with confirm=true after the user agreed.", 409);
        }

        // ---- Category tree ---------------------------------------------------------------------

        private static object ParentCollection(CitaviContext ctx, object category)
        {
            var parent = Cv.Get(category, "ParentCategory");
            return parent != null ? Cv.Get(parent, "Categories") : Cv.Get(ctx.Project, "Categories");
        }

        private static object CollectionFor(CitaviContext ctx, List<string> parentPath) =>
            parentPath.Count == 0 ? Cv.Get(ctx.Project, "Categories") : Cv.Get(ctx.RequireCategory(parentPath), "Categories");

        /// <summary>Moves a category within its collection: after = null puts it first.</summary>
        private static void MoveWithin(object collection, object category, object after)
        {
            var move = collection.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == "Move" && m.GetParameters().Length == 2)
                ?? throw new BridgeException("api_mismatch", "CategoryHierarchyCollection.Move not found.", 500);
            move.Invoke(collection, new[] { category, after });
        }

        private static void Position(CitaviContext ctx, object collection, object category, JObject p)
        {
            var after = P.Str(p, "after");
            var position = P.Str(p, "position");
            var siblings = Cv.Items(collection).Where(c => !ReferenceEquals(c, category)).ToList();
            if (string.Equals(position, "first", StringComparison.OrdinalIgnoreCase)) MoveWithin(collection, category, null);
            else if (string.Equals(position, "last", StringComparison.OrdinalIgnoreCase)) { if (siblings.Count > 0) MoveWithin(collection, category, siblings.Last()); }
            else if (after != null)
            {
                var anchor = siblings.FirstOrDefault(c => string.Equals(Cv.GetString(c, "Name"), after, StringComparison.OrdinalIgnoreCase))
                             ?? throw new BridgeException("not_found", $"Sibling category '{after}' not found.", 404);
                MoveWithin(collection, category, anchor);
            }
        }

        private static void RegisterCategories(RpcDispatcher d)
        {
            d.Write("categories.rename", "Renames a category. params: path, newName", (ctx, p) =>
            {
                var cat = ctx.RequireCategory(P.CategoryPath(p["path"]));
                Cv.SetFromString(cat, "Name", P.Req(p, "newName"));
                return Serializer.CategoryTree(cat);
            });

            d.Write("categories.move",
                "Moves a category (with sub-categories, references and knowledge items) to another parent and/or position. " +
                "params: path, newParent? (path; [] or \"\" = top level; omitted = same parent), after? (sibling name) | position? (first|last)",
                (ctx, p) =>
                {
                    var path = P.CategoryPath(p["path"]);
                    var cat = ctx.RequireCategory(path);
                    var warnings = new List<string>();

                    if (p["newParent"] != null)
                    {
                        var newParentPath = P.CategoryPath(p["newParent"]);
                        if (newParentPath.Count >= path.Count && newParentPath.Take(path.Count).SequenceEqual(path, StringComparer.OrdinalIgnoreCase))
                            throw new BridgeException("bad_request", "A category cannot be moved into itself or its own sub-category.");

                        var currentParent = path.Take(path.Count - 1).ToList();
                        if (!currentParent.SequenceEqual(newParentPath, StringComparer.OrdinalIgnoreCase))
                        {
                            // Citavi has no re-parenting API: create the category under the new parent and merge the old one into it.
                            var target = CollectionFor(ctx, newParentPath);
                            var name = Cv.GetString(cat, "Name");
                            if (Cv.Items(target).Any(c => string.Equals(Cv.GetString(c, "Name"), name, StringComparison.OrdinalIgnoreCase)))
                                warnings.Add($"A category '{name}' already exists there; the two were merged.");
                            var moved = Cv.Call(target, "Add", name, true);
                            Cv.Call(moved, "Merge", cat);
                            cat = moved;
                        }
                    }

                    Position(ctx, ParentCollection(ctx, cat), cat, p);
                    return new { category = Serializer.CategoryTree(cat), path = CitaviContext.CategoryPath(cat), warnings };
                });

            d.Write("categories.reorder",
                "Sets the order of the sub-categories of a parent. Names not listed keep their relative order after the listed ones. params: parent? (path, empty = top level), order[] (names)",
                (ctx, p) =>
                {
                    var collection = CollectionFor(ctx, P.CategoryPath(p["parent"]));
                    var order = P.StrList(p, "order");
                    object previous = null;
                    foreach (var name in order)
                    {
                        var cat = Cv.Items(collection).FirstOrDefault(c => string.Equals(Cv.GetString(c, "Name"), name, StringComparison.OrdinalIgnoreCase))
                                  ?? throw new BridgeException("not_found", $"Category '{name}' not found under this parent.", 404);
                        MoveWithin(collection, cat, previous);
                        previous = cat;
                    }
                    return Cv.Items(collection).Select(c => Cv.GetString(c, "Name")).ToList();
                });

            d.Write("categories.sort", "Sorts the sub-categories of a parent alphabetically. params: parent? (path), recursive=false", (ctx, p) =>
            {
                var recursive = P.Bool(p, "recursive", false);
                var collection = CollectionFor(ctx, P.CategoryPath(p["parent"]));
                SortAlphabetically(collection, recursive);
                return Cv.Items(collection).Select(c => Cv.GetString(c, "Name")).ToList();
            });

            d.Write("categories.merge",
                "Merges one category into another: references, knowledge items and sub-categories move, the source disappears. params: source (path), target (path)",
                (ctx, p) =>
                {
                    var source = ctx.RequireCategory(P.CategoryPath(p["source"]));
                    var target = ctx.RequireCategory(P.CategoryPath(p["target"]));
                    if (ReferenceEquals(source, target)) throw new BridgeException("bad_request", "Source and target are the same category.");
                    Cv.Call(target, "Merge", source);
                    return new { target = Serializer.CategoryTree(target), path = CitaviContext.CategoryPath(target) };
                });

            d.Write("categories.delete",
                "Deletes a category. References/knowledge items only lose this category (they are not deleted). " +
                "params: path, mergeInto? (path: move contents there instead), confirm=true",
                (ctx, p) =>
                {
                    var cat = ctx.RequireCategory(P.CategoryPath(p["path"]));
                    var mergeInto = P.CategoryPath(p["mergeInto"]);
                    if (mergeInto.Count > 0)
                    {
                        var target = ctx.RequireCategory(mergeInto);
                        Cv.Call(target, "Merge", cat);
                        return new { deleted = true, mergedInto = CitaviContext.CategoryPath(target) };
                    }
                    RequireConfirm(p, "Deleting a category");
                    var counts = CategoryCounts(ctx, cat);
                    Cv.RemoveFrom(ParentCollection(ctx, cat), cat);
                    return new { deleted = true, unassignedReferences = counts.Item1, unassignedKnowledgeItems = counts.Item2 };
                });

            d.Read("categories.contents",
                "References and knowledge items assigned to a category. params: path, includeSubcategories=false, limit=200",
                (ctx, p) =>
                {
                    var cat = ctx.RequireCategory(P.CategoryPath(p["path"]));
                    var cats = new HashSet<object>(new[] { cat });
                    if (P.Bool(p, "includeSubcategories", false))
                        foreach (var c in Descendants(cat)) cats.Add(c);
                    var limit = P.Int(p, "limit", 200);
                    var refs = ctx.References.Where(r => Cv.Items(Cv.Get(r, "Categories")).Any(cats.Contains)).ToList();
                    var kis = ctx.AllKnowledgeItems.Where(k => Cv.Items(Cv.Get(k, "Categories")).Any(cats.Contains)).ToList();
                    return new
                    {
                        path = CitaviContext.CategoryPath(cat),
                        referenceCount = refs.Count,
                        knowledgeItemCount = kis.Count,
                        references = refs.Take(limit).Select(Serializer.ReferenceSummary).ToList(),
                        knowledgeItems = kis.Take(limit).Select(Serializer.KnowledgeItem).ToList(),
                    };
                });

            d.Write("categories.set",
                "Replaces the categories of references and/or knowledge items (re-sorting). params: categories[] (paths, created if missing), referenceIds[], knowledgeItemIds[]",
                (ctx, p) =>
                {
                    var cats = P.CategoryPaths(p, "categories").Select(path => ctx.FindOrCreateCategory(path, out _)).ToList();
                    var targets = P.StrList(p, "referenceIds").Select(ctx.Reference)
                        .Concat(P.StrList(p, "knowledgeItemIds").Select(ctx.KnowledgeItem)).ToList();
                    if (targets.Count == 0) throw new BridgeException("bad_request", "Pass referenceIds and/or knowledgeItemIds.");
                    foreach (var t in targets)
                    {
                        var col = Cv.Get(t, "Categories");
                        foreach (var existing in Cv.Items(col).ToList())
                            if (!cats.Contains(existing)) Cv.RemoveFrom(col, existing);
                        foreach (var c in cats)
                            if (!Cv.Contains(col, c)) Cv.AddTo(col, c);
                    }
                    return new { targets = targets.Count, categories = cats.Select(c => string.Join(" > ", CitaviContext.CategoryPath(c))).ToList() };
                });

            d.Read("categories.unassigned", "References and knowledge items without any category. params: limit=200", (ctx, p) =>
            {
                var limit = P.Int(p, "limit", 200);
                var refs = ctx.References.Where(r => !Cv.Items(Cv.Get(r, "Categories")).Any()).ToList();
                var kis = ctx.AllKnowledgeItems.Where(k => !Cv.Items(Cv.Get(k, "Categories")).Any()).ToList();
                return new
                {
                    referenceCount = refs.Count,
                    knowledgeItemCount = kis.Count,
                    references = refs.Take(limit).Select(Serializer.ReferenceSummary).ToList(),
                    knowledgeItems = kis.Take(limit).Select(Serializer.KnowledgeItem).ToList(),
                };
            });
        }

        private static IEnumerable<object> Descendants(object category)
        {
            foreach (var child in CitaviContext.Children(category))
            {
                yield return child;
                foreach (var grandChild in Descendants(child)) yield return grandChild;
            }
        }

        private static Tuple<int, int> CategoryCounts(CitaviContext ctx, object cat) => Tuple.Create(
            ctx.References.Count(r => Cv.Contains(Cv.Get(r, "Categories"), cat)),
            ctx.AllKnowledgeItems.Count(k => Cv.Contains(Cv.Get(k, "Categories"), cat)));

        private static void SortAlphabetically(object collection, bool recursive)
        {
            var sorted = Cv.Items(collection).OrderBy(c => Cv.GetString(c, "Name"), StringComparer.CurrentCultureIgnoreCase).ToList();
            object previous = null;
            foreach (var c in sorted)
            {
                MoveWithin(collection, c, previous);
                previous = c;
                if (recursive) SortAlphabetically(Cv.Get(c, "Categories"), true);
            }
        }

        // ---- Keywords and groups ---------------------------------------------------------------

        private static void RegisterNamed(RpcDispatcher d, string prefix, string collectionName, string typeName)
        {
            Func<CitaviContext, string, object> find = (ctx, name) =>
                Cv.Items(Cv.Get(ctx.Project, collectionName)).FirstOrDefault(x => string.Equals(Cv.GetString(x, "Name"), name, StringComparison.OrdinalIgnoreCase))
                ?? throw new BridgeException("not_found", $"'{name}' not found in {collectionName}.", 404);

            if (prefix == "groups")
            {
                d.Read("groups.list", "All groups of the project with usage counts.", (ctx, p) => UsageList(ctx, "Groups"));
                d.Write("groups.assign",
                    "Adds or removes groups on references and/or knowledge items (groups are created if missing). params: groups[], referenceIds[], knowledgeItemIds[], remove=false",
                    (ctx, p) =>
                    {
                        var remove = P.Bool(p, "remove", false);
                        var groups = P.StrList(p, "groups").Select(g => remove ? find(ctx, g) : ctx.FindOrCreateGroup(g)).ToList();
                        if (groups.Count == 0) throw new BridgeException("bad_request", "Nothing to assign.");
                        var targets = P.StrList(p, "referenceIds").Select(ctx.Reference)
                            .Concat(P.StrList(p, "knowledgeItemIds").Select(ctx.KnowledgeItem)).ToList();
                        if (targets.Count == 0) throw new BridgeException("bad_request", "Pass referenceIds and/or knowledgeItemIds.");
                        var changes = 0;
                        foreach (var t in targets)
                        {
                            var col = Cv.Get(t, "Groups");
                            foreach (var g in groups)
                            {
                                var has = Cv.Contains(col, g);
                                if (remove && has) { Cv.RemoveFrom(col, g); changes++; }
                                else if (!remove && !has) { Cv.AddTo(col, g); changes++; }
                            }
                        }
                        return new { targets = targets.Count, changes };
                    });
            }
            else
            {
                d.Read("keywords.usage", "All keywords with usage counts.", (ctx, p) => UsageList(ctx, "Keywords"));
            }

            d.Write($"{prefix}.rename", $"Renames an entry of {collectionName}. params: name, newName", (ctx, p) =>
            {
                var item = find(ctx, P.Req(p, "name"));
                Cv.SetFromString(item, "Name", P.Req(p, "newName"));
                return new { name = Cv.GetString(item, "Name") };
            });

            d.Write($"{prefix}.merge", $"Merges entries of {collectionName} into one (assignments move, sources disappear). params: sources[], target", (ctx, p) =>
            {
                var target = find(ctx, P.Req(p, "target"));
                var merged = new List<string>();
                foreach (var name in P.StrList(p, "sources"))
                {
                    var source = find(ctx, name);
                    if (ReferenceEquals(source, target)) continue;
                    MergeNamed(ctx, collectionName, source, target);
                    merged.Add(name);
                }
                return new { target = Cv.GetString(target, "Name"), merged };
            });

            d.Write($"{prefix}.delete", $"Deletes entries of {collectionName}; assignments are removed. params: names[], confirm=true", (ctx, p) =>
            {
                RequireConfirm(p, $"Deleting {collectionName}");
                var deleted = new List<string>();
                foreach (var name in P.StrList(p, "names"))
                {
                    Cv.RemoveFrom(Cv.Get(ctx.Project, collectionName), find(ctx, name));
                    deleted.Add(name);
                }
                return new { deleted };
            });
        }

        private static object UsageList(CitaviContext ctx, string collectionName)
        {
            var refs = ctx.References.ToList();
            var kis = ctx.AllKnowledgeItems.ToList();
            return Cv.Items(Cv.Get(ctx.Project, collectionName)).Select(x => new
            {
                name = Cv.GetString(x, "Name"),
                references = refs.Count(r => Cv.Contains(Cv.Get(r, collectionName), x)),
                knowledgeItems = kis.Count(k => Cv.Contains(Cv.Get(k, collectionName), x)),
            }).OrderBy(x => x.name).ToList();
        }

        /// <summary>Citavi's own Merge (Group.Merge, Keyword.Merge) when available, otherwise reassign by hand.</summary>
        private static void MergeNamed(CitaviContext ctx, string collectionName, object source, object target)
        {
            var merge = target.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == "Merge" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType.IsInstanceOfType(source));
            if (merge != null)
            {
                merge.Invoke(target, new[] { source });
                return;
            }
            foreach (var entity in ctx.References.Concat(ctx.AllKnowledgeItems))
            {
                var col = Cv.Get(entity, collectionName);
                if (col == null || !Cv.Contains(col, source)) continue;
                if (!Cv.Contains(col, target)) Cv.AddTo(col, target);
                Cv.RemoveFrom(col, source);
            }
            Cv.RemoveFrom(Cv.Get(ctx.Project, collectionName), source);
        }

        // ---- Thoughts (Gedanken) ---------------------------------------------------------------

        private static void RegisterThoughts(RpcDispatcher d)
        {
            d.Read("thoughts.list", "Thoughts (knowledge items without reference). params: query?, limit=100", (ctx, p) =>
            {
                var query = P.Str(p, "query");
                var items = ctx.Thoughts.Where(k => query == null ||
                    ((Cv.GetString(k, "Text") ?? "") + " " + (Cv.GetString(k, "CoreStatement") ?? "")).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                return new { total = items.Count, items = items.Take(P.Int(p, "limit", 100)).Select(Serializer.KnowledgeItem).ToList() };
            });

            d.Write("thoughts.create", "Creates a thought. params: coreStatement, text?, categories[], keywords[], groups[], relevance?", (ctx, p) =>
            {
                var thoughts = Cv.Get(ctx.Project, "Thoughts");
                var ki = Cv.Call(thoughts, "Add", P.Req(p, "coreStatement"));
                var warnings = KnowledgeHandlers.Apply(ctx, ki, p);
                return new { knowledgeItem = Serializer.KnowledgeItem(ki), warnings };
            });
        }

        // ---- Tasks (Aufgaben) ------------------------------------------------------------------

        private static void RegisterTasks(RpcDispatcher d)
        {
            d.Read("tasks.list", "Tasks of a reference or of the whole project. params: referenceId?, includeDone=false", (ctx, p) =>
            {
                var referenceId = P.Str(p, "referenceId");
                var tasks = referenceId != null
                    ? Cv.Items(Cv.Get(ctx.Reference(referenceId), "Tasks"))
                    : Cv.Items(Cv.Get(ctx.Project, "AllTaskItems"));
                var includeDone = P.Bool(p, "includeDone", false);
                return tasks.Where(t => includeDone || Cv.Get(t, "State")?.ToString() != "Done").Select(Serializer.Task).ToList();
            });

            d.Write("tasks.create",
                "Creates a task on a reference (or a project task without referenceId). params: name, referenceId?, dueDate? (yyyy-mm-dd), importance? (Low|Middle|High), state? (NotStarted|Started|Advanced|Done), assignedTo?, notes?",
                (ctx, p) =>
                {
                    var name = P.Req(p, "name");
                    var referenceId = P.Str(p, "referenceId");
                    object task;
                    if (referenceId != null) task = Cv.Call(Cv.Get(ctx.Reference(referenceId), "Tasks"), "Add", name);
                    else
                    {
                        task = Cv.Create(Cv.RequireType("SwissAcademic.Citavi.TaskItem"), ctx.Project, name);
                        task = Cv.AddTo(Cv.Get(ctx.Project, "Tasks"), task);
                    }
                    var warnings = ApplyTask(task, p);
                    return new { task = Serializer.Task(task), warnings };
                });

            d.Write("tasks.update", "Updates a task. params: id, name?, dueDate?, importance?, state?, assignedTo?, notes?", (ctx, p) =>
            {
                var task = FindTask(ctx, P.Req(p, "id"));
                var warnings = ApplyTask(task, p);
                return new { task = Serializer.Task(task), warnings };
            });

            d.Write("tasks.delete", "Deletes tasks. params: ids[], confirm=true", (ctx, p) =>
            {
                RequireConfirm(p, "Deleting tasks");
                var deleted = 0;
                foreach (var id in P.StrList(p, "ids"))
                {
                    var task = FindTask(ctx, id);
                    var reference = Cv.Get(task, "Reference");
                    Cv.RemoveFrom(reference != null ? Cv.Get(reference, "Tasks") : Cv.Get(ctx.Project, "Tasks"), task);
                    deleted++;
                }
                return new { deleted };
            });
        }

        private static object FindTask(CitaviContext ctx, string id)
        {
            if (!Guid.TryParse(id, out var guid)) throw new BridgeException("bad_request", $"'id' must be a GUID, got '{id}'.");
            return Cv.Items(Cv.Get(ctx.Project, "AllTaskItems")).FirstOrDefault(t => Equals(Cv.Get(t, "Id"), guid))
                   ?? throw new BridgeException("not_found", $"Task {id} not found.", 404);
        }

        private static List<string> ApplyTask(object task, JObject p)
        {
            var warnings = new List<string>();
            foreach (var (param, prop) in new[] { ("name", "Name"), ("dueDate", "DueDate"), ("importance", "Importance"), ("state", "State"), ("assignedTo", "AssignedTo"), ("notes", "Notes") })
            {
                var v = P.Str(p, param);
                if (v == null) continue;
                try { Cv.SetFromString(task, prop, v); }
                catch (Exception ex) { warnings.Add($"{prop}: {ex.GetBaseException().Message}"); }
            }
            return warnings;
        }

        // ---- References: identifier import, duplicates, merge, delete, export --------------------

        private static void RegisterReferences(RpcDispatcher d)
        {
            d.Write("references.importIdentifier",
                "Imports a reference via Citavi's own lookup (DOI, ISBN, PubMed ID, arXiv ID). Skips duplicates (same DOI/ISBN). " +
                "params: identifier, categories[], keywords[], groups[], allowDuplicate=false, timeoutSeconds=60",
                (ctx, p) =>
                {
                    var identifier = P.Req(p, "identifier").Trim();
                    if (!P.Bool(p, "allowDuplicate", false))
                    {
                        var existing = FindByIdentifier(ctx, identifier);
                        if (existing != null) return new { status = "exists", reference = Serializer.ReferenceDetail(existing) };
                    }

                    var ridType = Cv.RequireType("SwissAcademic.Citavi.DataExchange.ReferenceIdentifier");
                    var idTypeEnum = Cv.RequireType("SwissAcademic.Citavi.DataExchange.ReferenceIdentifierType");
                    var (typeName, value) = DetectIdentifier(identifier);
                    if (typeName == null)
                        throw new BridgeException("bad_request", $"'{identifier}' is not recognized as DOI, ISBN, PubMed ID or arXiv ID.");
                    var idType = Enum.Parse(idTypeEnum, typeName);
                    var rid = Cv.Create(ridType, value, idType);

                    var support = Cv.Create(Cv.RequireType("SwissAcademic.Citavi.DataExchange.ReferenceIdentifierSupport"));
                    var find = support.GetType().GetMethods().FirstOrDefault(m => m.Name == "FindReferenceAsync" && m.GetParameters().Length == 3)
                               ?? throw new BridgeException("api_mismatch", "ReferenceIdentifierSupport.FindReferenceAsync(project, identifier, token) not found.", 500);
                    var timeout = TimeSpan.FromSeconds(P.Int(p, "timeoutSeconds", 60));
                    var task = (Task)find.Invoke(support, new[] { ctx.Project, rid, CancellationToken.None });

                    return new Continuation(() =>
                    {
                        if (!task.Wait(timeout)) throw new BridgeException("timeout", $"Lookup of {identifier} timed out.", 504);
                        if (task.IsFaulted) throw new BridgeException("lookup_failed", task.Exception.GetBaseException().Message, 502);
                        var found = task.GetType().GetProperty("Result")?.GetValue(task, null);
                        if (found == null) throw new BridgeException("not_found", $"No record found for {identifier} ({idType}).", 404);

                        return ctx.Ui(() =>
                        {
                            var refs = Cv.Get(ctx.Project, "References");
                            var reference = Cv.Contains(refs, found) ? found : Cv.AddTo(refs, found);
                            var warnings = ReferenceHandlers.Apply(ctx, reference, p, replaceLists: false);
                            return (object)new { status = "created", identifierType = idType.ToString(), reference = Serializer.ReferenceDetail(reference), warnings };
                        });
                    });
                });

            d.Read("references.duplicates",
                "Finds duplicate references in the project: same DOI, same ISBN, same normalized title+year, plus Citavi's own duplicate comparer. params: useCitaviComparer=true",
                (ctx, p) =>
                {
                    var refs = ctx.References.ToList();
                    var groups = new UnionFind(refs.Count);
                    var reasons = new List<Tuple<int, int, string>>();
                    void Link(int a, int b, string why) { groups.Union(a, b); reasons.Add(Tuple.Create(a, b, why)); }

                    foreach (var key in new Func<object, string>[] { r => NormDoi(Cv.GetString(r, "Doi")), r => NormIsbn(Cv.GetString(r, "Isbn")), TitleKey })
                    {
                        var seen = new Dictionary<string, int>();
                        for (var i = 0; i < refs.Count; i++)
                        {
                            var k = key(refs[i]);
                            if (string.IsNullOrEmpty(k)) continue;
                            if (seen.TryGetValue(k, out var j)) Link(j, i, k.StartsWith("10.") ? "doi" : k.Contains("|") ? "title+year" : "isbn");
                            else seen[k] = i;
                        }
                    }

                    var comparerUsed = false;
                    if (P.Bool(p, "useCitaviComparer", true))
                    {
                        try
                        {
                            var refType = Cv.RequireType("SwissAcademic.Citavi.Reference");
                            var cache = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(refType));
                            var comparer = Cv.Create(Cv.RequireType("SwissAcademic.Citavi.ReferenceDuplicateComparer"), cache);
                            var index = new Dictionary<object, int>();
                            for (var i = 0; i < refs.Count; i++)
                            {
                                foreach (var dup in Cv.Items(Cv.Call(comparer, "GetDuplicatesInCache", refs[i], true)))
                                    if (index.TryGetValue(dup, out var j)) Link(j, i, "citavi");
                                index[refs[i]] = i;
                            }
                            comparerUsed = true;
                        }
                        catch (Exception ex)
                        {
                            BridgeLog.Write("Duplicate comparer failed: " + ex.GetBaseException().Message);
                        }
                    }

                    var result = groups.Groups().Where(g => g.Count > 1).Select(g => new
                    {
                        reasons = reasons.Where(r => g.Contains(r.Item1) && g.Contains(r.Item2)).Select(r => r.Item3).Distinct().ToList(),
                        references = g.Select(i => DuplicateInfo(refs[i])).ToList(),
                    }).ToList();
                    return new { groupCount = result.Count, citaviComparer = comparerUsed, groups = result };
                });

            d.Write("references.merge",
                "Merges duplicates into one reference with Citavi's MergeReference (fields, persons, categories, keywords, knowledge items, attachments), then deletes the duplicates. " +
                "params: keepId, duplicateIds[], overrideData=false (true: duplicate's fields win), confirm=true",
                (ctx, p) =>
                {
                    RequireConfirm(p, "Merging references");
                    var keep = ctx.Reference(P.Req(p, "keepId"));
                    var overrideData = P.Bool(p, "overrideData", false);
                    var merge = keep.GetType().GetMethods().FirstOrDefault(m => m.Name == "MergeReference" && m.GetParameters().Length == 3 &&
                                                                               typeof(IList).IsAssignableFrom(m.GetParameters()[2].ParameterType))
                                ?? throw new BridgeException("api_mismatch", "Reference.MergeReference(reference, bool, List) not found.", 500);
                    var omit = Activator.CreateInstance(merge.GetParameters()[2].ParameterType);
                    var options = Copier.Options(new JObject { ["copyCategories"] = true });
                    var merged = new List<object>();
                    foreach (var id in P.StrList(p, "duplicateIds"))
                    {
                        var dup = ctx.Reference(id);
                        if (ReferenceEquals(dup, keep)) continue;
                        var kis = Cv.Items(Cv.Get(dup, "Quotations")).ToList();
                        var kiCountBefore = Cv.Items(Cv.Get(keep, "Quotations")).Count();

                        // MergeReference merges fields, persons, categories, keywords and groups, but not
                        // knowledge items, attachments and tasks (verified on Citavi 6.20). Those are copied first.
                        var map = new Dictionary<object, object>();
                        var report = Copier.CopyIntoReference(ctx, dup, keep, kis, options, copyMissingAttachments: true, idMap: map);
                        foreach (var task in Cv.Items(Cv.Get(dup, "Tasks")).ToList())
                        {
                            var t = Copier.Clone(task, keep, options).Item1;
                            var tasks = Cv.Get(keep, "Tasks");
                            if (!Cv.Contains(tasks, t)) Cv.AddTo(tasks, t);
                        }
                        merge.Invoke(keep, new[] { dup, overrideData, omit });

                        var copied = Cv.Items(Cv.Get(keep, "Quotations")).Count() - kiCountBefore;
                        if (copied < kis.Count)
                        {
                            merged.Add(new { id, deleted = false, reason = $"only {copied} of {kis.Count} knowledge items arrived; duplicate kept for safety", report });
                            continue;
                        }
                        var refs = Cv.Get(ctx.Project, "References");
                        if (Cv.Contains(refs, dup)) Cv.RemoveFrom(refs, dup);
                        merged.Add(new { id, deleted = true, knowledgeItemsCopied = copied, report });
                    }
                    return new { reference = Serializer.ReferenceDetail(keep), merged };
                });

            d.Write("references.copyToProject",
                "Copies whole references (fields, persons, knowledge items with PDF highlights, attachments, tasks, keywords, groups) into another open project " +
                "with Citavi's clone engine. Categories are not copied by default (target keeps its own system). Returns old->new ids for references and knowledge items. " +
                "params: referenceIds[], targetProject, copyCategories=false, copyKeywords=true, copyGroups=true, copyTasks=true, copyAttachments=true",
                (ctx, p) =>
                {
                    var target = Bootstrap.ContextFor(P.Req(p, "targetProject"));
                    if (ReferenceEquals(target.Project, ctx.Project)) throw new BridgeException("bad_request", "Source and target project are the same.");
                    var options = Copier.Options(p);
                    var results = new List<object>();
                    foreach (var id in P.StrList(p, "referenceIds"))
                    {
                        var source = ctx.Reference(id);
                        var copy = Copier.CopyReference(source, target.Project, options);
                        results.Add(new
                        {
                            sourceId = id,
                            targetId = Cv.Get(copy.Item1, "Id")?.ToString(),
                            shortTitle = Cv.GetString(copy.Item1, "ShortTitle"),
                            knowledgeItems = copy.Item2.Where(kv => kv.Key.GetType().Name == "KnowledgeItem")
                                .Select(kv => new { sourceId = Cv.Get(kv.Key, "Id")?.ToString(), targetId = Cv.Get(kv.Value, "Id")?.ToString() }).ToList(),
                            attachments = Cv.Items(Cv.Get(copy.Item1, "Locations")).Count(),
                        });
                    }
                    return new { targetProject = Cv.GetString(target.Project, "Name"), copied = results };
                });

            d.Write("knowledgeItems.copyTo",
                "Copies knowledge items onto a reference (same or another open project) with Citavi's clone engine and re-links their PDF highlights. " +
                "Missing attachments of the source reference are copied along when copyAttachments=true. " +
                "params: knowledgeItemIds[], targetReferenceId, targetProject? (default: same project), copyCategories=false, copyKeywords=true, copyGroups=true, copyAttachments=true",
                (ctx, p) =>
                {
                    var targetName = P.Str(p, "targetProject");
                    var target = targetName == null ? ctx : Bootstrap.ContextFor(targetName);
                    var targetReference = target.Reference(P.Req(p, "targetReferenceId"));
                    var kis = P.StrList(p, "knowledgeItemIds").Select(ctx.KnowledgeItem).ToList();
                    if (kis.Count == 0) throw new BridgeException("bad_request", "Pass knowledgeItemIds.");
                    var groupsBySource = kis.GroupBy(k => Cv.Get(k, "Reference")).ToList();
                    var options = Copier.Options(p);
                    var map = new Dictionary<object, object>();
                    var report = new List<object>();
                    foreach (var g in groupsBySource)
                    {
                        if (g.Key == null) throw new BridgeException("bad_request", "Thoughts cannot be copied onto a reference; use thoughts.create.");
                        report.AddRange(Copier.CopyIntoReference(target, g.Key, targetReference, g.ToList(), options, P.Bool(p, "copyAttachments", true), map));
                    }
                    return new
                    {
                        targetReferenceId = Cv.Get(targetReference, "Id")?.ToString(),
                        knowledgeItems = kis.Select(k => new { sourceId = Cv.Get(k, "Id")?.ToString(), targetId = map.TryGetValue(k, out var c) ? Cv.Get(c, "Id")?.ToString() : null }).ToList(),
                        report,
                    };
                });

            d.Write("references.delete", "Deletes references including their knowledge items and attachments in the project. params: ids[], confirm=true", (ctx, p) =>
            {
                RequireConfirm(p, "Deleting references");
                var refs = Cv.Get(ctx.Project, "References");
                var deleted = new List<string>();
                foreach (var id in P.StrList(p, "ids"))
                {
                    var r = ctx.Reference(id);
                    deleted.Add(Cv.GetString(r, "ShortTitle") ?? id);
                    Cv.RemoveFrom(refs, r);
                }
                return new { deleted };
            });

            d.Write("knowledgeItems.delete", "Deletes knowledge items or thoughts. params: ids[], removeHighlights=true, confirm=true", (ctx, p) =>
            {
                RequireConfirm(p, "Deleting knowledge items");
                var removeHighlights = P.Bool(p, "removeHighlights", true);
                var deleted = 0;
                foreach (var id in P.StrList(p, "ids"))
                {
                    var ki = ctx.KnowledgeItem(id);
                    if (removeHighlights && Cv.Has(ki, "EntityLinks"))
                    {
                        foreach (var link in Cv.Items(Cv.Get(ki, "EntityLinks")).ToList())
                        {
                            var annotation = Cv.Get(link, "Target");
                            if (!ReferenceEquals(Cv.Get(link, "Source"), ki) || annotation?.GetType().Name != "Annotation") continue;
                            Cv.RemoveFrom(Cv.Get(ctx.Project, "EntityLinks"), link);
                            var location = Cv.Get(annotation, "Location");
                            if (location != null) Cv.RemoveFrom(Cv.Get(location, "Annotations"), annotation);
                        }
                    }
                    var reference = Cv.Get(ki, "Reference");
                    Cv.RemoveFrom(reference != null ? Cv.Get(reference, "Quotations") : Cv.Get(ctx.Project, "Thoughts"), ki);
                    deleted++;
                }
                return new { deleted };
            });

            d.Read("project.export",
                "Full export of references with knowledge items (incl. PDF highlight quads), thoughts and the category tree, for comparing and merging projects. " +
                "params: referenceIds[]?, includeKnowledgeItems=true",
                (ctx, p) =>
                {
                    var ids = new HashSet<string>(P.StrList(p, "referenceIds"), StringComparer.OrdinalIgnoreCase);
                    var withKi = P.Bool(p, "includeKnowledgeItems", true);
                    var refs = ctx.References.Where(r => ids.Count == 0 || ids.Contains(Cv.Get(r, "Id")?.ToString() ?? "")).ToList();
                    return new
                    {
                        project = Cv.GetString(ctx.Project, "Name"),
                        categories = ctx.RootCategories.Select(Serializer.CategoryTree).ToList(),
                        references = refs.Select(r =>
                        {
                            var detail = Serializer.ReferenceDetail(r);
                            if (withKi) detail["knowledgeItems"] = Cv.Items(Cv.Get(r, "Quotations")).Select(Serializer.KnowledgeItemFull).ToList();
                            return detail;
                        }).ToList(),
                        thoughts = withKi ? ctx.Thoughts.Select(Serializer.KnowledgeItemFull).ToList() : null,
                    };
                });
        }

        private static object DuplicateInfo(object r)
        {
            var d = Serializer.ReferenceSummary(r);
            d["isbn"] = Cv.GetString(r, "Isbn");
            d["knowledgeItemCount"] = Cv.Items(Cv.Get(r, "Quotations")).Count();
            d["attachmentCount"] = Cv.Items(Cv.Get(r, "Locations")).Count();
            d["categories"] = Cv.Items(Cv.Get(r, "Categories")).Select(c => string.Join(" > ", CitaviContext.CategoryPath(c))).ToList();
            d["createdOn"] = Cv.Get(r, "CreatedOn")?.ToString();
            return d;
        }

        private static object FindByIdentifier(CitaviContext ctx, string identifier)
        {
            var doi = NormDoi(identifier);
            var isbn = NormIsbn(identifier);
            return ctx.References.FirstOrDefault(r =>
                (!string.IsNullOrEmpty(doi) && NormDoi(Cv.GetString(r, "Doi")) == doi) ||
                (!string.IsNullOrEmpty(isbn) && NormIsbn(Cv.GetString(r, "Isbn")) == isbn));
        }

        /// <summary>Identifier type as Citavi's ReferenceIdentifierType name, plus the cleaned value.</summary>
        private static (string, string) DetectIdentifier(string input)
        {
            var s = input.Trim();
            var doi = NormDoi(s);
            if (doi != null) return ("Doi", doi);
            var arxiv = Regex.Match(s, @"(?:arxiv:)?(\d{4}\.\d{4,5}(?:v\d+)?)", RegexOptions.IgnoreCase);
            if (arxiv.Success && s.IndexOf("arxiv", StringComparison.OrdinalIgnoreCase) >= 0) return ("Arxiv", arxiv.Groups[1].Value);
            var isbn = NormIsbn(s);
            if (isbn != null) return ("Isbn", isbn);
            var pmid = Regex.Match(s, @"^(?:pmid:?\s*)?(\d{5,9})$", RegexOptions.IgnoreCase);
            if (pmid.Success) return ("PubMedId", pmid.Groups[1].Value);
            if (arxiv.Success) return ("Arxiv", arxiv.Groups[1].Value);
            return (null, null);
        }

        internal static string NormDoi(string doi)
        {
            if (string.IsNullOrWhiteSpace(doi)) return null;
            var m = Regex.Match(doi, @"10\.\d{4,9}/\S+", RegexOptions.IgnoreCase);
            return m.Success ? m.Value.TrimEnd('.', ',', ';').ToLowerInvariant() : null;
        }

        internal static string NormIsbn(string isbn)
        {
            if (string.IsNullOrWhiteSpace(isbn)) return null;
            var digits = new string(isbn.Where(c => char.IsDigit(c) || c == 'X' || c == 'x').ToArray()).ToUpperInvariant();
            if (digits.Length == 10) digits = Isbn10To13(digits);
            return digits.Length == 13 ? digits : null;
        }

        private static string Isbn10To13(string isbn10)
        {
            var core = "978" + isbn10.Substring(0, 9);
            var sum = 0;
            for (var i = 0; i < 12; i++) sum += (core[i] - '0') * (i % 2 == 0 ? 1 : 3);
            return core + ((10 - sum % 10) % 10);
        }

        private static string TitleKey(object r)
        {
            var title = Cv.GetString(r, "Title");
            if (string.IsNullOrWhiteSpace(title)) return null;
            var norm = new string(title.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
            if (norm.Length < 12) return null;
            return norm + "|" + (Cv.GetString(r, "Year") ?? "");
        }

        private sealed class UnionFind
        {
            private readonly int[] _parent;
            public UnionFind(int n) { _parent = Enumerable.Range(0, n).ToArray(); }
            private int Find(int x) => _parent[x] == x ? x : (_parent[x] = Find(_parent[x]));
            public void Union(int a, int b) { _parent[Find(a)] = Find(b); }
            public List<List<int>> Groups() => Enumerable.Range(0, _parent.Length).GroupBy(Find).Select(g => g.ToList()).ToList();
        }
    }
}
