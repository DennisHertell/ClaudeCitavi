using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace ClaudeCitavi.Bridge.Handlers
{
    internal static class SystemHandlers
    {
        public static void Register(RpcDispatcher d)
        {
            d.Read("system.ping", "Bridge/Citavi version and the active project.", (ctx, p) => new
            {
                bridgeVersion = typeof(SystemHandlers).Assembly.GetName().Version.ToString(),
                citaviVersion = ctx.Form.GetType().Assembly.GetName().Version.ToString(),
                project = new
                {
                    name = Cv.GetString(ctx.Project, "Name"),
                    type = Cv.Get(ctx.Project, "ProjectType")?.ToString(),
                    isCloud = ctx.IsCloud,
                    path = Cv.GetString(Cv.Get(ctx.Project, "Addresses"), "ProjectFilePath"),
                    referenceCount = ctx.References.Count(),
                    attachmentsFolder = Cv.GetString(Cv.Get(ctx.Project, "Addresses"), "AttachmentsFolderPath"),
                },
            });

            d.Read("system.methods", "Lists all RPC methods.", (ctx, p) => d.Describe());

            RegisterFindMembers(d);

            // Introspection: lets us verify the real Citavi API on the user's machine
            // without a debugger (see docs/ENTWICKLUNG.md).
            d.Read("debug.describeType", "Public members of a Citavi type. params: typeName", (ctx, p) =>
            {
                var name = P.Req(p, "typeName");
                var type = Cv.FindType(name) ?? Cv.FindType("SwissAcademic.Citavi." + name)
                           ?? throw new BridgeException("not_found", $"Type '{name}' not found.", 404);
                const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
                return new
                {
                    fullName = type.FullName,
                    assembly = type.Assembly.GetName().Name,
                    baseType = type.BaseType?.FullName,
                    isEnum = type.IsEnum,
                    enumValues = type.IsEnum ? Enum.GetNames(type) : null,
                    constructors = type.GetConstructors().Select(c => Signature(c.GetParameters())).ToList(),
                    properties = type.GetProperties(flags).Select(x => $"{x.PropertyType.Name} {x.Name} {{{(x.CanRead ? "get;" : "")}{(x.GetSetMethod() != null ? "set;" : "")}}}").ToList(),
                    methods = type.GetMethods(flags).Where(m => !m.IsSpecialName)
                        .Select(m => $"{(m.IsStatic ? "static " : "")}{m.ReturnType.Name} {m.Name}{Signature(m.GetParameters())}").Distinct().ToList(),
                };
            });
        }

        private static void RegisterFindMembers(RpcDispatcher d)
        {
            d.Read("debug.findMembers",
                "Searches all Citavi types for members whose name matches a pattern (case-insensitive substring). params: pattern, limit=300",
                (ctx, p) =>
                {
                    var pattern = P.Req(p, "pattern");
                    var limit = P.Int(p, "limit", 300);
                    var hits = new List<string>();
                    const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
                    foreach (var asm in Attachments.CitaviAssemblies())
                    {
                        Type[] types;
                        try { types = asm.GetTypes(); } catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
                        foreach (var t in types.Where(t => t.IsPublic))
                        {
                            if (t.Name.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0) hits.Add("type " + t.FullName);
                            foreach (var m in t.GetMembers(flags).Where(m => m.Name.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0))
                            {
                                var sig = m is MethodInfo mi ? $"{(mi.IsStatic ? "static " : "")}{mi.ReturnType.Name} {mi.Name}{Signature(mi.GetParameters())}"
                                        : m is PropertyInfo pi ? $"{pi.PropertyType.Name} {pi.Name}" : m.Name;
                                hits.Add($"{t.FullName}: {sig}");
                                if (hits.Count >= limit) return new { truncated = true, hits };
                            }
                        }
                    }
                    return new { truncated = false, hits };
                });
        }

        private static string Signature(ParameterInfo[] ps) =>
            "(" + string.Join(", ", ps.Select(x => x.ParameterType.Name + " " + x.Name)) + ")";
    }
}
