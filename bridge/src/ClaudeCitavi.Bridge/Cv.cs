using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace ClaudeCitavi.Bridge
{
    /// <summary>
    /// Reflection helpers for the Citavi object model.
    ///
    /// Citavi has no officially documented add-on API. To keep the add-on
    /// compiling across Citavi versions, all access beyond the add-on entry
    /// point goes through these helpers. If a member does not exist in the
    /// installed Citavi version, we get a clear runtime error (and can inspect
    /// the real API via the "debug.describeType" RPC) instead of a build error.
    /// </summary>
    internal static class Cv
    {
        private const BindingFlags Inst = BindingFlags.Public | BindingFlags.Instance;
        private const BindingFlags Stat = BindingFlags.Public | BindingFlags.Static;

        public static Type FindType(string fullName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t = null;
                try { t = asm.GetType(fullName, false); } catch { /* dynamic assemblies */ }
                if (t != null) return t;
            }
            return null;
        }

        public static Type RequireType(string fullName) =>
            FindType(fullName) ?? throw new BridgeException("api_mismatch", $"Citavi type '{fullName}' not found in this Citavi version.", 500);

        /// <summary>Property lookup: exact name first, then case-insensitive (callers often send "title" for "Title").</summary>
        private static PropertyInfo Prop(Type type, string name)
        {
            try
            {
                return type.GetProperty(name, Inst) ?? type.GetProperty(name, Inst | BindingFlags.IgnoreCase);
            }
            catch (AmbiguousMatchException)
            {
                // Property hidden with "new" in a derived class: take the most derived one.
                return type.GetProperties(Inst).Where(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(p => Depth(p.DeclaringType)).FirstOrDefault();
            }
        }

        private static int Depth(Type t) { var d = 0; while (t != null) { d++; t = t.BaseType; } return d; }

        public static object Get(object obj, string property)
        {
            if (obj == null) return null;
            var p = Prop(obj.GetType(), property);
            return p?.GetValue(obj, null);
        }

        public static string GetString(object obj, string property)
        {
            var v = Get(obj, property);
            return v == null ? null : NullIfEmpty(v.ToString());
        }

        public static bool Has(object obj, string property) =>
            obj != null && Prop(obj.GetType(), property) != null;

        /// <summary>Sets a property from a string, converting it if the property is not a string.</summary>
        public static void SetFromString(object obj, string property, string value)
        {
            var p = Prop(obj.GetType(), property)
                    ?? throw new BridgeException("unknown_field", $"{obj.GetType().Name} has no property '{property}'.");

            if (!p.CanWrite)
            {
                // Some Citavi value objects (e.g. PageRange) are immutable and replaced via an "Update" method.
                var current = p.GetValue(obj, null);
                var update = current?.GetType().GetMethod("Update", Inst, null, new[] { typeof(string) }, null);
                if (update != null && update.ReturnType == p.PropertyType && p.GetSetMethod() != null)
                {
                    p.SetValue(obj, update.Invoke(current, new object[] { value }), null);
                    return;
                }
                throw new BridgeException("read_only_field", $"{obj.GetType().Name}.{property} is read-only.");
            }

            p.SetValue(obj, Convert(value, p.PropertyType, p.GetValue(obj, null)), null);
        }

        public static object Convert(string value, Type target, object current = null)
        {
            if (target == typeof(string)) return value;
            if (value == null) return target.IsValueType ? Activator.CreateInstance(target) : null;

            var underlying = Nullable.GetUnderlyingType(target) ?? target;
            if (underlying.IsEnum) return Enum.Parse(underlying, value, true);
            if (underlying == typeof(int)) return int.Parse(value);
            if (underlying == typeof(bool)) return bool.Parse(value);
            if (underlying == typeof(double)) return double.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
            if (underlying == typeof(DateTime)) return DateTime.Parse(value, System.Globalization.CultureInfo.InvariantCulture);

            // Citavi value objects: try instance Update(string), static Parse(string), ctor(string).
            var update = current?.GetType().GetMethod("Update", Inst, null, new[] { typeof(string) }, null);
            if (update != null && target.IsAssignableFrom(update.ReturnType)) return update.Invoke(current, new object[] { value });

            var parse = underlying.GetMethod("Parse", Stat, null, new[] { typeof(string) }, null);
            if (parse != null) return parse.Invoke(null, new object[] { value });

            var ctor = underlying.GetConstructor(new[] { typeof(string) });
            if (ctor != null) return ctor.Invoke(new object[] { value });

            throw new BridgeException("unsupported_conversion", $"Cannot convert text to {underlying.FullName}.");
        }

        public static object Call(object obj, string method, params object[] args)
        {
            var candidates = obj.GetType().GetMethods(Inst).Where(m => m.Name == method && m.GetParameters().Length == args.Length);
            foreach (var m in candidates)
            {
                if (ParametersMatch(m.GetParameters(), args)) return m.Invoke(obj, args);
            }
            throw new BridgeException("api_mismatch", $"{obj.GetType().Name}.{method}({args.Length} args) not found in this Citavi version.", 500);
        }

        public static object CallStatic(Type type, string method, params object[] args)
        {
            foreach (var m in type.GetMethods(Stat).Where(x => x.Name == method && x.GetParameters().Length == args.Length))
            {
                if (ParametersMatch(m.GetParameters(), args)) return m.Invoke(null, args);
            }
            throw new BridgeException("api_mismatch", $"{type.Name}.{method}({args.Length} args) not found in this Citavi version.", 500);
        }

        public static object Create(Type type, params object[] args)
        {
            foreach (var c in type.GetConstructors())
            {
                if (c.GetParameters().Length == args.Length && ParametersMatch(c.GetParameters(), args)) return c.Invoke(args);
            }
            throw new BridgeException("api_mismatch", $"No matching constructor {type.Name}({string.Join(", ", args.Select(a => a?.GetType().Name ?? "null"))}).", 500);
        }

        /// <summary>Tries several constructor signatures and returns the first that works.</summary>
        public static object CreateFirst(Type type, params object[][] argumentSets)
        {
            foreach (var args in argumentSets)
            {
                foreach (var c in type.GetConstructors())
                {
                    if (c.GetParameters().Length == args.Length && ParametersMatch(c.GetParameters(), args)) return c.Invoke(args);
                }
            }
            throw new BridgeException("api_mismatch", $"No suitable constructor found for {type.Name}.", 500);
        }

        /// <summary>Adds an item to a Citavi collection and returns the stored instance.</summary>
        public static object AddTo(object collection, object item)
        {
            var add = collection.GetType().GetMethods(Inst)
                .FirstOrDefault(m => m.Name == "Add" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType.IsInstanceOfType(item));
            if (add == null) throw new BridgeException("api_mismatch", $"{collection.GetType().Name}.Add({item.GetType().Name}) not found.", 500);
            var result = add.Invoke(collection, new[] { item });
            return result != null && item.GetType().IsInstanceOfType(result) ? result : item;
        }

        public static void RemoveFrom(object collection, object item)
        {
            var remove = collection.GetType().GetMethods(Inst)
                .FirstOrDefault(m => m.Name == "Remove" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType.IsInstanceOfType(item));
            remove?.Invoke(collection, new[] { item });
        }

        public static bool Contains(object collection, object item) => Items(collection).Any(x => ReferenceEquals(x, item));

        public static IEnumerable<object> Items(object collection)
        {
            if (collection is IEnumerable e)
            {
                foreach (var item in e) yield return item;
            }
        }

        /// <summary>Resolves a static member (e.g. ReferenceType.JournalArticle) by name, case-insensitive.</summary>
        public static object StaticValue(Type type, string name)
        {
            var f = type.GetFields(Stat).FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            if (f != null) return f.GetValue(null);
            var p = type.GetProperties(Stat).FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            return p?.GetValue(null, null);
        }

        public static IEnumerable<KeyValuePair<string, object>> StaticValuesOfOwnType(Type type)
        {
            foreach (var f in type.GetFields(Stat).Where(x => x.FieldType == type))
                yield return new KeyValuePair<string, object>(f.Name, f.GetValue(null));
            foreach (var p in type.GetProperties(Stat).Where(x => x.PropertyType == type && x.GetIndexParameters().Length == 0))
                yield return new KeyValuePair<string, object>(p.Name, p.GetValue(null, null));
        }

        /// <summary>First enum member whose name matches one of the candidates (case-insensitive), else the first member.</summary>
        public static object EnumValue(Type enumType, string[] candidates, bool fallbackToFirst = false)
        {
            if (enumType == null || !enumType.IsEnum) return null;
            foreach (var c in candidates)
            {
                var name = Enum.GetNames(enumType).FirstOrDefault(n => string.Equals(n, c, StringComparison.OrdinalIgnoreCase));
                if (name != null) return Enum.Parse(enumType, name);
            }
            var names = Enum.GetNames(enumType);
            return fallbackToFirst && names.Length > 0 ? Enum.Parse(enumType, names[0]) : null;
        }

        public static string NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;

        private static bool ParametersMatch(ParameterInfo[] ps, object[] args)
        {
            for (var i = 0; i < ps.Length; i++)
            {
                var t = ps[i].ParameterType;
                if (args[i] == null)
                {
                    if (t.IsValueType && Nullable.GetUnderlyingType(t) == null) return false;
                }
                else if (!t.IsInstanceOfType(args[i])) return false;
            }
            return true;
        }
    }
}
