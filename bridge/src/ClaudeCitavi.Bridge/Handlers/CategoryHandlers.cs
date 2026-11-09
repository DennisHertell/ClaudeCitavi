using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace ClaudeCitavi.Bridge.Handlers
{
    /// <summary>Categories (Kategoriensystem) and keywords (Schlagwörter).</summary>
    internal static class CategoryHandlers
    {
        public static void Register(RpcDispatcher d)
        {
            d.Read("categories.list", "Category tree of the project.", (ctx, p) =>
                ctx.RootCategories.Select(Serializer.CategoryTree).ToList());

            d.Write("categories.create", "Creates a category path (missing parents are created). params: path", (ctx, p) =>
            {
                var path = P.CategoryPath(p["path"]);
                if (path.Count == 0) throw new BridgeException("bad_request", "Parameter 'path' is required, e.g. \"Kapitel 2 > Methodik\".");
                var category = ctx.FindOrCreateCategory(path, out var created);
                return new { created, category = Serializer.CategoryTree(category) };
            });

            d.Write("categories.assign",
                "Adds or removes categories on references and/or knowledge items. params: categories[], referenceIds[], knowledgeItemIds[], remove=false, create=true",
                (ctx, p) => AssignMany(ctx, p, "Categories", path =>
                {
                    if (P.Bool(p, "create", true)) return ctx.FindOrCreateCategory(path, out _);
                    return ctx.RequireCategory(path);
                }, P.CategoryPaths(p, "categories")));

            d.Read("keywords.list", "All keywords of the project.", (ctx, p) =>
                Cv.Items(Cv.Get(ctx.Project, "Keywords")).Select(k => Cv.GetString(k, "Name")).OrderBy(x => x).ToList());

            d.Write("keywords.assign",
                "Adds or removes keywords on references and/or knowledge items. params: keywords[], referenceIds[], knowledgeItemIds[], remove=false",
                (ctx, p) => AssignMany(ctx, p, "Keywords", path => ctx.FindOrCreateKeyword(path[0]),
                    P.StrList(p, "keywords").Select(k => new List<string> { k }).ToList()));
        }

        private static object AssignMany(CitaviContext ctx, JObject p, string collectionProperty,
            Func<List<string>, object> resolve, List<List<string>> names)
        {
            if (names.Count == 0) throw new BridgeException("bad_request", "Nothing to assign.");
            var remove = P.Bool(p, "remove", false);

            var targets = P.StrList(p, "referenceIds").Select(ctx.Reference)
                .Concat(P.StrList(p, "knowledgeItemIds").Select(ctx.KnowledgeItem)).ToList();
            if (targets.Count == 0) throw new BridgeException("bad_request", "Pass referenceIds and/or knowledgeItemIds.");

            var entities = names.Select(resolve).ToList();
            var changes = 0;
            foreach (var target in targets)
            {
                var col = Cv.Get(target, collectionProperty);
                foreach (var e in entities)
                {
                    var has = Cv.Contains(col, e);
                    if (remove && has) { Cv.RemoveFrom(col, e); changes++; }
                    else if (!remove && !has) { Cv.AddTo(col, e); changes++; }
                }
            }
            return new { targets = targets.Count, changes };
        }
    }
}
