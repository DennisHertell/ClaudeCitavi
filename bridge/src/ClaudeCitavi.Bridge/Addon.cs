using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using SwissAcademic.Citavi.Shell;

namespace ClaudeCitavi.Bridge
{
    /// <summary>
    /// Entry point loaded by Citavi from its AddOns folder.
    /// The bridge starts as soon as Citavi instantiates the add-on, even if no project is open yet
    /// (start window only). Project windows are tracked as they appear, so Claude can open,
    /// list and work in projects without anybody touching the Citavi window.
    /// </summary>
    public class Addon : CitaviAddOn<MainForm>
    {
        public Addon()
        {
            // Citavi creates the add-on during start-up, before any project window exists.
            Bootstrap.Start("add-on loaded");
        }

        public override void OnHostingFormLoaded(MainForm mainForm)
        {
            base.OnHostingFormLoaded(mainForm);
            Bootstrap.Track(mainForm);
            Bootstrap.Start("project window loaded");
        }

        /// <summary>Kept for the macro entry point (machines without admin rights).</summary>
        internal static string Attach(MainForm mainForm, bool showErrors)
        {
            Bootstrap.Track(mainForm);
            return Bootstrap.Start("macro", showErrors ? mainForm : null);
        }

        internal static MainForm ActiveMainForm => Bootstrap.ActiveMainForm;

        internal static IList<MainForm> GetOpenForms() => Bootstrap.GetOpenForms();
    }

    /// <summary>Process-wide bridge state: server, tracked project windows, auto-open.</summary>
    internal static class Bootstrap
    {
        private static readonly object Sync = new object();
        private static readonly List<MainForm> Forms = new List<MainForm>();
        private static BridgeServer _server;
        private static MainForm _active;

        public static MainForm ActiveMainForm
        {
            get
            {
                var active = _active;
                if (active != null && !active.IsDisposed) return active;
                return GetOpenForms().LastOrDefault();
            }
        }

        public static void Track(MainForm form)
        {
            if (form == null) return;
            lock (Forms)
            {
                if (Forms.Contains(form)) { _active = form; return; }
                Forms.Add(form);
            }
            _active = form;
            form.Activated += (s, e) => _active = (MainForm)s;
            form.FormClosed += (s, e) =>
            {
                lock (Forms) Forms.Remove((MainForm)s);
                if (ReferenceEquals(_active, s)) _active = null;
            };
        }

        /// <summary>Tracked windows plus any MainForm Citavi opened without notifying the add-on.</summary>
        public static IList<MainForm> GetOpenForms()
        {
            foreach (var f in Ui.OpenForms().OfType<MainForm>()) Track(f);
            lock (Forms) return Forms.Where(f => !f.IsDisposed).ToList();
        }

        /// <summary>Context of an open project by name (UI thread). Used for copying between projects.</summary>
        public static CitaviContext ContextFor(string projectName)
        {
            var forms = GetOpenForms();
            var form = forms.FirstOrDefault(f => string.Equals(Projects.NameOf(f), projectName, StringComparison.OrdinalIgnoreCase))
                       ?? throw new BridgeException("not_found", $"Project '{projectName}' is not open. Open it with projects.open first. Open: {string.Join(", ", forms.Select(Projects.NameOf))}", 404);
            return new CitaviContext(form);
        }

        public static string Start(string reason, IWin32Window errorOwner = null)
        {
            lock (Sync)
            {
                if (_server != null) return $"ClaudeCitavi Bridge läuft bereits (Port {_server.Port}).";
                try
                {
                    Ui.CaptureContext();
                    var config = BridgeConfig.LoadOrCreate();
                    var server = new BridgeServer(config, new RpcDispatcher(() => ActiveMainForm, GetOpenForms));
                    server.Start();
                    _server = server;
                    Application.ApplicationExit += (s, e) => _server?.Stop();
                    BridgeLog.Write($"Bridge started ({reason}), version {typeof(Bootstrap).Assembly.GetName().Version}");
                    AutoOpen.Schedule(config);
                    return $"ClaudeCitavi Bridge gestartet (Port {config.Port}).";
                }
                catch (Exception ex)
                {
                    BridgeLog.Write("Bridge start failed: " + ex);
                    var message = "ClaudeCitavi Bridge konnte nicht gestartet werden:\n\n" + ex.Message +
                                  "\n\nDetails: " + BridgeLog.LogFilePath;
                    if (errorOwner != null)
                        MessageBox.Show(errorOwner, message, "ClaudeCitavi Bridge", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return message;
                }
            }
        }
    }

    /// <summary>
    /// Runs code on Citavi's UI thread without needing a project window:
    /// any open form (start window or project window) works as Invoke target.
    /// </summary>
    internal static class Ui
    {
        private static SynchronizationContext _context;

        public static void CaptureContext()
        {
            if (_context == null && SynchronizationContext.Current is WindowsFormsSynchronizationContext c) _context = c;
        }

        public static IList<Form> OpenForms()
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try { return Application.OpenForms.Cast<Form>().ToList(); }
                catch (InvalidOperationException) { Thread.Sleep(20); } // collection changed while enumerating
            }
            return new List<Form>();
        }

        public static Control Anchor() =>
            OpenForms().FirstOrDefault(f => !f.IsDisposed && f.IsHandleCreated);

        public static bool Available => Anchor() != null || _context != null;

        public static object Run(Func<object> func, string method)
        {
            var anchor = Anchor();
            if (anchor != null) return CitaviContext.RunOnUi(anchor, func, method);

            if (_context == null) throw new BridgeException("citavi_starting", "Citavi is still starting (no window yet).", 503);
            object result = null;
            Exception error = null;
            _context.Send(_ =>
            {
                try { result = func(); }
                catch (Exception ex) { error = ex; }
            }, null);
            if (error != null) throw CitaviContext.Wrap(error, method);
            return result;
        }
    }
}
