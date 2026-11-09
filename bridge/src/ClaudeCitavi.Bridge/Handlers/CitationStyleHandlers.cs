using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace ClaudeCitavi.Bridge.Handlers
{
    /// <summary>
    /// Citation style (Zitationsstil) of the project.
    ///
    /// Citavi 6 (verified): the style is stored per project and user in
    /// SwissAcademic.Citavi.Settings.ProjectUserSettings.CitationStyle and changed with
    /// ProjectUserSettings.ChangeCitationStyleAsync(KnownCitationStyle). All styles Citavi
    /// knows (built-in, downloaded, custom) are in CitaviEngine.CitationStyleManager.KnownCitationStyles.
    /// </summary>
    internal static class CitationStyleHandlers
    {
        private static readonly string[] InfoProperties = { "Name", "Title", "DisplayName", "Id", "Culture", "FileName", "FilePath", "FullPath", "Path", "Source", "IsCustom" };

        public static void Register(RpcDispatcher d)
        {
            d.Read("citationStyle.get", "Current citation style of the project.", (ctx, p) =>
            {
                var settings = UserSettings(ctx);
                var style = Cv.Get(settings, "CitationStyle");
                var info = Describe(style);
                info["id"] = info.ContainsKey("id") ? info["id"] : Cv.Get(settings, "CitationStyleId")?.ToString();
                return info;
            });

            d.Read("citationStyle.list",
                "Citation styles Citavi knows (built-in, downloaded, custom) plus *.ccs files in configured folders. params: query?, limit=200",
                (ctx, p) =>
                {
                    var query = P.Str(p, "query");
                    var known = KnownStyles(ctx)
                        .Select(Describe)
                        .Where(s => query == null || string.Join(" ", s.Values).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                        .Take(P.Int(p, "limit", 200))
                        .ToList();

                    var folders = StyleFolders(P.Str(p, "folder"));
                    var files = folders.Where(Directory.Exists)
                        .SelectMany(f => SafeFiles(f, "*.ccs"))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Select(f => new { name = Path.GetFileNameWithoutExtension(f), path = f })
                        .Where(f => query == null || f.name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                        .OrderBy(x => x.name)
                        .ToList();

                    return new
                    {
                        known,
                        files,
                        searchedFolders = folders.Select(f => new { path = f, exists = Directory.Exists(f) }).ToList(),
                    };
                });

            d.Write("citationStyle.set",
                "Sets the project's citation style. params: name (or part of it) or id, as listed by citationStyle.list",
                (ctx, p) =>
                {
                    var name = P.Str(p, "name");
                    var id = P.Str(p, "id");
                    if (name == null && id == null) throw new BridgeException("bad_request", "Pass 'name' or 'id'.");

                    var all = KnownStyles(ctx).ToList();
                    var matches = all.Where(k =>
                    {
                        var info = Describe(k);
                        if (id != null) return info.TryGetValue("id", out var v) && string.Equals(v, id, StringComparison.OrdinalIgnoreCase);
                        return info.Values.Any(v => string.Equals(v, name, StringComparison.OrdinalIgnoreCase));
                    }).ToList();
                    if (matches.Count == 0 && name != null)
                        matches = all.Where(k => Describe(k).Values.Any(v => v.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)).ToList();

                    if (matches.Count == 0) throw new BridgeException("not_found", $"No citation style matches '{name ?? id}'. See citationStyle.list.", 404);
                    if (matches.Count > 1)
                        throw new BridgeException("ambiguous", "Several styles match: " + string.Join("; ", matches.Take(10).Select(m => Label(Describe(m)))) + ". Be more specific or use the id.");

                    var settings = UserSettings(ctx);
                    var known = matches[0];
                    var change = settings.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                        .FirstOrDefault(m => m.Name == "ChangeCitationStyleAsync" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType.IsInstanceOfType(known))
                        ?? throw new BridgeException("api_mismatch", "ProjectUserSettings.ChangeCitationStyleAsync(KnownCitationStyle) not found.", 500);
                    var task = change.Invoke(settings, new[] { known }) as Task;

                    // Loading a style can take a moment (download/parse); wait off the UI thread.
                    return new Continuation(() =>
                    {
                        if (task != null && !task.Wait(TimeSpan.FromSeconds(60)))
                            throw new BridgeException("timeout", "Citavi did not finish changing the citation style within 60 s.", 504);
                        if (task?.Exception != null) throw task.Exception.GetBaseException();
                        return ctx.Ui(() => Describe(Cv.Get(UserSettings(ctx), "CitationStyle")));
                    });
                });
        }

        /// <summary>The project's ProjectUserSettings, found by type name on the project.</summary>
        private static object UserSettings(CitaviContext ctx)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var prop = ctx.Project.GetType().GetProperties(flags).FirstOrDefault(x => x.PropertyType.Name == "ProjectUserSettings" && x.GetIndexParameters().Length == 0);
            var settings = prop?.GetValue(ctx.Project, null);
            if (settings != null) return settings;

            var engine = Engine(ctx);
            prop = engine?.GetType().GetProperties(flags).FirstOrDefault(x => x.PropertyType.Name == "ProjectUserSettings" && x.GetIndexParameters().Length == 0);
            return prop?.GetValue(engine, null)
                   ?? throw new BridgeException("api_mismatch", "ProjectUserSettings not reachable from the project in this Citavi version.", 500);
        }

        private static object Engine(CitaviContext ctx)
        {
            var engine = Cv.Get(ctx.Project, "Engine");
            if (engine != null) return engine;
            var program = Cv.FindType("SwissAcademic.Citavi.Shell.Program");
            var prop = program?.GetProperties(BindingFlags.Public | BindingFlags.Static).FirstOrDefault(x => x.PropertyType.Name == "CitaviEngine");
            return prop?.GetValue(null, null);
        }

        private static IEnumerable<object> KnownStyles(CitaviContext ctx)
        {
            var manager = Cv.Get(Engine(ctx), "CitationStyleManager")
                          ?? throw new BridgeException("api_mismatch", "CitaviEngine.CitationStyleManager not found.", 500);
            return Cv.Items(Cv.Get(manager, "KnownCitationStyles"));
        }

        private static Dictionary<string, string> Describe(object style)
        {
            var d = new Dictionary<string, string>();
            if (style == null) return d;
            foreach (var name in InfoProperties)
            {
                var v = Cv.GetString(style, name);
                if (v != null) d[char.ToLowerInvariant(name[0]) + name.Substring(1)] = v;
            }
            if (!d.ContainsKey("name") && !d.ContainsKey("title")) d["name"] = style.ToString();
            return d;
        }

        private static string Label(Dictionary<string, string> info) =>
            (info.TryGetValue("name", out var n) ? n : info.TryGetValue("title", out var t) ? t : "?") +
            (info.TryGetValue("id", out var id) ? $" [{id}]" : "");

        private static List<string> StyleFolders(string extra)
        {
            var folders = new List<string>();
            if (extra != null) folders.Add(extra);
            if (BridgeConfig.Current?.CitationStyleFolders != null)
                folders.AddRange(BridgeConfig.Current.CitationStyleFolders.Select(Environment.ExpandEnvironmentVariables));
            var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            folders.Add(Path.Combine(docs, "Citavi 6", "Custom Citation Styles"));
            return folders.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static IEnumerable<string> SafeFiles(string folder, string pattern)
        {
            try { return Directory.EnumerateFiles(folder, pattern, SearchOption.AllDirectories).ToList(); }
            catch { return Enumerable.Empty<string>(); }
        }
    }
}
