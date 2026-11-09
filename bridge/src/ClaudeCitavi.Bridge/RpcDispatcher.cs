using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using Newtonsoft.Json.Linq;
using SwissAcademic.Citavi.Shell;

namespace ClaudeCitavi.Bridge
{
    internal delegate object RpcHandler(CitaviContext ctx, JObject p);

    /// <summary>
    /// Returned by a handler that must wait for something (e.g. a cloud download)
    /// without blocking Citavi's UI thread. The dispatcher runs <see cref="Next"/>
    /// on the HTTP worker thread; it may hop back to the UI thread via CitaviContext.Ui
    /// and may return another Continuation.
    /// </summary>
    internal sealed class Continuation
    {
        public Func<object> Next { get; }
        public Continuation(Func<object> next) { Next = next; }
    }

    /// <summary>
    /// Routes RPC method names to handlers and runs every handler on Citavi's
    /// UI thread. Citavi's object model is not thread-safe; running on the UI
    /// thread also makes changes visible immediately and keeps undo/autosave intact.
    /// </summary>
    public sealed class RpcDispatcher
    {
        private readonly Func<MainForm> _activeForm;
        private readonly Func<IList<MainForm>> _openForms;
        private readonly Dictionary<string, (RpcHandler Handler, bool Writes, string Help)> _methods =
            new Dictionary<string, (RpcHandler, bool, string)>(StringComparer.OrdinalIgnoreCase);

        public RpcDispatcher(Func<MainForm> activeForm, Func<IList<MainForm>> openForms)
        {
            _activeForm = activeForm;
            _openForms = openForms;

            Handlers.SystemHandlers.Register(this);
            Handlers.ReferenceHandlers.Register(this);
            Handlers.CategoryHandlers.Register(this);
            Handlers.KnowledgeHandlers.Register(this);
            Handlers.AttachmentHandlers.Register(this);
            Handlers.CitationStyleHandlers.Register(this);
            Handlers.InspectHandlers.Register(this);
            Handlers.PdfHandlers.Register(this);
            Handlers.OrganizeHandlers.Register(this);
            Handlers.MoreHandlers.Register(this);
        }

        internal void Read(string name, string help, RpcHandler handler) => _methods[name] = (handler, false, help);
        internal void Write(string name, string help, RpcHandler handler) => _methods[name] = (handler, true, help);

        internal IEnumerable<object> Describe() =>
            _methods.OrderBy(m => m.Key).Select(m => new { method = m.Key, writes = m.Value.Writes, help = m.Value.Help });

        /// <param name="projectName">Optional: name of an open project; default is the last active window.</param>
        public object Execute(string method, JObject parameters, bool readOnly, string projectName = null)
        {
            // Project-independent methods: work while only Citavi's start window is open.
            if (string.Equals(method, "projects.list", StringComparison.OrdinalIgnoreCase))
                return Ui.Run(() => Projects.List(_openForms, _activeForm), method);
            if (string.Equals(method, "projects.open", StringComparison.OrdinalIgnoreCase))
            {
                if (readOnly) throw new BridgeException("read_only", "The bridge is configured as read-only (bridge.json: readOnly=true).", 403);
                return Drain(Ui.Run(() => Projects.Open(parameters, _openForms), method), method);
            }
            if (string.Equals(method, "projects.create", StringComparison.OrdinalIgnoreCase))
            {
                if (readOnly) throw new BridgeException("read_only", "The bridge is configured as read-only (bridge.json: readOnly=true).", 403);
                return Drain(Ui.Run(() => ProjectCreation.Create(parameters, _openForms), method), method);
            }
            if (string.Equals(method, "system.ping", StringComparison.OrdinalIgnoreCase) && _activeForm() == null)
                return Ui.Run(() => new
                {
                    bridgeVersion = typeof(RpcDispatcher).Assembly.GetName().Version.ToString(),
                    project = (object)null,
                    message = "Citavi läuft, aber kein Projekt ist geöffnet. projects.open öffnet eines.",
                    projects = Projects.List(_openForms, _activeForm),
                }, method);

            if (string.IsNullOrWhiteSpace(method) || !_methods.TryGetValue(method, out var entry))
                throw new BridgeException("unknown_method", $"Unknown method '{method}'. Call 'system.methods' for a list.", 404);

            if (entry.Writes && readOnly)
                throw new BridgeException("read_only", "The bridge is configured as read-only (bridge.json: readOnly=true).", 403);

            var form = SelectForm(projectName);
            object result = CitaviContext.RunOnUi(form, () => entry.Handler(new CitaviContext(form), parameters), method);

            return Drain(result, method);
        }

        /// <summary>Long-running work (downloads, opening projects) continues off the UI thread.</summary>
        private static object Drain(object result, string method)
        {
            while (result is Continuation c)
            {
                try { result = c.Next(); }
                catch (Exception ex) { throw CitaviContext.Wrap(ex, method); }
            }
            return result;
        }

        private MainForm SelectForm(string projectName)
        {
            if (projectName == null)
            {
                var active = _activeForm();
                if (active == null || active.IsDisposed)
                    throw new BridgeException("no_project", "No Citavi project window is open.", 409);
                return active;
            }

            var forms = _openForms().Where(f => !f.IsDisposed).ToList();
            if (forms.Count == 0) throw new BridgeException("no_project", "No Citavi project window is open.", 409);
            var names = (List<string>)CitaviContext.RunOnUi(forms[0],
                () => forms.Select(f => Cv.GetString(Cv.Get(f, "Project"), "Name") ?? "").ToList(), "projects.select");
            var index = names.FindIndex(n => string.Equals(n, projectName, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
                throw new BridgeException("not_found", $"Project '{projectName}' is not open. Open projects: {string.Join(", ", names)}", 404);
            return forms[index];
        }
    }

    /// <summary>Access to the open project plus lookup helpers shared by all handlers.</summary>
    internal sealed class CitaviContext
    {
        public MainForm Form { get; }
        public object Project { get; }

        public CitaviContext(MainForm form)
        {
            Form = form;
            Project = Cv.Get(form, "Project") ?? throw new BridgeException("no_project", "No Citavi project is loaded.", 409);
        }

        /// <summary>Runs code on Citavi's UI thread (needed for every object-model access from a continuation).</summary>
        public T Ui<T>(Func<T> func) => (T)RunOnUi(Form, () => func(), "ui");

        public bool IsCloud => IsCloudProject(Project);

        public static bool IsCloudProject(object project)
        {
            var type = Cv.Get(project, "ProjectType")?.ToString() ?? "";
            return type.IndexOf("Cloud", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        internal static object RunOnUi(Control control, Func<object> func, string method)
        {
            object result = null;
            Exception error = null;
            Action run = () =>
            {
                try { result = func(); }
                catch (Exception ex) { error = ex; }
            };

            if (control.InvokeRequired) control.Invoke(run);
            else run();

            if (error != null) throw Wrap(error, method);
            return result;
        }

        internal static Exception Wrap(Exception error, string method)
        {
            var inner = error is System.Reflection.TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : error;
            if (inner is BridgeException) return inner;
            BridgeLog.Write($"{method} failed: {inner}");
            return new BridgeException("citavi_error", inner.GetType().Name + ": " + inner.Message, 500);
        }

        public IEnumerable<object> References => Cv.Items(Cv.Get(Project, "References"));

        public object Reference(string id)
        {
            var guid = ParseGuid(id, "referenceId");
            return References.FirstOrDefault(r => Equals(Cv.Get(r, "Id"), guid))
                   ?? throw new BridgeException("not_found", $"Reference {id} not found.", 404);
        }

        public IEnumerable<object> Thoughts => Cv.Items(Cv.Get(Project, "Thoughts"));

        /// <summary>All knowledge items: quotations of references plus thoughts (Gedanken) without reference.</summary>
        public IEnumerable<object> AllKnowledgeItems => References.SelectMany(r => Cv.Items(Cv.Get(r, "Quotations"))).Concat(Thoughts);

        public object KnowledgeItem(string id)
        {
            var guid = ParseGuid(id, "knowledgeItemId");
            foreach (var k in AllKnowledgeItems)
                if (Equals(Cv.Get(k, "Id"), guid)) return k;
            throw new BridgeException("not_found", $"Knowledge item {id} not found.", 404);
        }

        // ---- Categories ------------------------------------------------------

        /// <summary>Top-level categories. Filters on ParentCategory in case the project collection is flat.</summary>
        public IEnumerable<object> RootCategories =>
            Cv.Items(Cv.Get(Project, "Categories")).Where(c => !Cv.Has(c, "ParentCategory") || Cv.Get(c, "ParentCategory") == null);

        public static IEnumerable<object> Children(object category) => Cv.Items(Cv.Get(category, "Categories"));

        public object FindCategory(IList<string> path)
        {
            IEnumerable<object> level = RootCategories;
            object current = null;
            foreach (var name in path)
            {
                current = level.FirstOrDefault(c => string.Equals(Cv.GetString(c, "Name"), name, StringComparison.OrdinalIgnoreCase));
                if (current == null) return null;
                level = Children(current);
            }
            return current;
        }

        public object FindOrCreateCategory(IList<string> path, out bool created)
        {
            created = false;
            object parentCollection = Cv.Get(Project, "Categories");
            IEnumerable<object> level = RootCategories;
            object current = null;

            foreach (var name in path)
            {
                current = level.FirstOrDefault(c => string.Equals(Cv.GetString(c, "Name"), name, StringComparison.OrdinalIgnoreCase));
                if (current == null)
                {
                    // CategoryCollection.Add(string name) is the documented macro pattern.
                    current = Cv.Call(parentCollection, "Add", name);
                    created = true;
                }
                parentCollection = Cv.Get(current, "Categories");
                level = Children(current);
            }
            return current;
        }

        public object RequireCategory(IList<string> path) =>
            FindCategory(path) ?? throw new BridgeException("not_found", $"Category '{string.Join(" > ", path)}' not found.", 404);

        public static IList<string> CategoryPath(object category)
        {
            var names = new List<string>();
            var c = category;
            while (c != null)
            {
                names.Insert(0, Cv.GetString(c, "Name"));
                c = Cv.Has(c, "ParentCategory") ? Cv.Get(c, "ParentCategory") : null;
            }
            return names;
        }

        // ---- Keywords, persons, periodicals, publishers ----------------------

        public object FindOrCreateKeyword(string name) => FindOrCreateNamed("Keywords", "SwissAcademic.Citavi.Keyword", name);
        public object FindOrCreateGroup(string name) => FindOrCreateNamed("Groups", "SwissAcademic.Citavi.Group", name);
        public object FindOrCreatePeriodical(string name) => FindOrCreateNamed("Periodicals", "SwissAcademic.Citavi.Periodical", name);
        public object FindOrCreatePublisher(string name) => FindOrCreateNamed("Publishers", "SwissAcademic.Citavi.Publisher", name);

        private object FindOrCreateNamed(string collectionName, string typeName, string name)
        {
            var collection = Cv.Get(Project, collectionName);
            var existing = Cv.Items(collection).FirstOrDefault(x => string.Equals(Cv.GetString(x, "Name"), name, StringComparison.OrdinalIgnoreCase));
            if (existing != null) return existing;

            var type = Cv.RequireType(typeName);
            var entity = Cv.CreateFirst(type, new[] { Project, (object)name }, new[] { Project });
            if (Cv.GetString(entity, "Name") == null) Cv.SetFromString(entity, "Name", name);
            return Cv.AddTo(collection, entity);
        }

        public object FindOrCreatePerson(string lastName, string firstName, string middleName = null)
        {
            var collection = Cv.Get(Project, "Persons");
            var existing = Cv.Items(collection).FirstOrDefault(x =>
                string.Equals(Cv.GetString(x, "LastName"), lastName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(Cv.GetString(x, "FirstName") ?? "", firstName ?? "", StringComparison.OrdinalIgnoreCase));
            if (existing != null) return existing;

            var type = Cv.RequireType("SwissAcademic.Citavi.Person");
            var person = Cv.CreateFirst(type,
                new[] { Project, (object)firstName ?? "", (object)lastName },
                new[] { Project, (object)lastName },
                new[] { Project });
            Cv.SetFromString(person, "LastName", lastName);
            if (!string.IsNullOrEmpty(firstName)) Cv.SetFromString(person, "FirstName", firstName);
            if (!string.IsNullOrEmpty(middleName) && Cv.Has(person, "MiddleName")) Cv.SetFromString(person, "MiddleName", middleName);
            return Cv.AddTo(collection, person);
        }

        private static Guid ParseGuid(string id, string name)
        {
            if (Guid.TryParse(id, out var g)) return g;
            throw new BridgeException("bad_request", $"'{name}' must be a GUID, got '{id}'.");
        }
    }

    /// <summary>Parameter accessors for handlers.</summary>
    internal static class P
    {
        public static string Str(JObject p, string name) => Cv.NullIfEmpty((string)p[name]);

        public static string Req(JObject p, string name) =>
            Str(p, name) ?? throw new BridgeException("bad_request", $"Parameter '{name}' is required.");

        public static int Int(JObject p, string name, int fallback) => p[name] == null ? fallback : (int)p[name];

        public static bool Bool(JObject p, string name, bool fallback) => p[name] == null ? fallback : (bool)p[name];

        public static List<string> StrList(JObject p, string name) =>
            p[name] is JArray a ? a.Select(x => (string)x).Where(x => !string.IsNullOrWhiteSpace(x)).ToList() : new List<string>();

        /// <summary>Category paths: either ["A", "B"] or "A > B" per entry.</summary>
        public static List<List<string>> CategoryPaths(JObject p, string name)
        {
            var result = new List<List<string>>();
            if (!(p[name] is JArray a)) return result;
            foreach (var entry in a)
            {
                var path = CategoryPath(entry);
                if (path.Count > 0) result.Add(path);
            }
            return result;
        }

        public static List<string> CategoryPath(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null) return new List<string>();
            var parts = token is JArray a
                ? a.Select(x => (string)x)
                : ((string)token).Split(new[] { '>' }, StringSplitOptions.RemoveEmptyEntries);
            return parts.Select(x => x?.Trim()).Where(x => !string.IsNullOrEmpty(x)).ToList();
        }
    }
}
