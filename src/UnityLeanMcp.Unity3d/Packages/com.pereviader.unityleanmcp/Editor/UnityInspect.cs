#nullable disable
#pragma warning disable CS0618

using System;
using System.Collections.Generic;
using System.Globalization;
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

        #region Shared Formatting & Guardrail Helpers

        internal static string LimitOutput(
            string value,
            int maxCharacters = DefaultMaxOutputCharacters)
        {
            if (string.IsNullOrEmpty(value))
            {
                return value ?? string.Empty;
            }

            return UnityResultFormatter.LimitOutput(value, maxCharacters);
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
