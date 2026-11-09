using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Newtonsoft.Json.Linq;
using SwissAcademic.Citavi.Shell;

namespace ClaudeCitavi.Bridge
{
    /// <summary>
    /// Project-independent operations: list known projects (the start window's list),
    /// open a project by name, auto-open projects at start-up. These work while only the
    /// Citavi start window is shown, so nobody has to click in Citavi (remote use).
    /// </summary>
    internal static class Projects
    {
        private static Type ProgramType => Cv.RequireType("SwissAcademic.Citavi.Shell.Program");

        /// <summary>Program.Engine.Settings.General.KnownProjects (resolved defensively across versions).</summary>
        public static IEnumerable<object> KnownProjects()
        {
            var engine = Cv.StaticValue(ProgramType, "Engine");
            var settings = engine == null ? null : Cv.Get(engine, "Settings");
            object general = null;
            if (settings != null)
            {
                general = Cv.Has(settings, "General") ? Cv.Get(settings, "General") : null;
                if (general == null)
                {
                    var prop = settings.GetType().GetProperties()
                        .FirstOrDefault(p => p.PropertyType.Name == "GeneralSettings" && p.GetIndexParameters().Length == 0);
                    general = prop?.GetValue(settings, null);
                }
            }
            return general == null ? Enumerable.Empty<object>() : Cv.Items(Cv.Get(general, "KnownProjects"));
        }

        public static string NameOf(MainForm form) => Cv.GetString(Cv.Get(form, "Project"), "Name");

        /// <summary>Runs on the UI thread.</summary>
        public static object List(Func<IList<MainForm>> openForms, Func<MainForm> active)
        {
            var forms = openForms();
            var activeForm = active();
            var open = forms.Select(f =>
            {
                var project = Cv.Get(f, "Project");
                return new
                {
                    name = Cv.GetString(project, "Name"),
                    type = Cv.Get(project, "ProjectType")?.ToString(),
                    isCloud = CitaviContext.IsCloudProject(project),
                    active = ReferenceEquals(f, activeForm),
                };
            }).ToList();

            var openNames = new HashSet<string>(open.Select(o => o.name ?? ""), StringComparer.OrdinalIgnoreCase);
            var known = KnownProjects().Select(k => new
            {
                name = Cv.GetString(k, "Name"),
                type = Cv.Get(k, "ProjectType")?.ToString(),
                isOpen = openNames.Contains(Cv.GetString(k, "Name") ?? ""),
            }).ToList();

            return new { open, known };
        }

        /// <summary>
        /// Starts opening a known project on the UI thread and returns a continuation that waits
        /// (off the UI thread) until the project window is there.
        /// </summary>
        public static object Open(JObject p, Func<IList<MainForm>> openForms)
        {
            var name = P.Req(p, "name");
            var timeout = TimeSpan.FromSeconds(P.Int(p, "timeoutSeconds", 180));

            if (openForms().Any(f => string.Equals(NameOf(f), name, StringComparison.OrdinalIgnoreCase)))
                return new { name, opened = false, alreadyOpen = true };

            var known = KnownProjects().ToList();
            var match = known.FirstOrDefault(k => string.Equals(Cv.GetString(k, "Name"), name, StringComparison.OrdinalIgnoreCase))
                        ?? throw new BridgeException("not_found",
                            $"Project '{name}' is not in Citavi's project list. Known: {string.Join(", ", known.Select(k => Cv.GetString(k, "Name")))}", 404);

            var connection = Cv.GetString(match, "ConnectionString")
                             ?? throw new BridgeException("api_mismatch", "Known project has no ConnectionString.", 500);

            var owner = (Cv.StaticValue(ProgramType, "StartForm") as Form) ?? Ui.Anchor() as Form;
            var method = ProgramType.GetMethods()
                .FirstOrDefault(m => m.Name == "OpenProjectAsync" && m.IsStatic &&
                                     m.GetParameters().Select(x => x.ParameterType).SequenceEqual(new[] { typeof(Form), typeof(string), typeof(CancellationToken) }))
                ?? throw new BridgeException("api_mismatch", "Program.OpenProjectAsync(Form, string, CancellationToken) not found.", 500);

            BridgeLog.Write($"Opening project '{name}'");
            var task = (Task)method.Invoke(null, new object[] { owner, connection, CancellationToken.None });

            return new Continuation(() =>
            {
                var deadline = DateTime.UtcNow + timeout;
                task.Wait(timeout);
                if (task.IsFaulted) throw CitaviContext.Wrap(task.Exception.GetBaseException(), "projects.open");

                // The window appears shortly after the task completes.
                while (DateTime.UtcNow < deadline)
                {
                    var found = (bool)Ui.Run(() => openForms().Any(f => string.Equals(NameOf(f), name, StringComparison.OrdinalIgnoreCase)), "projects.open");
                    if (found) return new { name, opened = true, alreadyOpen = false };
                    Thread.Sleep(500);
                }
                throw new BridgeException("timeout", $"Project '{name}' did not open within {timeout.TotalSeconds:0} s (dialog in Citavi?).", 504);
            });
        }
    }

    internal static class ProjectCreation
    {
        /// <summary>
        /// Creates a new project (default: cloud project) via Program.AddProjectAsync(owner, ProjectCreationOptions)
        /// and waits until its window is open. Runs the start on the UI thread, the waiting off it.
        /// </summary>
        public static object Create(JObject p, Func<IList<MainForm>> openForms)
        {
            var name = P.Req(p, "name");
            var kind = (P.Str(p, "type") ?? "cloud").ToLowerInvariant();
            if (Projects.KnownProjects().Any(k => string.Equals(Cv.GetString(k, "Name"), name, StringComparison.OrdinalIgnoreCase)))
                throw new BridgeException("exists", $"A project named '{name}' already exists in Citavi's project list.", 409);

            var programType = Cv.RequireType("SwissAcademic.Citavi.Shell.Program");
            var optionsType = Cv.RequireType("SwissAcademic.Citavi.ProjectCreationOptions");
            var projectTypeEnum = Cv.RequireType("SwissAcademic.Citavi.ProjectType");
            var options = Activator.CreateInstance(optionsType);
            optionsType.GetProperty("ProjectName").SetValue(options, name, null);
            optionsType.GetProperty("ProjectType").SetValue(options, Enum.Parse(projectTypeEnum, kind == "local" ? "DesktopSQLite" : "DesktopCloud"), null);
            optionsType.GetProperty("AddToKnownProjects").SetValue(options, true, null);

            var owner = (Cv.StaticValue(programType, "StartForm") as Form) ?? Ui.Anchor() as Form;
            var method = programType.GetMethods().FirstOrDefault(m => m.Name == "AddProjectAsync" && m.IsStatic &&
                                                                   m.GetParameters().Length == 2 && m.GetParameters()[1].ParameterType == optionsType)
                         ?? throw new BridgeException("api_mismatch", "Program.AddProjectAsync(Form, ProjectCreationOptions) not found.", 500);
            BridgeLog.Write($"Creating {kind} project '{name}'");
            var task = (Task)method.Invoke(null, new[] { owner, options });
            var timeout = TimeSpan.FromSeconds(P.Int(p, "timeoutSeconds", 180));

            return new Continuation(() =>
            {
                var deadline = DateTime.UtcNow + timeout;
                task.Wait(timeout);
                if (task.IsFaulted) throw CitaviContext.Wrap(task.Exception.GetBaseException(), "projects.create");
                while (DateTime.UtcNow < deadline)
                {
                    var open = (bool)Ui.Run(() => openForms().Any(f => string.Equals(Projects.NameOf(f), name, StringComparison.OrdinalIgnoreCase)), "projects.create");
                    if (open) return new { name, created = true, type = kind };
                    Thread.Sleep(500);
                }
                throw new BridgeException("timeout", $"Project '{name}' was not opened within {timeout.TotalSeconds:0} s (dialog in Citavi?).", 504);
            });
        }
    }

    /// <summary>Opens the projects listed in bridge.json (autoOpenProjects) once Citavi is ready.</summary>
    internal static class AutoOpen
    {
        public static void Schedule(BridgeConfig config)
        {
            var names = config.AutoOpenProjects?.Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
            if (names == null || names.Count == 0) return;

            var thread = new Thread(() => Run(names)) { IsBackground = true, Name = "ClaudeCitavi.AutoOpen" };
            thread.Start();
        }

        private static void Run(IList<string> names)
        {
            try
            {
                // Wait until Citavi shows a window and the engine is ready.
                var deadline = DateTime.UtcNow.AddMinutes(5);
                while (DateTime.UtcNow < deadline && !Ready()) Thread.Sleep(1000);
                Thread.Sleep(3000);

                foreach (var name in names)
                {
                    try
                    {
                        var result = Ui.Run(() => Projects.Open(new JObject { ["name"] = name }, Bootstrap.GetOpenForms), "autoOpen");
                        while (result is Continuation c) result = c.Next();
                        BridgeLog.Write($"Auto-open '{name}': {JObject.FromObject(result).ToString(Newtonsoft.Json.Formatting.None)}");
                    }
                    catch (Exception ex)
                    {
                        BridgeLog.Write($"Auto-open '{name}' failed: {ex.GetBaseException().Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                BridgeLog.Write("Auto-open failed: " + ex);
            }
        }

        private static bool Ready()
        {
            try
            {
                if (!Ui.Available) return false;
                return (bool)Ui.Run(() =>
                {
                    var program = Cv.FindType("SwissAcademic.Citavi.Shell.Program");
                    return program != null && Cv.StaticValue(program, "Engine") != null &&
                           (Cv.StaticValue(program, "StartForm") != null || Bootstrap.GetOpenForms().Count > 0);
                }, "autoOpen.ready");
            }
            catch
            {
                return false;
            }
        }
    }
}
