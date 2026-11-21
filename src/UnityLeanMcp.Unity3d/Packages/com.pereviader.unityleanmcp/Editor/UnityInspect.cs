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

        #region Tests Discovery

        /// <summary>
        /// Lists available tests matching a name filter and maximum item count.
        /// </summary>
        public static string Tests(string filter, int maxItems)
        {
            return Tests(filter, mode: null, category: null, maxItems: maxItems);
        }

        /// <summary>
        /// Lists available tests with ground-truth modes, categories, and parameterized names via TestRunnerApi (with reflection fallback).
        /// </summary>
        public static string Tests(string filter = null, string mode = null, string category = null, int maxItems = DefaultMaxItems)
        {
            maxItems = Math.Max(1, maxItems);
            string cleanFilter = filter?.Trim() ?? string.Empty;
            string cleanMode = mode?.Trim() ?? string.Empty;
            string cleanCategory = category?.Trim() ?? string.Empty;

            if (!string.IsNullOrEmpty(cleanMode) &&
                !string.Equals(cleanMode, "editmode", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(cleanMode, "playmode", StringComparison.OrdinalIgnoreCase))
            {
                return $"(Invalid mode '{cleanMode}'. Expected 'editmode' or 'playmode')";
            }

            string normalizedModeFilter = string.IsNullOrEmpty(cleanMode) ? string.Empty : cleanMode.ToLowerInvariant();
            Regex wildcardRegex = BuildWildcardRegex(cleanFilter);
            Regex categoryRegex = BuildWildcardRegex(cleanCategory);

            var allItems = TestDiscoveryHelper.DiscoverAllTests();
            var matchedItems = new List<TestDiscoveryHelper.DiscoveredItem>();
            var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < allItems.Count; i++)
            {
                var item = allItems[i];
                if (item == null || string.IsNullOrWhiteSpace(item.FullName))
                {
                    continue;
                }

                string itemMode = string.IsNullOrWhiteSpace(item.Mode) ? "editmode" : item.Mode.Trim().ToLowerInvariant();
                if (!string.IsNullOrEmpty(normalizedModeFilter) &&
                    !string.Equals(itemMode, normalizedModeFilter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!MatchesWildcardOrSubstring(item.FullName, cleanFilter, wildcardRegex))
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(cleanCategory))
                {
                    bool categoryMatch = false;
                    if (item.Categories != null)
                    {
                        for (int c = 0; c < item.Categories.Count; c++)
                        {
                            if (MatchesWildcardOrSubstring(item.Categories[c], cleanCategory, categoryRegex))
                            {
                                categoryMatch = true;
                                break;
                            }
                        }
                    }

                    if (!categoryMatch)
                    {
                        continue;
                    }
                }

                string dedupeKey = itemMode + "\n" + (item.AssemblyName ?? string.Empty) + "\n" + item.FullName;
                if (seenKeys.Add(dedupeKey))
                {
                    matchedItems.Add(item);
                }
            }

            if (matchedItems.Count == 0)
            {
                if (!string.IsNullOrEmpty(cleanFilter) &&
                    string.IsNullOrEmpty(normalizedModeFilter) &&
                    string.IsNullOrEmpty(cleanCategory))
                {
                    return $"(No tests found matching filter '{cleanFilter}')";
                }

                var criteria = new List<string>();
                if (!string.IsNullOrEmpty(cleanFilter)) criteria.Add($"filter '{cleanFilter}'");
                if (!string.IsNullOrEmpty(normalizedModeFilter)) criteria.Add($"mode '{normalizedModeFilter}'");
                if (!string.IsNullOrEmpty(cleanCategory)) criteria.Add($"category '{cleanCategory}'");

                return criteria.Count > 0
                    ? $"(No tests found matching {string.Join(", ", criteria)})"
                    : "(No tests found in loaded assemblies)";
            }

            matchedItems.Sort((a, b) =>
            {
                int cmp = StringComparer.OrdinalIgnoreCase.Compare(a.FullName, b.FullName);
                return cmp != 0 ? cmp : StringComparer.OrdinalIgnoreCase.Compare(a.Mode, b.Mode);
            });

            var sb = new StringBuilder();
            sb.AppendLine($"Available Tests ({matchedItems.Count}):");
            int countToReport = Math.Min(matchedItems.Count, maxItems);
            for (int i = 0; i < countToReport; i++)
            {
                sb.AppendLine(FormatDiscoveredTestLine(matchedItems[i]));
            }

            if (matchedItems.Count > maxItems)
            {
                sb.AppendLine(ItemTruncationMarker);
            }

            return LimitOutput(sb.ToString().TrimEnd());
        }

        private static string FormatDiscoveredTestLine(TestDiscoveryHelper.DiscoveredItem item)
        {
            string modeLabel = string.IsNullOrWhiteSpace(item.Mode) ? "editmode" : item.Mode.Trim().ToLowerInvariant();
            var line = new StringBuilder();
            line.Append("  • ");
            line.Append(item.FullName);
            line.Append(" [");
            line.Append(modeLabel);
            line.Append(']');

            if (item.Categories != null && item.Categories.Count > 0)
            {
                var nonEmptyCategories = new List<string>();
                for (int i = 0; i < item.Categories.Count; i++)
                {
                    string cat = item.Categories[i]?.Trim();
                    if (!string.IsNullOrEmpty(cat))
                    {
                        nonEmptyCategories.Add(cat);
                    }
                }

                if (nonEmptyCategories.Count > 0)
                {
                    line.Append(" [");
                    line.Append(string.Join(", ", nonEmptyCategories));
                    line.Append(']');
                }
            }

            if (!string.IsNullOrWhiteSpace(item.AssemblyName))
            {
                line.Append(" (");
                line.Append(item.AssemblyName.Trim());
                line.Append(')');
            }

            return line.ToString();
        }

        #endregion

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
