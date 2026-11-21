#nullable disable
#pragma warning disable CS0618

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityLeanMcp
{
    public static partial class Inspect
    {
        private static readonly Dictionary<Type, PropertyInfo> s_RaycastTargetPropCache = new Dictionary<Type, PropertyInfo>();
        private static readonly Dictionary<Type, PropertyInfo> s_UiTextPropCache = new Dictionary<Type, PropertyInfo>();

        private enum SceneBfsDecision
        {
            Continue,
            SkipChildren,
            Stop
        }

        #region Hierarchy & Scene Inspection

        /// <summary>
        /// Inspects the active scene hierarchy or all loaded scenes if multiple are open.
        /// </summary>
        public static string Hierarchy()
        {
            return Hierarchy(root: (GameObject)null, maxDepth: DefaultMaxDepth, maxItems: DefaultMaxItems, includeUiDetails: false);
        }

        /// <summary>
        /// Inspects the active scene hierarchy, optionally including compact uGUI layout details (rects, anchors, raycast targets).
        /// </summary>
        public static string Hierarchy(bool includeUiDetails)
        {
            return Hierarchy(root: (GameObject)null, maxDepth: DefaultMaxDepth, maxItems: DefaultMaxItems, includeUiDetails: includeUiDetails);
        }

        /// <summary>
        /// Inspects the hierarchy starting from a specific depth and item limit, optionally including uGUI layout details.
        /// </summary>
        public static string Hierarchy(int maxDepth, int maxItems = DefaultMaxItems, bool includeUiDetails = false)
        {
            return Hierarchy(root: (GameObject)null, maxDepth: maxDepth, maxItems: maxItems, includeUiDetails: includeUiDetails);
        }

        /// <summary>
        /// Inspects the hierarchy starting from a specific root Transform, optionally including uGUI layout details.
        /// </summary>
        public static string Hierarchy(Transform root, int maxDepth = DefaultMaxDepth, int maxItems = DefaultMaxItems, bool includeUiDetails = false)
        {
            if (ReferenceEquals(root, null)) return "(Transform is null)";
            if (root == null) return "(Transform is destroyed)";

            GameObject go = null;
            try { go = root.gameObject; } catch { }
            if (go == null) return "(Transform has no GameObject)";

            return Hierarchy(go, maxDepth, maxItems, includeUiDetails);
        }

        /// <summary>
        /// Inspects the hierarchy of a specific loaded scene by name or path, or the active Prefab Stage, optionally including uGUI layout details.
        /// </summary>
        public static string Hierarchy(string sceneName, int maxDepth = DefaultMaxDepth, int maxItems = DefaultMaxItems, bool includeUiDetails = false)
        {
            if (string.IsNullOrWhiteSpace(sceneName))
            {
                return Hierarchy(root: (GameObject)null, maxDepth: maxDepth, maxItems: maxItems, includeUiDetails: includeUiDetails);
            }

            string cleanQuery = sceneName.Trim().Replace('\\', '/');
            string cleanNameWithoutExt = Path.GetFileNameWithoutExtension(cleanQuery);

            PrefabStage stage = GetCurrentPrefabStageSafe();
            if (stage != null && MatchesPrefabStageQuery(stage, cleanQuery, cleanNameWithoutExt))
            {
                return InspectPrefabStageHierarchy(stage, maxDepth, maxItems, includeUiDetails);
            }

            int count = GetSceneCountSafe();
            for (int i = 0; i < count; i++)
            {
                Scene s;
                try { s = SceneManager.GetSceneAt(i); }
                catch { continue; }

                if (string.Equals(s.name, cleanQuery, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(s.name, cleanNameWithoutExt, StringComparison.OrdinalIgnoreCase) ||
                    (!string.IsNullOrEmpty(s.path) && string.Equals(s.path.Replace('\\', '/'), cleanQuery, StringComparison.OrdinalIgnoreCase)))
                {
                    return InspectScene(s, maxDepth, maxItems, includeUiDetails);
                }
            }

            return $"(Scene \"{sceneName}\" is not currently loaded)";
        }

        /// <summary>
        /// Inspects the hierarchy starting from a specific root GameObject, optionally including uGUI layout details.
        /// </summary>
        public static string Hierarchy(GameObject root, int maxDepth = DefaultMaxDepth, int maxItems = DefaultMaxItems, bool includeUiDetails = false)
        {
            if (!ReferenceEquals(root, null) && root == null) return "(GameObject is destroyed)";

            ClampHierarchyLimits(ref maxDepth, ref maxItems);
            var sb = new StringBuilder();
            int itemCount = 0;
            bool itemLimitReached = false;
            var visited = new HashSet<Transform>(ReferenceIdentityComparer<Transform>.Instance);

            if (root != null)
            {
                if (root.transform == null) return "(GameObject has no Transform)";
                TraverseHierarchy(root.transform, 0, maxDepth, maxItems, ref itemCount, ref itemLimitReached, visited, sb, includeUiDetails);
                return LimitOutput(sb.ToString().TrimEnd());
            }

            int sceneCount = GetSceneCountSafe();
            PrefabStage stage = GetCurrentPrefabStageSafe();
            if (stage == null && sceneCount == 0) return "(No scenes currently loaded)";

            if (stage != null)
            {
                AppendPrefabStageHierarchy(stage, maxDepth, maxItems, ref itemCount, ref itemLimitReached, visited, sb, includeUiDetails);
            }

            for (int s = 0; s < sceneCount; s++)
            {
                if (itemCount >= maxItems) break;

                Scene scene;
                try { scene = SceneManager.GetSceneAt(s); }
                catch { continue; }

                if (!scene.isLoaded)
                {
                    sb.AppendLine($"Scene \"{scene.name}\" (not loaded)");
                    continue;
                }

                if (stage != null || sceneCount > 1)
                {
                    sb.AppendLine($"=== Scene: \"{scene.name}\" (roots: {scene.rootCount}) ===");
                }

                GameObject[] rootObjects = GetRootGameObjectsSafe(scene);
                if (rootObjects == null || rootObjects.Length == 0)
                {
                    sb.AppendLine($"Scene \"{scene.name}\" (0 root GameObjects - empty scene)");
                    continue;
                }

                TraverseRootObjects(rootObjects, maxDepth, maxItems, ref itemCount, ref itemLimitReached, visited, sb, includeUiDetails);
            }

            if (sb.Length == 0)
            {
                return stage != null
                    ? "(No GameObjects found in Prefab Stage or loaded scenes)"
                    : "(No GameObjects found in loaded scenes)";
            }

            return LimitOutput(sb.ToString().TrimEnd());
        }

        private static bool MatchesPrefabStageQuery(PrefabStage stage, string cleanQuery, string cleanNameWithoutExt)
        {
            string stageAssetPath = GetPrefabStageAssetPath(stage).Replace('\\', '/');
            string stageFileNameWithoutExt = Path.GetFileNameWithoutExtension(stageAssetPath);
            string rootName = stage.prefabContentsRoot != null ? stage.prefabContentsRoot.name : null;

            return string.Equals(cleanQuery, "Prefab", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(cleanQuery, "PrefabStage", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(cleanQuery, "Prefab Stage", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(cleanQuery, stageAssetPath, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(cleanNameWithoutExt, stageFileNameWithoutExt, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrEmpty(rootName) && string.Equals(cleanQuery, rootName, StringComparison.OrdinalIgnoreCase));
        }

        private static string InspectScene(Scene scene, int maxDepth, int maxItems, bool includeUiDetails = false)
        {
            ClampHierarchyLimits(ref maxDepth, ref maxItems);
            if (!scene.isLoaded) return $"Scene \"{scene.name}\" (not loaded)";

            GameObject[] rootObjects = GetRootGameObjectsSafe(scene);
            if (rootObjects == null || rootObjects.Length == 0)
                return $"Scene \"{scene.name}\" (0 root GameObjects - empty scene)";

            var sb = new StringBuilder();
            sb.AppendLine($"Scene \"{scene.name}\" (roots: {rootObjects.Length}):");
            int itemCount = 0;
            bool itemLimitReached = false;
            var visited = new HashSet<Transform>(ReferenceIdentityComparer<Transform>.Instance);

            TraverseRootObjects(rootObjects, maxDepth, maxItems, ref itemCount, ref itemLimitReached, visited, sb, includeUiDetails);
            return itemCount == 0
                ? $"Scene \"{scene.name}\" (0 valid root GameObjects)"
                : LimitOutput(sb.ToString().TrimEnd());
        }

        private static string InspectPrefabStageHierarchy(PrefabStage stage, int maxDepth, int maxItems, bool includeUiDetails = false)
        {
            ClampHierarchyLimits(ref maxDepth, ref maxItems);
            var sb = new StringBuilder();
            int itemCount = 0;
            bool itemLimitReached = false;
            var visited = new HashSet<Transform>(ReferenceIdentityComparer<Transform>.Instance);

            bool hasRoot = AppendPrefabStageHierarchy(stage, maxDepth, maxItems, ref itemCount, ref itemLimitReached, visited, sb, includeUiDetails);
            return hasRoot ? LimitOutput(sb.ToString().TrimEnd()) : sb.ToString().TrimEnd();
        }

        private static bool AppendPrefabStageHierarchy(
            PrefabStage stage,
            int maxDepth,
            int maxItems,
            ref int itemCount,
            ref bool itemLimitReached,
            HashSet<Transform> visited,
            StringBuilder sb,
            bool includeUiDetails)
        {
            string assetPath = GetPrefabStageAssetPath(stage);
            var rootGo = stage.prefabContentsRoot;
            string rootName = rootGo != null ? rootGo.name : (!string.IsNullOrEmpty(assetPath) ? Path.GetFileNameWithoutExtension(assetPath) : "(empty)");

            sb.AppendLine($"=== Prefab Stage: \"{rootName}\" (Asset: \"{assetPath}\") ===");
            if (rootGo != null && rootGo.transform != null)
            {
                TraverseHierarchy(rootGo.transform, 0, maxDepth, maxItems, ref itemCount, ref itemLimitReached, visited, sb, includeUiDetails);
                return true;
            }

            sb.AppendLine("Prefab Stage (0 root GameObjects - empty prefab)");
            return false;
        }

        private static void TraverseRootObjects(
            GameObject[] rootObjects,
            int maxDepth,
            int maxItems,
            ref int itemCount,
            ref bool itemLimitReached,
            HashSet<Transform> visited,
            StringBuilder sb,
            bool includeUiDetails)
        {
            for (int i = 0; i < rootObjects.Length; i++)
            {
                if (itemCount >= maxItems)
                {
                    AppendItemLimitMarkerOnce(sb, "", ref itemLimitReached);
                    break;
                }

                var rootGo = rootObjects[i];
                if (rootGo != null && rootGo.transform != null)
                {
                    TraverseHierarchy(rootGo.transform, 0, maxDepth, maxItems, ref itemCount, ref itemLimitReached, visited, sb, includeUiDetails);
                }
            }
        }

        private static void TraverseHierarchy(
            Transform current,
            int depth,
            int maxDepth,
            int maxItems,
            ref int itemCount,
            ref bool itemLimitReached,
            HashSet<Transform> visited,
            StringBuilder sb,
            bool includeUiDetails = false)
        {
            if (current == null) return;

            string indent = new string(' ', depth * 2);
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

                GameObject go = null;
                try { go = current.gameObject; } catch { }
                if (go == null)
                {
                    sb.AppendLine($"{indent}(Missing GameObject)");
                    return;
                }

                string goName = "Unnamed";
                bool activeInHierarchy = false;
                bool activeSelf = false;
                try { goName = go.name; } catch { }
                try { activeInHierarchy = go.activeInHierarchy; activeSelf = go.activeSelf; } catch { }

                string activeStatus = activeInHierarchy ? "[active]" : (!activeSelf ? "[inactive]" : "[inactive (parent disabled)]");

                Component[] comps = null;
                try { comps = go.GetComponents<Component>(); } catch { }

                string compListStr = FormatComponentList(comps);
                string uiSuffix = includeUiDetails ? GetUiDetailsSuffix(current, comps) : "";
                sb.AppendLine($"{indent}{goName} {activeStatus} [{compListStr}]{uiSuffix}");

                int childCount = 0;
                try { childCount = current.childCount; } catch { }

                if (depth >= maxDepth)
                {
                    if (childCount > 0) sb.AppendLine($"{indent}  {DepthTruncationMarker}");
                    return;
                }

                for (int i = 0; i < childCount; i++)
                {
                    if (itemCount >= maxItems)
                    {
                        AppendItemLimitMarkerOnce(sb, indent + "  ", ref itemLimitReached);
                        break;
                    }

                    Transform child = null;
                    try { child = current.GetChild(i); } catch { }
                    if (child != null)
                    {
                        TraverseHierarchy(child, depth + 1, maxDepth, maxItems, ref itemCount, ref itemLimitReached, visited, sb, includeUiDetails);
                    }
                }
            }
            finally
            {
                visited.Remove(current);
            }
        }

        private static string FormatComponentList(Component[] comps)
        {
            if (comps == null || comps.Length == 0) return "No Components";

            var compNames = new List<string>(comps.Length);
            for (int c = 0; c < comps.Length; c++)
            {
                var comp = comps[c];
                if (comp == null)
                {
                    compNames.Add("Missing Script");
                }
                else
                {
                    try { compNames.Add(comp.GetType().Name); }
                    catch { compNames.Add("Unknown Component"); }
                }
            }

            return compNames.Count > 0 ? string.Join(", ", compNames) : "No Components";
        }

        private static string GetUiDetailsSuffix(Transform current, Component[] comps)
        {
            if (current == null) return "";

            var parts = new List<string>();
            if (comps != null)
            {
                for (int i = 0; i < comps.Length; i++)
                {
                    if (comps[i] is Canvas c) { parts.Add($"canvas: {c.renderMode}, order={c.sortingOrder}"); break; }
                }
            }

            if (current is RectTransform rt)
            {
                parts.Add($"pos: {rt.anchoredPosition.x.ToString("F0", CultureInfo.InvariantCulture)}, {rt.anchoredPosition.y.ToString("F0", CultureInfo.InvariantCulture)}");
                parts.Add($"size: {rt.rect.width.ToString("F0", CultureInfo.InvariantCulture)}x{rt.rect.height.ToString("F0", CultureInfo.InvariantCulture)}");

                if (rt.anchorMin.x != rt.anchorMax.x || rt.anchorMin.y != rt.anchorMax.y)
                {
                    parts.Add($"anchors: [{rt.anchorMin.x.ToString("F1", CultureInfo.InvariantCulture)}, {rt.anchorMin.y.ToString("F1", CultureInfo.InvariantCulture)} - {rt.anchorMax.x.ToString("F1", CultureInfo.InvariantCulture)}, {rt.anchorMax.y.ToString("F1", CultureInfo.InvariantCulture)}]");
                }
            }

            if (comps != null)
            {
                for (int i = 0; i < comps.Length; i++)
                {
                    var comp = comps[i];
                    if (comp == null) continue;
                    Type compType = comp.GetType();
                    try
                    {
                        var rtProp = GetCachedProperty(s_RaycastTargetPropCache, compType, "raycastTarget", typeof(bool));
                        if (rtProp != null && !(bool)rtProp.GetValue(comp)) parts.Add("raycast: false");
                    }
                    catch { }

                    try
                    {
                        if (!compType.Name.Contains("InputField"))
                        {
                            var textProp = GetCachedProperty(s_UiTextPropCache, compType, "text", typeof(string));
                            if (textProp?.GetValue(comp) is string txt && !string.IsNullOrEmpty(txt))
                                parts.Add($"text: \"{FormatTextSummary(txt, 25)}\"");
                        }
                    }
                    catch { }
                }
            }

            return parts.Count > 0 ? $" ({string.Join(" | ", parts)})" : "";
        }

        private static void TraverseLoadedScenesBfs(int sceneCount, Func<Scene, Transform, GameObject, SceneBfsDecision> visitor)
        {
            for (int s = 0; s < sceneCount; s++)
            {
                Scene scene;
                try { scene = SceneManager.GetSceneAt(s); }
                catch { continue; }

                if (!scene.isLoaded) continue;

                GameObject[] rootObjects = GetRootGameObjectsSafe(scene);
                if (TraverseRootGameObjectsBfs(rootObjects, (current, go) => visitor(scene, current, go)) == SceneBfsDecision.Stop)
                {
                    return;
                }
            }
        }

        private static void TraverseActiveHierarchiesBfs(
            PrefabStage stage,
            int sceneCount,
            Func<string, bool, Transform, GameObject, SceneBfsDecision> visitor)
        {
            if (stage != null)
            {
                GameObject prefabRoot = null;
                try { prefabRoot = stage.prefabContentsRoot; } catch { }
                if (prefabRoot != null)
                {
                    string assetPath = GetPrefabStageAssetPath(stage);
                    if (TraverseRootGameObjectsBfs(new[] { prefabRoot }, (current, go) => visitor(assetPath, true, current, go)) == SceneBfsDecision.Stop)
                    {
                        return;
                    }
                }
            }

            TraverseLoadedScenesBfs(sceneCount, (scene, current, go) => visitor(scene.name, false, current, go));
        }

        private static SceneBfsDecision TraverseRootGameObjectsBfs(GameObject[] rootObjects, Func<Transform, GameObject, SceneBfsDecision> visitor)
        {
            if (rootObjects == null || rootObjects.Length == 0) return SceneBfsDecision.Continue;

            var queue = new Queue<Transform>();
            for (int i = 0; i < rootObjects.Length; i++)
            {
                var rootGo = rootObjects[i];
                if (rootGo != null && rootGo.transform != null) queue.Enqueue(rootGo.transform);
            }

            var visited = new HashSet<Transform>(ReferenceIdentityComparer<Transform>.Instance);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (current == null || !visited.Add(current)) continue;

                GameObject go = null;
                try { go = current.gameObject; } catch { }
                if (go == null) continue;

                SceneBfsDecision decision = visitor(current, go);
                if (decision == SceneBfsDecision.Stop) return SceneBfsDecision.Stop;
                if (decision == SceneBfsDecision.SkipChildren) continue;

                int childCount = 0;
                try { childCount = current.childCount; } catch { }
                for (int c = 0; c < childCount; c++)
                {
                    Transform child = null;
                    try { child = current.GetChild(c); } catch { }
                    if (child != null) queue.Enqueue(child);
                }
            }

            return SceneBfsDecision.Continue;
        }

        /// <summary>
        /// Finds GameObjects matching a wildcard or substring pattern in an active Prefab Stage and loaded scenes.
        /// </summary>
        public static string Find(string namePattern, bool includeInactive = true, int maxItems = DefaultMaxItems)
        {
            maxItems = Math.Max(1, maxItems);
            PrefabStage stage = GetCurrentPrefabStageSafe();
            int sceneCount = GetSceneCountSafe();
            if (stage == null && sceneCount == 0) return "(No scenes currently loaded)";

            string pattern = namePattern?.Trim() ?? "";
            Regex wildcardRegex = BuildWildcardRegex(pattern);

            var matches = new List<string>();
            bool itemLimitReached = false;

            TraverseActiveHierarchiesBfs(stage, sceneCount, (containerName, isPrefabStage, current, go) =>
            {
                bool active = false;
                try { active = go.activeInHierarchy; } catch { }
                if (!includeInactive && !active) return SceneBfsDecision.SkipChildren;

                string goName = null;
                try { goName = go.name; } catch { }

                if (MatchesWildcardOrSubstring(goName, pattern, wildcardRegex))
                {
                    if (matches.Count >= maxItems)
                    {
                        itemLimitReached = true;
                        return SceneBfsDecision.Stop;
                    }

                    string path = GetHierarchyPath(current);
                    string status = active ? "active" : "inactive";
                    string containerLabel = isPrefabStage ? $"prefabStage: \"{containerName}\"" : $"scene: \"{containerName}\"";
                    matches.Add($"  • {goName} (path: \"{path}\", {containerLabel}, {status})");
                }

                return SceneBfsDecision.Continue;
            });

            if (matches.Count == 0)
            {
                string scope = stage != null ? "Prefab Stage or loaded scenes" : "loaded scenes";
                return string.IsNullOrEmpty(pattern)
                    ? $"(No GameObjects found in {scope})"
                    : $"(No GameObjects found matching '{pattern}' in {scope})";
            }

            var sb = new StringBuilder();
            sb.AppendLine($"GameObjects matching '{pattern}' ({matches.Count}):");
            for (int i = 0; i < matches.Count; i++) sb.AppendLine(matches[i]);
            if (itemLimitReached) sb.AppendLine(ItemTruncationMarker);

            return LimitOutput(sb.ToString().TrimEnd());
        }

        /// <summary>
        /// Finds GameObjects carrying a component matching the given type name, full type name, base type, interface, or wildcard pattern
        /// in an active Prefab Stage and loaded scenes.
        /// </summary>
        public static string FindByComponent(string typeName, bool includeInactive = true, int maxItems = DefaultMaxItems)
        {
            maxItems = Math.Max(1, maxItems);
            string query = typeName?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(query)) return "(Component type name is required)";

            PrefabStage stage = GetCurrentPrefabStageSafe();
            int sceneCount = GetSceneCountSafe();
            if (stage == null && sceneCount == 0) return "(No scenes currently loaded)";

            Regex wildcardRegex = BuildWildcardRegex(query);
            var matches = new List<string>();
            bool itemLimitReached = false;

            TraverseActiveHierarchiesBfs(stage, sceneCount, (containerName, isPrefabStage, current, go) =>
            {
                bool active = false;
                try { active = go.activeInHierarchy; } catch { }
                if (!includeInactive && !active) return SceneBfsDecision.SkipChildren;

                Component[] components = null;
                try { components = go.GetComponents<Component>(); } catch { }
                if (components == null || components.Length == 0) return SceneBfsDecision.Continue;

                List<string> matchedTypeNames = null;
                for (int c = 0; c < components.Length; c++)
                {
                    var comp = components[c];
                    if (ReferenceEquals(comp, null) || comp == null) continue;

                    Type compType = comp.GetType();
                    if (MatchesComponentType(compType, query, wildcardRegex))
                    {
                        matchedTypeNames ??= new List<string>();
                        matchedTypeNames.Add(compType.Name);
                    }
                }

                if (matchedTypeNames != null)
                {
                    if (matches.Count >= maxItems)
                    {
                        itemLimitReached = true;
                        return SceneBfsDecision.Stop;
                    }

                    string goName = null;
                    try { goName = go.name; } catch { }
                    string path = GetHierarchyPath(current);
                    string status = active ? "active" : "inactive";
                    string containerLabel = isPrefabStage ? $"prefabStage: \"{containerName}\"" : $"scene: \"{containerName}\"";
                    matches.Add($"  • {goName} [{string.Join(", ", matchedTypeNames)}] (path: \"{path}\", {containerLabel}, {status})");
                }

                return SceneBfsDecision.Continue;
            });

            if (matches.Count == 0)
            {
                string scope = stage != null ? "Prefab Stage or loaded scenes" : "loaded scenes";
                return $"(No GameObjects with component '{query}' found in {scope})";
            }

            var sb = new StringBuilder();
            sb.AppendLine($"GameObjects with component '{query}' ({matches.Count}):");
            for (int i = 0; i < matches.Count; i++) sb.AppendLine(matches[i]);
            if (itemLimitReached) sb.AppendLine(ItemTruncationMarker);

            return LimitOutput(sb.ToString().TrimEnd());
        }

        private static bool MatchesComponentType(Type compType, string query, Regex wildcardRegex)
        {
            if (compType == null) return false;

            for (Type current = compType; current != null && current != typeof(UnityEngine.Object) && current != typeof(object); current = current.BaseType)
            {
                if (MatchesTypeName(current, query, wildcardRegex)) return true;
            }

            try
            {
                var interfaces = compType.GetInterfaces();
                for (int i = 0; i < interfaces.Length; i++)
                {
                    if (MatchesTypeName(interfaces[i], query, wildcardRegex)) return true;
                }
            }
            catch { }

            return false;
        }

        private static bool MatchesTypeName(Type type, string query, Regex wildcardRegex)
        {
            if (type == null) return false;
            string shortName = type.Name;
            string fullName = type.FullName;

            if (wildcardRegex != null)
            {
                return (!string.IsNullOrEmpty(shortName) && wildcardRegex.IsMatch(shortName)) ||
                       (!string.IsNullOrEmpty(fullName) && wildcardRegex.IsMatch(fullName));
            }

            return string.Equals(shortName, query, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(fullName, query, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Returns active scene name, path, root GameObject count, isDirty state, and loaded scenes summary,
        /// as well as active Prefab Stage details if open.
        /// </summary>
        public static string SceneState()
        {
            PrefabStage stage = GetCurrentPrefabStageSafe();
            int sceneCount = GetSceneCountSafe();
            if (stage == null && sceneCount == 0) return "(No scenes currently loaded)";

            var sb = new StringBuilder();
            if (stage != null)
            {
                AppendPrefabStageMetadata(sb, stage);
                if (sceneCount > 0) sb.AppendLine();
            }

            if (sceneCount == 0 && stage != null) return LimitOutput(sb.ToString().TrimEnd());

            Scene activeScene = default;
            try { activeScene = SceneManager.GetActiveScene(); } catch { }

            string activeName = !string.IsNullOrEmpty(activeScene.name) ? activeScene.name : "(Untitled)";
            string activePath = !string.IsNullOrEmpty(activeScene.path) ? activeScene.path : "(Unsaved)";

            sb.AppendLine($"Active Scene: \"{activeName}\"");
            sb.AppendLine($"  Path: {activePath}");
            sb.AppendLine($"  Root GameObjects: {activeScene.rootCount}");
            sb.AppendLine($"  Is Dirty: {(activeScene.isDirty ? "true" : "false")}");
            sb.AppendLine($"  Is Loaded: {(activeScene.isLoaded ? "true" : "false")}");
            sb.AppendLine();
            sb.AppendLine($"Loaded Scenes ({sceneCount}):");

            for (int i = 0; i < sceneCount; i++)
            {
                Scene s;
                try { s = SceneManager.GetSceneAt(i); }
                catch { continue; }

                string sName = !string.IsNullOrEmpty(s.name) ? s.name : "(Untitled)";
                string sPath = !string.IsNullOrEmpty(s.path) ? s.path : "(Unsaved)";
                string activeMarker = (activeScene.IsValid() && s.handle == activeScene.handle) ? " [Active]" : "";
                string dirtyMarker = s.isDirty ? ", dirty" : "";
                string loadedMarker = s.isLoaded ? "" : ", not loaded";

                sb.AppendLine($"  {i + 1}. \"{sName}\"{activeMarker} (roots: {s.rootCount}{dirtyMarker}{loadedMarker}, path: {sPath})");
            }

            return LimitOutput(sb.ToString().TrimEnd());
        }

        /// <summary>
        /// Inspects the currently active Prefab Stage in the Unity Editor, returning its asset path,
        /// stage mode, root GameObject, dirty state, auto-save status, and internal hierarchy.
        /// Returns a notification if no Prefab Stage is open.
        /// </summary>
        public static string PrefabStage()
        {
            return PrefabStage(DefaultMaxDepth, DefaultMaxItems);
        }

        /// <summary>
        /// Inspects the currently active Prefab Stage with custom depth and item limits.
        /// </summary>
        public static string PrefabStage(int maxDepth, int maxItems = DefaultMaxItems)
        {
            PrefabStage stage = GetCurrentPrefabStageSafe();
            if (stage == null) return "(No active Prefab Stage open in Editor)";

            ClampHierarchyLimits(ref maxDepth, ref maxItems);
            var sb = new StringBuilder();
            AppendPrefabStageMetadata(sb, stage);

            var rootGo = stage.prefabContentsRoot;
            if (rootGo != null && rootGo.transform != null)
            {
                sb.AppendLine();
                sb.AppendLine("Hierarchy:");
                int itemCount = 0;
                bool itemLimitReached = false;
                var visited = new HashSet<Transform>(ReferenceIdentityComparer<Transform>.Instance);
                TraverseHierarchy(rootGo.transform, 1, maxDepth, maxItems, ref itemCount, ref itemLimitReached, visited, sb);
            }
            else
            {
                sb.AppendLine();
                sb.AppendLine("Hierarchy: (No root GameObject)");
            }

            return LimitOutput(sb.ToString().TrimEnd());
        }

        private static void AppendPrefabStageMetadata(StringBuilder sb, PrefabStage stage)
        {
            string assetPath = GetPrefabStageAssetPath(stage);
            var rootGo = stage.prefabContentsRoot;
            string rootName = rootGo != null ? rootGo.name : "(null)";

            sb.AppendLine("Active Prefab Stage:");
            sb.AppendLine($"  Asset Path: {assetPath}");
            sb.AppendLine($"  Mode: {stage.mode}");
            sb.AppendLine($"  Root GameObject: \"{rootName}\"");
            sb.AppendLine($"  Is Dirty: {(GetPrefabStageDirty(stage) ? "true" : "false")}");
            sb.AppendLine($"  Auto Save: {(GetPrefabStageAutoSave(stage) ? "true" : "false")}");
        }

        private static int GetSceneCountSafe() { try { return SceneManager.sceneCount; } catch { return 0; } }
        private static GameObject[] GetRootGameObjectsSafe(Scene scene) { try { return scene.GetRootGameObjects(); } catch { return null; } }
        private static PrefabStage GetCurrentPrefabStageSafe() { try { return PrefabStageUtility.GetCurrentPrefabStage(); } catch { return null; } }

        private static string GetPrefabStageAssetPath(PrefabStage stage)
        {
            if (stage == null) return string.Empty;
            try { if (!string.IsNullOrEmpty(stage.assetPath)) return stage.assetPath.Replace('\\', '/'); } catch { }
            try
            {
                var prop = typeof(PrefabStage).GetProperty("prefabAssetPath", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (prop?.GetValue(stage, null) is string val && !string.IsNullOrEmpty(val)) return val.Replace('\\', '/');
            }
            catch { }
            return "(Unknown)";
        }

        private static bool GetPrefabStageDirty(PrefabStage stage)
        {
            if (stage == null) return false;
            try { return stage.scene.isDirty; } catch { return false; }
        }

        private static bool GetPrefabStageAutoSave(PrefabStage stage)
        {
            if (stage == null) return false;
            try
            {
                var prop = typeof(PrefabStage).GetProperty("autoSave", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (prop?.GetValue(stage, null) is bool b) return b;
            }
            catch { }
            try { return EditorSettings.prefabModeAllowAutoSave; } catch { return false; }
        }

        #endregion
    }
}
