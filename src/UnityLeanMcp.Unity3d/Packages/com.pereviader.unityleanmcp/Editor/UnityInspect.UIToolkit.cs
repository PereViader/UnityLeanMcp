#nullable disable
#pragma warning disable CS0618

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.UIElements;

namespace UnityLeanMcp
{
    public static partial class Inspect
    {
        private static readonly Dictionary<Type, PropertyInfo> s_VisualElementTextPropCache = new Dictionary<Type, PropertyInfo>();

        #region UI Toolkit Inspection

        /// <summary>
        /// Inspects active UI Toolkit UIDocument instances and their VisualElement layout trees.
        /// </summary>
        /// <param name="maxDepth">The maximum hierarchy depth to traverse.</param>
        /// <param name="maxItems">The maximum number of VisualElement nodes to display.</param>
        /// <returns>A formatted summary of UIDocument instances and their VisualElement trees.</returns>
        public static string UIDocument(int maxDepth = DefaultMaxDepth, int maxItems = DefaultMaxItems)
        {
            ClampHierarchyLimits(ref maxDepth, ref maxItems);

            UIDocument[] docs = null;
            try
            {
#if UNITY_2023_1_OR_NEWER
                docs = UnityEngine.Object.FindObjectsByType<UIDocument>(FindObjectsInactive.Include, FindObjectsSortMode.None);
#else
                docs = UnityEngine.Object.FindObjectsOfType<UIDocument>();
#endif
            }
            catch { }

            if (docs == null || docs.Length == 0)
            {
                return "(No UIDocument found in loaded scenes or Prefab Stage)";
            }

            var sb = new StringBuilder();
            int itemCount = 0;
            bool itemLimitReached = false;
            var visited = new HashSet<VisualElement>(ReferenceIdentityComparer<VisualElement>.Instance);

            for (int i = 0; i < docs.Length; i++)
            {
                if (itemCount >= maxItems)
                {
                    break;
                }

                var doc = docs[i];
                if (doc == null) continue;

                string docName = doc.name;
                string goName = doc.gameObject != null ? doc.gameObject.name : "null";
                sb.AppendLine($"=== UIDocument: \"{docName}\" (GameObject: \"{goName}\", SortingOrder: {doc.sortingOrder.ToString("F0", CultureInfo.InvariantCulture)}) ===");

                if (doc.rootVisualElement == null)
                {
                    sb.AppendLine("(rootVisualElement is null)");
                    continue;
                }

                TraverseVisualElement(doc.rootVisualElement, 0, maxDepth, maxItems, ref itemCount, ref itemLimitReached, visited, sb);
                if (i < docs.Length - 1 && itemCount < maxItems)
                {
                    sb.AppendLine();
                }
            }

            return LimitOutput(sb.ToString().TrimEnd());
        }

        /// <summary>
        /// Inspects a specific UI Toolkit UIDocument and traverses its rootVisualElement tree.
        /// </summary>
        public static string UIDocument(UIDocument document, int maxDepth = DefaultMaxDepth, int maxItems = DefaultMaxItems)
        {
            if (!ReferenceEquals(document, null) && document == null)
            {
                return "(UIDocument is destroyed)";
            }
            if (document == null)
            {
                return "null";
            }

            ClampHierarchyLimits(ref maxDepth, ref maxItems);

            var sb = new StringBuilder();
            string docName = document.name;
            string goName = document.gameObject != null ? document.gameObject.name : "null";
            sb.AppendLine($"=== UIDocument: \"{docName}\" (GameObject: \"{goName}\", SortingOrder: {document.sortingOrder.ToString("F0", CultureInfo.InvariantCulture)}) ===");

            if (document.rootVisualElement == null)
            {
                sb.AppendLine("(rootVisualElement is null)");
                return sb.ToString().TrimEnd();
            }

            int itemCount = 0;
            bool itemLimitReached = false;
            var visited = new HashSet<VisualElement>(ReferenceIdentityComparer<VisualElement>.Instance);
            TraverseVisualElement(document.rootVisualElement, 0, maxDepth, maxItems, ref itemCount, ref itemLimitReached, visited, sb);

            return LimitOutput(sb.ToString().TrimEnd());
        }

        /// <summary>
        /// Inspects a specific UI Toolkit VisualElement and traverses its children.
        /// </summary>
        public static string UIDocument(VisualElement root, int maxDepth = DefaultMaxDepth, int maxItems = DefaultMaxItems)
        {
            if (root == null)
            {
                return "null";
            }

            ClampHierarchyLimits(ref maxDepth, ref maxItems);

            var sb = new StringBuilder();
            int itemCount = 0;
            bool itemLimitReached = false;
            var visited = new HashSet<VisualElement>(ReferenceIdentityComparer<VisualElement>.Instance);
            TraverseVisualElement(root, 0, maxDepth, maxItems, ref itemCount, ref itemLimitReached, visited, sb);

            return LimitOutput(sb.ToString().TrimEnd());
        }

        /// <summary>
        /// Inspects the UI Toolkit UIDocument component on the specified GameObject.
        /// </summary>
        public static string UIDocument(GameObject gameObject, int maxDepth = DefaultMaxDepth, int maxItems = DefaultMaxItems)
        {
            if (!ReferenceEquals(gameObject, null) && gameObject == null)
            {
                return "(GameObject is destroyed)";
            }
            if (gameObject == null)
            {
                return "null";
            }

            UIDocument doc = null;
            try
            {
                doc = gameObject.GetComponent<UIDocument>();
            }
            catch { }

            if (doc == null)
            {
                return $"(GameObject \"{gameObject.name}\" has no UIDocument component)";
            }

            return UIDocument(doc, maxDepth, maxItems);
        }

        private static void TraverseVisualElement(
            VisualElement current,
            int depth,
            int maxDepth,
            int maxItems,
            ref int itemCount,
            ref bool itemLimitReached,
            HashSet<VisualElement> visited,
            StringBuilder sb,
            string prefix = "")
        {
            if (current == null) return;

            string indent = prefix + new string(' ', depth * 2);

            if (depth > maxDepth)
            {
                sb.AppendLine($"{indent}{DepthTruncationMarker}");
                return;
            }

            if (!visited.Add(current))
            {
                sb.AppendLine($"{indent}{CycleTruncationMarker}");
                return;
            }

            try
            {
                if (itemCount >= maxItems)
                {
                    AppendItemLimitMarkerOnce(sb, indent, ref itemLimitReached);
                    return;
                }

                itemCount++;

                string typeName = current.GetType().Name;
                string nameStr = !string.IsNullOrEmpty(current.name) ? $" #{current.name}" : "";

                var classes = current.GetClasses();
                string classStr = "";
                if (classes != null)
                {
                    var classList = classes.ToList();
                    if (classList.Count > 0)
                    {
                        classStr = " ." + string.Join(".", classList);
                    }
                }

                var flags = new List<string>();
                if (!current.visible) flags.Add("hidden");
                if (!current.enabledSelf) flags.Add("disabled");
                string flagStr = flags.Count > 0 ? $" [{string.Join(", ", flags)}]" : "";

                var layout = current.layout;
                var layoutParts = new List<string>();
                if (!float.IsNaN(layout.x) && !float.IsNaN(layout.y) && (layout.x != 0 || layout.y != 0))
                {
                    layoutParts.Add($"pos: {layout.x.ToString("F0", CultureInfo.InvariantCulture)}, {layout.y.ToString("F0", CultureInfo.InvariantCulture)}");
                }
                if (!float.IsNaN(layout.width) && !float.IsNaN(layout.height) && (layout.width != 0 || layout.height != 0))
                {
                    layoutParts.Add($"size: {layout.width.ToString("F0", CultureInfo.InvariantCulture)}x{layout.height.ToString("F0", CultureInfo.InvariantCulture)}");
                }

                if (current is TextElement te && !string.IsNullOrEmpty(te.text))
                {
                    layoutParts.Add($"text: \"{FormatTextSummary(te.text, 30)}\"");
                }
                else
                {
                    try
                    {
                        var textProp = GetCachedProperty(s_VisualElementTextPropCache, current.GetType(), "text", typeof(string));
                        if (textProp != null)
                        {
                            string t = textProp.GetValue(current) as string;
                            if (!string.IsNullOrEmpty(t))
                            {
                                layoutParts.Add($"text: \"{FormatTextSummary(t, 30)}\"");
                            }
                        }
                    }
                    catch { }
                }

                string detailsStr = layoutParts.Count > 0 ? $" ({string.Join(" | ", layoutParts)})" : "";
                sb.AppendLine($"{indent}{typeName}{nameStr}{classStr}{flagStr}{detailsStr}");

                int childCount = 0;
                try { childCount = current.childCount; } catch { }

                if (depth >= maxDepth)
                {
                    if (childCount > 0)
                    {
                        sb.AppendLine($"{indent}  {DepthTruncationMarker}");
                    }
                    return;
                }

                for (int i = 0; i < childCount; i++)
                {
                    if (itemCount >= maxItems)
                    {
                        AppendItemLimitMarkerOnce(sb, indent + "  ", ref itemLimitReached);
                        break;
                    }

                    VisualElement child = null;
                    try { child = current[i]; } catch { }

                    if (child != null)
                    {
                        TraverseVisualElement(child, depth + 1, maxDepth, maxItems, ref itemCount, ref itemLimitReached, visited, sb, prefix);
                    }
                }
            }
            finally
            {
                visited.Remove(current);
            }
        }

        #endregion
    }
}
