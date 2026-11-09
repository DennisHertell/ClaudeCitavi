using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeCitavi.Bridge
{
    /// <summary>State of one electronic location (file / URL) of a reference.</summary>
    internal sealed class AttachmentInfo
    {
        public object Location;
        public object Reference;
        public string LocationId;
        public string ReferenceId;
        public string Address;          // as Citavi shows it
        public string ResourceType;     // Citavi LinkedResourceType, e.g. AttachmentFile, AttachmentRemote
        public string Storage;          // local | cloud | linked | url | unknown
        public string LocalPath;        // where the file is (or will be after download)
        public bool AvailableLocally;
        public string FileName;

        public Dictionary<string, object> ToJson() => new Dictionary<string, object>
        {
            ["locationId"] = LocationId,
            ["referenceId"] = ReferenceId,
            ["fileName"] = FileName,
            ["storage"] = Storage,
            ["resourceType"] = ResourceType,
            ["availableLocally"] = AvailableLocally,
            ["localPath"] = LocalPath,
            ["address"] = Address,
        };
    }

    /// <summary>
    /// Finds, resolves and downloads attachments.
    ///
    /// Cloud projects keep attachments in Citavi's cloud storage. Files uploaded
    /// by other people or from other computers are only downloaded by Citavi on
    /// demand. We ask Citavi itself to download them (with the user's Citavi
    /// login), then copy the local file. Citavi's download API is undocumented,
    /// so <see cref="StartDownload"/> discovers suitable "Download*" methods via
    /// reflection and reports what it tried.
    /// </summary>
    internal static class Attachments
    {
        private const BindingFlags Inst = BindingFlags.Public | BindingFlags.Instance;
        private static List<MethodInfo> _extensionCandidates;

        public static IEnumerable<object> ElectronicLocations(object reference) =>
            Cv.Items(Cv.Get(reference, "Locations")).Where(l =>
            {
                var t = Cv.Get(l, "LocationType")?.ToString() ?? "";
                return t.IndexOf("Electronic", StringComparison.OrdinalIgnoreCase) >= 0 || t.Length == 0;
            });

        public static AttachmentInfo Describe(object location, object reference)
        {
            var address = Cv.Get(location, "Address");
            var info = new AttachmentInfo
            {
                Location = location,
                Reference = reference,
                LocationId = Cv.Get(location, "Id")?.ToString(),
                ReferenceId = Cv.Get(reference, "Id")?.ToString(),
                Address = address?.ToString(),
                ResourceType = Cv.Get(address, "LinkedResourceType")?.ToString(),
            };

            info.LocalPath = ResolveLocalPath(location, address);
            info.AvailableLocally = info.LocalPath != null && SafeExists(info.LocalPath);
            // Cloud attachments are cached under a GUID name; the address holds the original file name.
            var isRemote = string.Equals(info.ResourceType, "AttachmentRemote", StringComparison.OrdinalIgnoreCase);
            info.FileName = isRemote
                ? SafeFileName(Cv.GetString(address, "OriginalString")) ?? SafeFileName(info.Address) ?? SafeFileName(info.LocalPath)
                : SafeFileName(info.LocalPath) ?? SafeFileName(Cv.GetString(address, "OriginalString")) ?? SafeFileName(info.Address);

            var rt = info.ResourceType ?? "";
            if (rt.IndexOf("Remote", StringComparison.OrdinalIgnoreCase) >= 0 || rt.IndexOf("Cloud", StringComparison.OrdinalIgnoreCase) >= 0)
                info.Storage = "cloud";
            else if (rt.IndexOf("Attachment", StringComparison.OrdinalIgnoreCase) >= 0)
                info.Storage = "local";
            else if (info.Address != null && (info.Address.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || info.Address.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
                info.Storage = "url";
            else if (info.LocalPath != null)
                info.Storage = "linked";
            else
                info.Storage = "unknown";
            return info;
        }

        /// <summary>Best effort: the path where Citavi keeps (or will keep) the file.</summary>
        private static string ResolveLocalPath(object location, object address)
        {
            // Cloud attachments (LinkedResourceType.AttachmentRemote) live in Citavi's project cache.
            var cache = Cv.GetString(address, "CacheFilePath");
            if (cache != null && string.Equals(Cv.Get(address, "LinkedResourceType")?.ToString(), "AttachmentRemote", StringComparison.OrdinalIgnoreCase))
                return cache;

            // Common macro pattern: location.Address.Resolve().LocalPath
            foreach (var target in new[] { address, location })
            {
                if (target == null) continue;
                try
                {
                    var resolve = target.GetType().GetMethods(Inst).FirstOrDefault(m => m.Name == "Resolve" && m.GetParameters().All(p => p.IsOptional));
                    var uri = resolve?.Invoke(target, resolve.GetParameters().Select(p => p.DefaultValue).ToArray()) as Uri;
                    if (uri != null && uri.IsFile) return uri.LocalPath;
                }
                catch { /* try next */ }
            }

            foreach (var name in new[] { "FullPath", "LocalPath", "Path" })
            {
                var v = Cv.GetString(location, name) ?? Cv.GetString(address, name);
                if (v != null && LooksLikePath(v)) return v;
            }

            var raw = address?.ToString();
            return raw != null && LooksLikePath(raw) ? raw : null;
        }

        /// <summary>
        /// Asks Citavi to download a cloud attachment. Must run on the UI thread.
        /// Returns a task to wait for (or null) and a log of what was tried.
        /// </summary>
        public static Task StartDownload(object location, object project, List<string> log)
        {
            var address = Cv.Get(location, "Address");

            // Citavi 6: cloud attachments are fetched into the project cache with LinkedResource.CacheAsync(RemoteUriCacheInfo).
            if (address != null && string.Equals(Cv.Get(address, "LinkedResourceType")?.ToString(), "AttachmentRemote", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var name in new[] { "CacheAsync", "StartCacheAsync" })
                {
                    var m = address.GetType().GetMethods(Inst).FirstOrDefault(x => x.Name == name && x.GetParameters().Length == 1);
                    if (m == null) continue;
                    try
                    {
                        var info = CreateDefault(m.GetParameters()[0].ParameterType);
                        var result = m.Invoke(address, new[] { info });
                        log.Add($"LinkedResource.{name}({(info == null ? "null" : info.GetType().Name)}): invoked");
                        if (result is Task t) return t;
                    }
                    catch (Exception ex)
                    {
                        log.Add($"LinkedResource.{name}: {ex.GetBaseException().Message}");
                    }
                }
            }
            var targets = new[] { location, address }.Where(t => t != null).ToList();

            var candidates = new List<(object Target, MethodInfo Method)>();
            foreach (var t in targets)
            {
                candidates.AddRange(t.GetType().GetMethods(Inst)
                    .Where(m => m.Name.IndexOf("Download", StringComparison.OrdinalIgnoreCase) >= 0)
                    .Select(m => (t, m)));
            }
            foreach (var m in ExtensionCandidates())
            {
                var first = m.GetParameters()[0].ParameterType;
                var target = targets.FirstOrDefault(first.IsInstanceOfType);
                if (target != null) candidates.Add((null, m));
            }

            foreach (var (target, method) in candidates.OrderBy(c => c.Method.Name.EndsWith("Async") ? 0 : 1))
            {
                var args = FillArguments(method, target == null ? targets : null, project);
                var signature = $"{method.DeclaringType?.Name}.{method.Name}";
                if (args == null) { log.Add(signature + ": skipped (unsupported parameters)"); continue; }
                try
                {
                    var result = method.Invoke(target, args);
                    log.Add(signature + ": invoked");
                    return result as Task ?? Task.FromResult(result);
                }
                catch (Exception ex)
                {
                    log.Add($"{signature}: {ex.GetBaseException().Message}");
                }
            }

            if (candidates.Count == 0) log.Add("No download method found on Location/LinkedResource (see debug.findMembers pattern=Download).");
            return null;
        }

        /// <summary>Parameterless instance, a static Default/Instance member, or null.</summary>
        private static object CreateDefault(Type type)
        {
            if (type.GetConstructor(Type.EmptyTypes) != null) return Activator.CreateInstance(type);
            var member = type.GetProperties(BindingFlags.Public | BindingFlags.Static).FirstOrDefault(p => p.PropertyType == type);
            if (member != null) return member.GetValue(null, null);
            var field = type.GetFields(BindingFlags.Public | BindingFlags.Static).FirstOrDefault(f => f.FieldType == type);
            return field?.GetValue(null);
        }

        /// <summary>Static (extension) methods named *Download* in Citavi assemblies whose first parameter is an object.</summary>
        private static List<MethodInfo> ExtensionCandidates()
        {
            if (_extensionCandidates != null) return _extensionCandidates;
            var list = new List<MethodInfo>();
            foreach (var asm in CitaviAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); } catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
                foreach (var t in types.Where(t => t.IsPublic && t.IsAbstract && t.IsSealed)) // static classes
                {
                    list.AddRange(t.GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m =>
                        m.Name.IndexOf("Download", StringComparison.OrdinalIgnoreCase) >= 0 &&
                        m.IsDefined(typeof(System.Runtime.CompilerServices.ExtensionAttribute), false) &&
                        !m.ContainsGenericParameters));
                }
            }
            return _extensionCandidates = list;
        }

        internal static IEnumerable<Assembly> CitaviAssemblies() =>
            AppDomain.CurrentDomain.GetAssemblies().Where(a =>
            {
                var n = a.GetName().Name;
                return n.StartsWith("SwissAcademic", StringComparison.OrdinalIgnoreCase) || n.StartsWith("Citavi", StringComparison.OrdinalIgnoreCase);
            });

        /// <summary>Fills parameters we can supply safely; returns null if any parameter is unknown.</summary>
        private static object[] FillArguments(MethodInfo method, List<object> extensionTargets, object project)
        {
            var ps = method.GetParameters();
            var args = new object[ps.Length];
            for (var i = 0; i < ps.Length; i++)
            {
                var t = ps[i].ParameterType;
                if (i == 0 && extensionTargets != null)
                {
                    args[i] = extensionTargets.First(t.IsInstanceOfType);
                }
                else if (t == typeof(CancellationToken)) args[i] = CancellationToken.None;
                else if (project != null && t.IsInstanceOfType(project)) args[i] = project;
                else if (ps[i].IsOptional) args[i] = ps[i].DefaultValue == DBNull.Value ? null : ps[i].DefaultValue;
                else if (t.IsGenericType && t.GetGenericTypeDefinition().Name.StartsWith("IProgress")) args[i] = null;
                else return null;
            }
            return args;
        }

        /// <summary>Waits for a download task off the UI thread and returns a path if the task produced one.</summary>
        public static string Wait(Task task, TimeSpan timeout, List<string> log)
        {
            if (task == null) return null;
            try
            {
                if (!task.Wait(timeout)) { log.Add("Download timed out."); return null; }
            }
            catch (Exception ex)
            {
                log.Add("Download failed: " + ex.GetBaseException().Message);
                return null;
            }

            var result = task.GetType().GetProperty("Result")?.GetValue(task, null);
            switch (result)
            {
                case string s when LooksLikePath(s): return s;
                case Uri u when u.IsFile: return u.LocalPath;
                case FileInfo f: return f.FullName;
                default: return null;
            }
        }

        public static string TargetFileName(AttachmentInfo info, string naming)
        {
            var ext = Path.GetExtension(info.FileName ?? "") ;
            if (string.IsNullOrEmpty(ext)) ext = ".pdf";
            if (!string.Equals(naming, "citation", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(info.FileName))
                return Sanitize(info.FileName);

            var author = Cv.Items(Cv.Get(info.Reference, "Authors")).Select(a => Cv.GetString(a, "LastName")).FirstOrDefault()
                         ?? Cv.Items(Cv.Get(info.Reference, "Editors")).Select(a => Cv.GetString(a, "LastName")).FirstOrDefault()
                         ?? "o.A.";
            var year = Cv.GetString(info.Reference, "Year") ?? Cv.GetString(info.Reference, "YearResolved") ?? "o.J.";
            var title = Cv.GetString(info.Reference, "Title") ?? info.FileName ?? "Dokument";
            var name = Sanitize($"{author} {year} - {title}");
            if (name.Length > 120) name = name.Substring(0, 120).TrimEnd();
            return name + ext;
        }

        public static string UniquePath(string folder, string fileName)
        {
            var path = Path.Combine(folder, fileName);
            var name = Path.GetFileNameWithoutExtension(fileName);
            var ext = Path.GetExtension(fileName);
            for (var i = 2; File.Exists(path); i++) path = Path.Combine(folder, $"{name} ({i}){ext}");
            return path;
        }

        private static string Sanitize(string s)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, ' ');
            return string.Join(" ", s.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)).Trim().TrimEnd('.');
        }

        private static bool LooksLikePath(string s) =>
            s.Length > 3 && (s[1] == ':' || s.StartsWith(@"\\")) && s.IndexOfAny(Path.GetInvalidPathChars()) < 0;

        private static bool SafeExists(string path)
        {
            try { return File.Exists(path); } catch { return false; }
        }

        private static string SafeFileName(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            try
            {
                var name = Path.GetFileName(s.Split('?')[0]);
                return string.IsNullOrWhiteSpace(name) ? null : name;
            }
            catch { return null; }
        }
    }
}
