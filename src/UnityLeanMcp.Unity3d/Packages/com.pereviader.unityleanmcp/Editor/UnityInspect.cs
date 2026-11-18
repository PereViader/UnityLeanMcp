#nullable disable
#pragma warning disable CS0618

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

namespace UnityLeanMcp
{
    /// <summary>
    /// Built-in, token-bounded scene and object introspection helpers for UnityLeanMcp.
    /// Callable directly within unity_eval to inspect scene hierarchy, GameObject components,
    /// project assets, and active selection without context bloat or reflection boilerplate.
    /// </summary>
    public static partial class Inspect
    {
        public const int DefaultMaxDepth = 32;
        public const int DefaultMaxItems = 100;
        public const int DefaultMaxOutputCharacters = 64 * 1024;
        public const int DefaultMaxOutputBytes = 64 * 1024;

        internal const string CycleTruncationMarker = "... (truncated: cycle detected)";
        internal const string DepthTruncationMarker = "... (truncated: maximum depth reached)";
        internal const string ItemTruncationMarker = "... (truncated: maximum item count reached)";
        internal const string OutputTruncationMarker = "... (truncated: maximum output size reached)";

        private sealed class ReferenceIdentityComparer<T> : IEqualityComparer<T> where T : class
        {
            public static readonly ReferenceIdentityComparer<T> Instance = new ReferenceIdentityComparer<T>();
            public bool Equals(T x, T y) => ReferenceEquals(x, y);
            public int GetHashCode(T obj) => RuntimeHelpers.GetHashCode(obj);
        }

        private sealed class ReferenceIdentityComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceIdentityComparer Instance = new ReferenceIdentityComparer();
            public new bool Equals(object x, object y) => ReferenceEquals(x, y);
            public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
        }

        #region Tests Discovery

        /// <summary>
        /// Lists available tests in the loaded AppDomain via reflection without running them.
        /// </summary>
        public static string Tests(string filter = null, int maxItems = DefaultMaxItems)
        {
            maxItems = Math.Max(1, maxItems);
            string cleanFilter = filter?.Trim() ?? string.Empty;
            bool hasFilter = !string.IsNullOrWhiteSpace(cleanFilter);
            Regex wildcardRegex = BuildWildcardRegex(cleanFilter);

            var allItems = TestDiscoveryHelper.DiscoverAllTests();
            var discoveredTests = new List<string>();
            for (int i = 0; i < allItems.Count; i++)
            {
                string fullName = allItems[i].FullName;
                if (MatchesWildcardOrSubstring(fullName, cleanFilter, wildcardRegex))
                {
                    discoveredTests.Add(fullName);
                }
            }

            if (discoveredTests.Count == 0)
            {
                return hasFilter
                    ? $"(No tests found matching filter '{cleanFilter}')"
                    : "(No tests found in loaded assemblies)";
            }

            discoveredTests.Sort(StringComparer.OrdinalIgnoreCase);

            var sb = new StringBuilder();
            sb.AppendLine($"Available Tests ({discoveredTests.Count}):");
            int countToReport = Math.Min(discoveredTests.Count, maxItems);
            for (int i = 0; i < countToReport; i++)
            {
                sb.AppendLine($"  • {discoveredTests[i]}");
            }

            if (discoveredTests.Count > maxItems)
            {
                sb.AppendLine(ItemTruncationMarker);
            }

            return LimitOutput(sb.ToString().TrimEnd());
        }

        #endregion

        #region Help

        /// <summary>
        /// Returns a compact, categorized reference of all available Inspect helper methods and signatures.
        /// </summary>
        public static string Help()
        {
            var sb = new StringBuilder();
            sb.AppendLine("UnityLeanMcp Inspection Helpers (UnityLeanMcp.Inspect):");

            var categories = new Dictionary<string, List<MethodInfo>>(StringComparer.Ordinal)
            {
                ["Scene & Hierarchy"] = new List<MethodInfo>(),
                ["Objects & Components"] = new List<MethodInfo>(),
                ["Assets & Prefabs"] = new List<MethodInfo>(),
                ["Visual Capture"] = new List<MethodInfo>(),
                ["Editor & Tests"] = new List<MethodInfo>(),
                ["Utilities"] = new List<MethodInfo>()
            };

            var methods = typeof(Inspect).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => !m.IsSpecialName)
                .OrderBy(m => m.Name, StringComparer.Ordinal)
                .ThenBy(m => m.GetParameters().Length);

            foreach (var m in methods)
            {
                string cat;
                switch (m.Name)
                {
                    case nameof(Hierarchy):
                    case nameof(Find):
                    case nameof(MissingScripts):
                    case nameof(SceneState):
                    case nameof(PrefabStage):
                    case nameof(UIDocument):
                        cat = "Scene & Hierarchy";
                        break;
                    case nameof(GameObject):
                    case nameof(Component):
                    case nameof(Object):
                    case nameof(PrefabOverrides):
                        cat = "Objects & Components";
                        break;
                    case nameof(FindAsset):
                    case nameof(FindAssets):
                    case nameof(FindPrefabs):
                    case nameof(Dependencies):
                    case nameof(Referencers):
                        cat = "Assets & Prefabs";
                        break;
                    case nameof(CaptureCamera):
                    case nameof(CaptureSceneView):
                        cat = "Visual Capture";
                        break;
                    case nameof(Selection):
                    case nameof(MainCamera):
                    case nameof(Tests):
                        cat = "Editor & Tests";
                        break;
                    default:
                        cat = "Utilities";
                        break;
                }
                categories[cat].Add(m);
            }

            foreach (var kvp in categories)
            {
                if (kvp.Value.Count == 0) continue;
                sb.AppendLine();
                sb.AppendLine($"{kvp.Key}:");
                foreach (var m in kvp.Value)
                {
                    string retType = FormatTypeName(m.ReturnType);
                    var paramStrings = new List<string>();
                    foreach (var p in m.GetParameters())
                    {
                        string pType = FormatTypeName(p.ParameterType);
                        string pStr = $"{pType} {p.Name}";
                        if (p.HasDefaultValue)
                        {
                            string defVal;
                            if (p.DefaultValue == null)
                            {
                                defVal = "null";
                            }
                            else if (p.DefaultValue is bool b)
                            {
                                defVal = b ? "true" : "false";
                            }
                            else if (p.DefaultValue is string s)
                            {
                                defVal = $"\"{s}\"";
                            }
                            else
                            {
                                defVal = Convert.ToString(p.DefaultValue, CultureInfo.InvariantCulture);
                            }
                            pStr += $" = {defVal}";
                        }
                        paramStrings.Add(pStr);
                    }

                    sb.AppendLine($"• {retType} {m.Name}({string.Join(", ", paramStrings)})");
                }
            }

            return sb.ToString().TrimEnd();
        }

        private static string FormatTypeName(Type type)
        {
            if (type == null) return "void";
            if (type == typeof(void)) return "void";
            if (type.IsArray) return FormatTypeName(type.GetElementType()) + "[]";
            var underlyingNullable = Nullable.GetUnderlyingType(type);
            if (underlyingNullable != null) return FormatTypeName(underlyingNullable) + "?";
            if (type == typeof(string)) return "string";
            if (type == typeof(int)) return "int";
            if (type == typeof(bool)) return "bool";
            if (type == typeof(float)) return "float";
            if (type == typeof(double)) return "double";
            if (type == typeof(object)) return "object";
            return type.Name;
        }

        #endregion

        #region Shared Formatting & Guardrail Helpers

        internal static string LimitOutput(
            string value,
            int maxCharacters = DefaultMaxOutputCharacters,
            int maxBytes = DefaultMaxOutputBytes)
        {
            if (string.IsNullOrEmpty(value))
            {
                return value ?? string.Empty;
            }

            return UnityResultFormatter.LimitOutput(value, maxCharacters, maxBytes);
        }

        private static PropertyInfo GetCachedProperty(
            Dictionary<Type, PropertyInfo> cache,
            Type type,
            string propertyName,
            Type expectedPropertyType)
        {
            if (type == null) return null;

            lock (cache)
            {
                if (cache.TryGetValue(type, out var cached))
                {
                    return cached;
                }
            }

            PropertyInfo resolved = null;
            try
            {
                var prop = type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
                if (prop != null && prop.PropertyType == expectedPropertyType)
                {
                    resolved = prop;
                }
            }
            catch { }

            lock (cache)
            {
                cache[type] = resolved;
            }

            return resolved;
        }

        private static string FormatTextSummary(string text, int maxLength = 30)
        {
            if (string.IsNullOrEmpty(text)) return "";
            string singleLine = text.Replace("\r", "").Replace("\n", "\\n");
            if (singleLine.Length > maxLength)
            {
                singleLine = singleLine.Substring(0, maxLength) + "...";
            }
            return singleLine;
        }

        private static string GetHierarchyPath(Transform t)
        {
            if (t == null) return "";
            var segments = new List<string>();
            var visited = new HashSet<Transform>(ReferenceIdentityComparer<Transform>.Instance);
            var cur = t;
            while (cur != null && visited.Add(cur))
            {
                string segName = "Unnamed";
                try { segName = cur.name; } catch { }
                segments.Add(segName);
                Transform p = null;
                try { p = cur.parent; } catch { }
                cur = p;
            }
            segments.Reverse();
            return "/" + string.Join("/", segments);
        }

        private static Regex BuildWildcardRegex(string pattern)
        {
            if (string.IsNullOrEmpty(pattern) || (!pattern.Contains('*') && !pattern.Contains('?')))
            {
                return null;
            }

            string regexPattern = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
            return new Regex(regexPattern, RegexOptions.IgnoreCase);
        }

        private static bool MatchesWildcardOrSubstring(string candidate, string pattern, Regex wildcardRegex)
        {
            if (string.IsNullOrEmpty(pattern) || pattern == "*") return true;
            if (string.IsNullOrEmpty(candidate)) return false;
            return wildcardRegex != null
                ? wildcardRegex.IsMatch(candidate)
                : candidate.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void ClampHierarchyLimits(ref int maxDepth, ref int maxItems)
        {
            maxDepth = Math.Max(0, Math.Min(maxDepth, 1000));
            maxItems = Math.Max(1, maxItems);
        }

        private static void AppendItemLimitMarkerOnce(StringBuilder sb, string indent, ref bool itemLimitReached)
        {
            if (!itemLimitReached)
            {
                itemLimitReached = true;
                sb.AppendLine($"{indent}{ItemTruncationMarker}");
            }
        }

        private static string FormatRectTransformSummary(RectTransform rt)
        {
            string anchoredPos = rt.anchoredPosition.ToString("F2", CultureInfo.InvariantCulture);
            string sizeDelta = rt.sizeDelta.ToString("F2", CultureInfo.InvariantCulture);
            string rectW = rt.rect.width.ToString("F2", CultureInfo.InvariantCulture);
            string rectH = rt.rect.height.ToString("F2", CultureInfo.InvariantCulture);
            string anchorMin = rt.anchorMin.ToString("F2", CultureInfo.InvariantCulture);
            string anchorMax = rt.anchorMax.ToString("F2", CultureInfo.InvariantCulture);
            string pivot = rt.pivot.ToString("F2", CultureInfo.InvariantCulture);
            return $"RectTransform: anchoredPos={anchoredPos}, sizeDelta={sizeDelta}, rect=({rectW}x{rectH}), anchors=[{anchorMin} to {anchorMax}], pivot={pivot}";
        }

        #endregion
    }
}
