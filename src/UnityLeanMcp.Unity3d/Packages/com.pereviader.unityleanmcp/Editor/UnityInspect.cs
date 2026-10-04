#nullable disable
#pragma warning disable CS0618

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityLeanMcp
{
    /// <summary>
    /// Built-in, token-bounded scene and object introspection helpers for UnityLeanMcp.
    /// Callable directly within unity_eval to inspect scene hierarchy, GameObject components,
    /// project assets, and active selection without context bloat or reflection boilerplate.
    /// </summary>
    public static class Inspect
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

        #region Hierarchy & Scene Inspection

        /// <summary>
        /// Inspects the active scene hierarchy or all loaded scenes if multiple are open.
        /// </summary>
        public static string Hierarchy()
        {
            return Hierarchy(root: (GameObject)null, maxDepth: DefaultMaxDepth, maxItems: DefaultMaxItems);
        }

        /// <summary>
        /// Inspects the hierarchy starting from a specific depth and item limit.
        /// </summary>
        public static string Hierarchy(int maxDepth, int maxItems = DefaultMaxItems)
        {
            return Hierarchy(root: (GameObject)null, maxDepth: maxDepth, maxItems: maxItems);
        }

        /// <summary>
        /// Inspects the hierarchy starting from a specific root Transform.
        /// </summary>
        public static string Hierarchy(Transform root, int maxDepth = DefaultMaxDepth, int maxItems = DefaultMaxItems)
        {
            if (ReferenceEquals(root, null))
            {
                return "(Transform is null)";
            }
            if (root == null)
            {
                return "(Transform is destroyed)";
            }
            GameObject go = null;
            try
            {
                go = root.gameObject;
            }
            catch { }
            if (go == null)
            {
                return "(Transform has no GameObject)";
            }
            return Hierarchy(go, maxDepth, maxItems);
        }

        /// <summary>
        /// Inspects the hierarchy of a specific loaded scene by name or path.
        /// </summary>
        public static string Hierarchy(string sceneName, int maxDepth = DefaultMaxDepth, int maxItems = DefaultMaxItems)
        {
            if (string.IsNullOrWhiteSpace(sceneName))
            {
                return Hierarchy(root: (GameObject)null, maxDepth: maxDepth, maxItems: maxItems);
            }

            Scene targetScene = default;
            bool found = false;
            int count = 0;
            try
            {
                count = SceneManager.sceneCount;
            }
            catch { }

            string cleanQuery = sceneName.Trim().Replace('\\', '/');
            string cleanNameWithoutExt = Path.GetFileNameWithoutExtension(cleanQuery);

            for (int i = 0; i < count; i++)
            {
                Scene s;
                try
                {
                    s = SceneManager.GetSceneAt(i);
                }
                catch
                {
                    continue;
                }

                if (string.Equals(s.name, cleanQuery, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(s.name, cleanNameWithoutExt, StringComparison.OrdinalIgnoreCase) ||
                    (!string.IsNullOrEmpty(s.path) && string.Equals(s.path.Replace('\\', '/'), cleanQuery, StringComparison.OrdinalIgnoreCase)))
                {
                    targetScene = s;
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                return $"(Scene \"{sceneName}\" is not currently loaded)";
            }

            return InspectScene(targetScene, maxDepth, maxItems);
        }

        /// <summary>
        /// Inspects the hierarchy starting from a specific root GameObject.
        /// </summary>
        public static string Hierarchy(GameObject root, int maxDepth = DefaultMaxDepth, int maxItems = DefaultMaxItems)
        {
            if (!ReferenceEquals(root, null) && root == null)
            {
                return "(GameObject is destroyed)";
            }

            maxDepth = Math.Max(0, Math.Min(maxDepth, 1000));
            maxItems = Math.Max(1, maxItems);

            var sb = new StringBuilder();
            int itemCount = 0;
            bool itemLimitReached = false;
            var visited = new HashSet<Transform>(ReferenceIdentityComparer<Transform>.Instance);

            if (root != null)
            {
                if (root.transform != null)
                {
                    TraverseHierarchy(root.transform, 0, maxDepth, maxItems, ref itemCount, ref itemLimitReached, visited, sb);
                }
                else
                {
                    return "(GameObject has no Transform)";
                }
                return LimitOutput(sb.ToString().TrimEnd());
            }

            int sceneCount = 0;
            try
            {
                sceneCount = SceneManager.sceneCount;
            }
            catch { }

            if (sceneCount == 0)
            {
                return "(No scenes currently loaded)";
            }

            for (int s = 0; s < sceneCount; s++)
            {
                Scene scene;
                try
                {
                    scene = SceneManager.GetSceneAt(s);
                }
                catch
                {
                    continue;
                }

                if (!scene.isLoaded)
                {
                    sb.AppendLine($"Scene \"{scene.name}\" (not loaded)");
                    continue;
                }

                if (sceneCount > 1)
                {
                    sb.AppendLine($"=== Scene: \"{scene.name}\" (roots: {scene.rootCount}) ===");
                }

                GameObject[] rootObjects = null;
                try
                {
                    rootObjects = scene.GetRootGameObjects();
                }
                catch { }

                if (rootObjects == null || rootObjects.Length == 0)
                {
                    sb.AppendLine($"Scene \"{scene.name}\" (0 root GameObjects - empty scene)");
                    continue;
                }

                for (int i = 0; i < rootObjects.Length; i++)
                {
                    if (itemCount >= maxItems)
                    {
                        if (!itemLimitReached)
                        {
                            itemLimitReached = true;
                            sb.AppendLine(ItemTruncationMarker);
                        }
                        break;
                    }

                    var rootGo = rootObjects[i];
                    if (rootGo != null && rootGo.transform != null)
                    {
                        TraverseHierarchy(rootGo.transform, 0, maxDepth, maxItems, ref itemCount, ref itemLimitReached, visited, sb);
                    }
                }

                if (itemCount >= maxItems)
                {
                    break;
                }
            }

            if (sb.Length == 0)
            {
                return "(No GameObjects found in loaded scenes)";
            }

            return LimitOutput(sb.ToString().TrimEnd());
        }

        private static string InspectScene(Scene scene, int maxDepth, int maxItems)
        {
            maxDepth = Math.Max(0, Math.Min(maxDepth, 1000));
            maxItems = Math.Max(1, maxItems);

            if (!scene.isLoaded)
            {
                return $"Scene \"{scene.name}\" (not loaded)";
            }

            var sb = new StringBuilder();
            GameObject[] rootObjects = null;
            try
            {
                rootObjects = scene.GetRootGameObjects();
            }
            catch { }

            if (rootObjects == null || rootObjects.Length == 0)
            {
                return $"Scene \"{scene.name}\" (0 root GameObjects - empty scene)";
            }

            sb.AppendLine($"Scene \"{scene.name}\" (roots: {rootObjects.Length}):");
            int itemCount = 0;
            bool itemLimitReached = false;
            var visited = new HashSet<Transform>(ReferenceIdentityComparer<Transform>.Instance);

            for (int i = 0; i < rootObjects.Length; i++)
            {
                if (itemCount >= maxItems)
                {
                    if (!itemLimitReached)
                    {
                        itemLimitReached = true;
                        sb.AppendLine(ItemTruncationMarker);
                    }
                    break;
                }

                var rootGo = rootObjects[i];
                if (rootGo != null && rootGo.transform != null)
                {
                    TraverseHierarchy(rootGo.transform, 0, maxDepth, maxItems, ref itemCount, ref itemLimitReached, visited, sb);
                }
            }

            if (itemCount == 0)
            {
                return $"Scene \"{scene.name}\" (0 valid root GameObjects)";
            }

            return LimitOutput(sb.ToString().TrimEnd());
        }

        private static void TraverseHierarchy(
            Transform current,
            int depth,
            int maxDepth,
            int maxItems,
            ref int itemCount,
            ref bool itemLimitReached,
            HashSet<Transform> visited,
            StringBuilder sb)
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
                    if (!itemLimitReached)
                    {
                        itemLimitReached = true;
                        sb.AppendLine($"{indent}{ItemTruncationMarker}");
                    }
                    return;
                }

                itemCount++;

                GameObject go = null;
                try
                {
                    go = current.gameObject;
                }
                catch { }

                if (go == null)
                {
                    sb.AppendLine($"{indent}(Missing GameObject)");
                    return;
                }

                string goName = "Unnamed";
                try
                {
                    goName = go.name;
                }
                catch { }

                bool activeInHierarchy = false;
                bool activeSelf = false;
                try
                {
                    activeInHierarchy = go.activeInHierarchy;
                    activeSelf = go.activeSelf;
                }
                catch { }

                string activeStatus = activeInHierarchy ? "[active]" : (!activeSelf ? "[inactive]" : "[inactive (parent disabled)]");

                Component[] comps = null;
                try
                {
                    comps = go.GetComponents<Component>();
                }
                catch { }

                var compNames = new List<string>();
                if (comps != null)
                {
                    for (int c = 0; c < comps.Length; c++)
                    {
                        var comp = comps[c];
                        if (comp == null)
                        {
                            compNames.Add("Missing Script");
                        }
                        else
                        {
                            try
                            {
                                compNames.Add(comp.GetType().Name);
                            }
                            catch
                            {
                                compNames.Add("Unknown Component");
                            }
                        }
                    }
                }

                string compListStr = compNames.Count > 0 ? string.Join(", ", compNames) : "No Components";
                sb.AppendLine($"{indent}{goName} {activeStatus} [{compListStr}]");

                int childCount = 0;
                try
                {
                    childCount = current.childCount;
                }
                catch { }

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
                        if (!itemLimitReached)
                        {
                            itemLimitReached = true;
                            sb.AppendLine($"{indent}  {ItemTruncationMarker}");
                        }
                        break;
                    }

                    Transform child = null;
                    try
                    {
                        child = current.GetChild(i);
                    }
                    catch { }

                    if (child != null)
                    {
                        TraverseHierarchy(child, depth + 1, maxDepth, maxItems, ref itemCount, ref itemLimitReached, visited, sb);
                    }
                }
            }
            finally
            {
                visited.Remove(current);
            }
        }

        #endregion

        #region Object & Component Inspection

        /// <summary>
        /// Inspects a GameObject by listing its hierarchy path, active state, components,
        /// and serialized / public fields.
        /// </summary>
        public static string GameObject(GameObject gameObject, int maxItems = 50)
        {
            return Object(gameObject, maxDepth: 2, maxItems: maxItems);
        }

        /// <summary>
        /// Inspects a Component by listing its type, parent GameObject, enabled state,
        /// and serialized / public fields.
        /// </summary>
        public static string Component(Component component, int maxItems = 50)
        {
            return Object(component, maxDepth: 2, maxItems: maxItems);
        }

        /// <summary>
        /// Inspects any object (GameObject, Component, ScriptableObject, Material, or plain C# object).
        /// Non-destructive, handles missing components, null references, and uninstantiated prefabs gracefully.
        /// </summary>
        public static string Object(object target, int maxDepth = 2, int maxItems = 50)
        {
            if (target == null)
            {
                return "null";
            }

            maxDepth = Math.Max(0, Math.Min(maxDepth, 32));
            maxItems = Math.Max(1, maxItems);

            var sb = new StringBuilder();
            var visited = new HashSet<object>(ReferenceIdentityComparer.Instance);

            InspectObjectCore(target, 0, maxDepth, maxItems, visited, sb);

            return LimitOutput(sb.ToString().TrimEnd());
        }

        private static void InspectObjectCore(
            object target,
            int depth,
            int maxDepth,
            int maxItems,
            HashSet<object> visited,
            StringBuilder sb)
        {
            if (target == null)
            {
                sb.AppendLine("null");
                return;
            }

            if (target is UnityEngine.Object unityObj && unityObj == null)
            {
                sb.AppendLine($"null ({target.GetType().Name})");
                return;
            }

            if (depth > maxDepth)
            {
                sb.AppendLine(DepthTruncationMarker);
                return;
            }

            if (!target.GetType().IsValueType && !visited.Add(target))
            {
                sb.AppendLine(CycleTruncationMarker);
                return;
            }

            try
            {
                if (target is GameObject go)
                {
                    InspectGameObject(go, maxItems, visited, sb);
                }
                else if (target is Component comp)
                {
                    InspectComponent(comp, maxItems, visited, sb);
                }
                else if (target is UnityEngine.Object uo)
                {
                    InspectUnityObject(uo, maxItems, sb);
                }
                else if (target is Type targetType)
                {
                    sb.AppendLine($"Type: {targetType.FullName ?? targetType.Name}");
                }
                else if (target is Delegate targetDel)
                {
                    sb.AppendLine($"Delegate: {targetDel.Method.Name}()");
                }
                else if (target is MemberInfo targetMi)
                {
                    sb.AppendLine($"{targetMi.MemberType}: {targetMi.Name} ({targetMi.DeclaringType?.Name})");
                }
                else if (target is Assembly targetAss)
                {
                    sb.AppendLine($"Assembly: {targetAss.GetName().Name}");
                }
                else if (target is IDictionary dict)
                {
                    InspectDictionary(dict, depth, maxDepth, maxItems, visited, sb);
                }
                else if (target is IEnumerable enumerable && !(target is string))
                {
                    InspectEnumerable(enumerable, depth, maxDepth, maxItems, visited, sb);
                }
                else if (IsLeafValue(target))
                {
                    sb.AppendLine(FormatValueSnippet(target));
                }
                else
                {
                    InspectGenericObject(target, depth, maxDepth, maxItems, visited, sb);
                }
            }
            finally
            {
                if (!target.GetType().IsValueType)
                {
                    visited.Remove(target);
                }
            }
        }

        private static void InspectDictionary(
            IDictionary dict,
            int depth,
            int maxDepth,
            int maxItems,
            HashSet<object> visited,
            StringBuilder sb)
        {
            string indent = new string(' ', depth * 2);
            int count = dict.Count;
            sb.AppendLine($"{indent}{dict.GetType().Name} ({count} entries):");

            int idx = 0;
            foreach (DictionaryEntry entry in dict)
            {
                if (idx >= maxItems)
                {
                    sb.AppendLine($"{indent}  {ItemTruncationMarker}");
                    break;
                }
                idx++;
                sb.AppendLine($"{indent}  [{entry.Key}]: {FormatValueSnippet(entry.Value)}");
            }
        }

        private static void InspectEnumerable(
            IEnumerable enumerable,
            int depth,
            int maxDepth,
            int maxItems,
            HashSet<object> visited,
            StringBuilder sb)
        {
            string indent = new string(' ', depth * 2);
            int? count = null;
            if (enumerable is ICollection coll)
            {
                count = coll.Count;
            }

            string countStr = count.HasValue ? $" ({count.Value} items)" : "";
            sb.AppendLine($"{indent}{enumerable.GetType().Name}{countStr}:");

            int idx = 0;
            foreach (var item in enumerable)
            {
                if (idx >= maxItems)
                {
                    sb.AppendLine($"{indent}  {ItemTruncationMarker}");
                    break;
                }
                sb.AppendLine($"{indent}  [{idx}]: {FormatValueSnippet(item)}");
                idx++;
            }
        }

        private static void InspectGameObject(
            GameObject go,
            int maxItems,
            HashSet<object> visited,
            StringBuilder sb)
        {
            string path = GetHierarchyPath(go.transform);
            string assetPath = null;
            try
            {
                assetPath = AssetDatabase.GetAssetPath(go);
            }
            catch { }

            string goName = "Unnamed";
            bool activeSelf = false;
            bool activeInHierarchy = false;
            string tag = "Untagged";
            int layer = 0;
            string layerName = "";

            try { goName = go.name; } catch { }
            try { activeSelf = go.activeSelf; } catch { }
            try { activeInHierarchy = go.activeInHierarchy; } catch { }
            try { tag = go.tag; } catch { }
            try { layer = go.layer; } catch { }
            try { layerName = LayerMask.LayerToName(layer); } catch { }

            sb.AppendLine($"GameObject: \"{goName}\"");
            sb.AppendLine($"  Active: {(activeSelf ? "true" : "false")} (in hierarchy: {(activeInHierarchy ? "true" : "false")})");
            sb.AppendLine($"  Tag: \"{tag}\" | Layer: {layer} ({layerName})");
            sb.AppendLine($"  Path: {path}");

            if (!string.IsNullOrEmpty(assetPath))
            {
                sb.AppendLine($"  Asset Path: \"{assetPath}\" (Prefab Asset)");
            }

            if (go.transform != null)
            {
                try
                {
                    string pos = go.transform.localPosition.ToString("F2", CultureInfo.InvariantCulture);
                    string rot = go.transform.localEulerAngles.ToString("F2", CultureInfo.InvariantCulture);
                    string scale = go.transform.localScale.ToString("F2", CultureInfo.InvariantCulture);
                    sb.AppendLine($"  Transform: localPos={pos}, localRot={rot}, localScale={scale}");

                    int childCount = go.transform.childCount;
                    if (childCount > 0)
                    {
                        var childNames = new List<string>();
                        for (int i = 0; i < Math.Min(childCount, 8); i++)
                        {
                            var ch = go.transform.GetChild(i);
                            if (ch != null) childNames.Add(ch.name);
                        }
                        string extra = childCount > 8 ? $", ... and {childCount - 8} more" : "";
                        sb.AppendLine($"  Children ({childCount}): [{string.Join(", ", childNames)}{extra}]");
                    }
                }
                catch { }
            }

            Component[] comps = null;
            try
            {
                comps = go.GetComponents<Component>();
            }
            catch { }

            if (comps == null || comps.Length == 0)
            {
                sb.AppendLine("  Components: None");
                return;
            }

            sb.AppendLine($"  Components ({comps.Length}):");
            for (int i = 0; i < comps.Length; i++)
            {
                if (i >= maxItems)
                {
                    sb.AppendLine($"    {ItemTruncationMarker}");
                    break;
                }

                var c = comps[i];
                if (c == null)
                {
                    sb.AppendLine($"    [{i}] Missing Component / Unassigned Script");
                    continue;
                }

                sb.AppendLine($"    [{i}] {c.GetType().Name} (Type: {c.GetType().FullName})");
                InspectComponentInline(c, maxItems, sb, "      ");
            }
        }

        private static void InspectComponent(
            Component comp,
            int maxItems,
            HashSet<object> visited,
            StringBuilder sb)
        {
            string goName = "null";
            try
            {
                goName = comp.gameObject != null ? comp.gameObject.name : "null";
            }
            catch { }

            sb.AppendLine($"Component: {comp.GetType().Name} (Type: {comp.GetType().FullName})");
            sb.AppendLine($"  GameObject: \"{goName}\"");

            try
            {
                if (comp is Transform tr)
                {
                    string pos = tr.localPosition.ToString("F2", CultureInfo.InvariantCulture);
                    string rot = tr.localEulerAngles.ToString("F2", CultureInfo.InvariantCulture);
                    string scale = tr.localScale.ToString("F2", CultureInfo.InvariantCulture);
                    sb.AppendLine($"  Transform: localPos={pos}, localRot={rot}, localScale={scale}, children={tr.childCount}");
                }
                else if (comp is Behaviour beh)
                {
                    sb.AppendLine($"  Enabled: {(beh.enabled ? "true" : "false")}");
                }
                else if (comp is Collider col)
                {
                    sb.AppendLine($"  Enabled: {(col.enabled ? "true" : "false")}");
                }
                else if (comp is Renderer ren)
                {
                    sb.AppendLine($"  Enabled: {(ren.enabled ? "true" : "false")}");
                }
            }
            catch { }

            InspectComponentInline(comp, maxItems, sb, "  ");
        }

        private static void InspectComponentInline(Component comp, int maxItems, StringBuilder sb, string prefix)
        {
            var seenProperties = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int count = 0;

            // 1. Inspect serialized properties using SerializedObject
            try
            {
                var so = new SerializedObject(comp);
                var sp = so.GetIterator();
                bool enterChildren = true;

                while (sp.NextVisible(enterChildren))
                {
                    enterChildren = false;

                    if (sp.name == "m_Script")
                    {
                        continue;
                    }

                    if (count >= maxItems)
                    {
                        sb.AppendLine($"{prefix}{ItemTruncationMarker}");
                        return;
                    }

                    seenProperties.Add(sp.name);
                    string rawName = sp.name;
                    if (rawName.StartsWith("m_", StringComparison.OrdinalIgnoreCase) && rawName.Length > 2)
                    {
                        seenProperties.Add(rawName.Substring(2));
                    }
                    else if (rawName.StartsWith("_", StringComparison.OrdinalIgnoreCase) && rawName.Length > 1)
                    {
                        seenProperties.Add(rawName.Substring(1));
                    }
                    if (!string.IsNullOrEmpty(sp.displayName))
                    {
                        seenProperties.Add(sp.displayName);
                    }

                    count++;

                    string valStr = FormatSerializedPropertyWithAsset(sp);
                    sb.AppendLine($"{prefix}{sp.displayName} ({sp.name}): {valStr}");
                }
            }
            catch
            {
                // SerializedObject might fail on uninstantiated or unusual components; fallback to reflection
            }

            // 2. Inspect remaining public instance fields via reflection
            try
            {
                var fields = comp.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public);
                for (int i = 0; i < fields.Length; i++)
                {
                    var field = fields[i];
                    if (field.IsStatic) continue;
                    if (seenProperties.Contains(field.Name)) continue;
                    if (field.IsDefined(typeof(ObsoleteAttribute), inherit: true)) continue;

                    if (count >= maxItems)
                    {
                        sb.AppendLine($"{prefix}{ItemTruncationMarker}");
                        return;
                    }

                    count++;
                    seenProperties.Add(field.Name);

                    string fieldValStr;
                    try
                    {
                        object val = field.GetValue(comp);
                        fieldValStr = FormatValueSnippet(val);
                    }
                    catch (Exception ex)
                    {
                        fieldValStr = $"<error: {ex.InnerException?.Message ?? ex.Message}>";
                    }

                    sb.AppendLine($"{prefix}{field.Name}: {fieldValStr}");
                }
            }
            catch { }

            // 3. Inspect remaining public instance properties via reflection
            try
            {
                var props = comp.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public);
                for (int i = 0; i < props.Length; i++)
                {
                    var prop = props[i];
                    if (!prop.CanRead || prop.GetIndexParameters().Length > 0) continue;
                    if (seenProperties.Contains(prop.Name)) continue;
                    if (prop.IsDefined(typeof(ObsoleteAttribute), inherit: true)) continue;
                    if (IsIgnoredComponentProperty(prop.Name)) continue;

                    if (count >= maxItems)
                    {
                        sb.AppendLine($"{prefix}{ItemTruncationMarker}");
                        return;
                    }

                    count++;
                    seenProperties.Add(prop.Name);

                    string propValStr;
                    try
                    {
                        object val = prop.GetValue(comp);
                        propValStr = FormatValueSnippet(val);
                    }
                    catch (Exception ex)
                    {
                        propValStr = $"<error: {ex.InnerException?.Message ?? ex.Message}>";
                    }

                    sb.AppendLine($"{prefix}{prop.Name}: {propValStr}");
                }
            }
            catch { }
        }

        private static void InspectUnityObject(UnityEngine.Object uo, int maxItems, StringBuilder sb)
        {
            string assetPath = null;
            try
            {
                assetPath = AssetDatabase.GetAssetPath(uo);
            }
            catch { }

            string uoName = "Unnamed";
            try
            {
                uoName = uo.name;
            }
            catch { }

            sb.AppendLine($"{uo.GetType().Name}: \"{uoName}\"");
            if (!string.IsNullOrEmpty(assetPath))
            {
                sb.AppendLine($"  Asset Path: \"{assetPath}\"");
            }

            int count = 0;
            try
            {
                var so = new SerializedObject(uo);
                var sp = so.GetIterator();
                bool enterChildren = true;

                while (sp.NextVisible(enterChildren))
                {
                    enterChildren = false;
                    if (sp.name == "m_Script") continue;

                    if (count >= maxItems)
                    {
                        sb.AppendLine($"  {ItemTruncationMarker}");
                        return;
                    }

                    count++;
                    string valStr = FormatSerializedPropertyWithAsset(sp);
                    sb.AppendLine($"  {sp.displayName} ({sp.name}): {valStr}");
                }
            }
            catch
            {
                // Fallback to basic string
                sb.AppendLine($"  Value: {uo}");
            }
        }

        private static void InspectGenericObject(
            object target,
            int depth,
            int maxDepth,
            int maxItems,
            HashSet<object> visited,
            StringBuilder sb)
        {
            var type = target.GetType();
            if (IsLeafValue(target))
            {
                sb.AppendLine(FormatValueSnippet(target));
                return;
            }

            string indent = new string(' ', depth * 2);
            sb.AppendLine($"{indent}{type.Name} ({type.FullName}):");
            int count = 0;

            var fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public);
            for (int i = 0; i < fields.Length; i++)
            {
                if (count >= maxItems)
                {
                    sb.AppendLine($"{indent}  {ItemTruncationMarker}");
                    return;
                }

                var f = fields[i];
                if (f.IsStatic) continue;
                if (f.IsDefined(typeof(ObsoleteAttribute), inherit: true)) continue;

                count++;
                object val = null;
                try
                {
                    val = f.GetValue(target);
                }
                catch (Exception ex)
                {
                    sb.AppendLine($"{indent}  {f.Name}: <error: {ex.InnerException?.Message ?? ex.Message}>");
                    continue;
                }

                if (!IsLeafValue(val))
                {
                    if (visited.Contains(val))
                    {
                        sb.AppendLine($"{indent}  {f.Name}: {CycleTruncationMarker}");
                    }
                    else if (depth >= maxDepth)
                    {
                        sb.AppendLine($"{indent}  {f.Name}: {DepthTruncationMarker}");
                    }
                    else
                    {
                        sb.AppendLine($"{indent}  {f.Name}:");
                        InspectObjectCore(val, depth + 1, maxDepth, maxItems, visited, sb);
                    }
                }
                else
                {
                    sb.AppendLine($"{indent}  {f.Name}: {FormatValueSnippet(val)}");
                }
            }

            var props = type.GetProperties(BindingFlags.Instance | BindingFlags.Public);
            for (int i = 0; i < props.Length; i++)
            {
                if (count >= maxItems)
                {
                    sb.AppendLine($"{indent}  {ItemTruncationMarker}");
                    return;
                }

                var p = props[i];
                if (!p.CanRead || p.GetIndexParameters().Length > 0) continue;
                if (p.IsDefined(typeof(ObsoleteAttribute), inherit: true)) continue;

                count++;
                object val = null;
                try
                {
                    val = p.GetValue(target);
                }
                catch (Exception ex)
                {
                    sb.AppendLine($"{indent}  {p.Name}: <error: {ex.InnerException?.Message ?? ex.Message}>");
                    continue;
                }

                if (!IsLeafValue(val))
                {
                    if (visited.Contains(val))
                    {
                        sb.AppendLine($"{indent}  {p.Name}: {CycleTruncationMarker}");
                    }
                    else if (depth >= maxDepth)
                    {
                        sb.AppendLine($"{indent}  {p.Name}: {DepthTruncationMarker}");
                    }
                    else
                    {
                        sb.AppendLine($"{indent}  {p.Name}:");
                        InspectObjectCore(val, depth + 1, maxDepth, maxItems, visited, sb);
                    }
                }
                else
                {
                    sb.AppendLine($"{indent}  {p.Name}: {FormatValueSnippet(val)}");
                }
            }
        }

        private static string FormatSerializedPropertyWithAsset(SerializedProperty prop)
        {
            try
            {
                if (prop.propertyType == SerializedPropertyType.ObjectReference)
                {
                    var obj = prop.objectReferenceValue;
                    if (obj == null)
                    {
                        return "null";
                    }

                    string assetPath = null;
                    try
                    {
                        assetPath = AssetDatabase.GetAssetPath(obj);
                    }
                    catch { }

                    string objName = "Unnamed";
                    try { objName = obj.name; } catch { }

                    if (!string.IsNullOrEmpty(assetPath))
                    {
                        return $"\"{objName}\" ({obj.GetType().Name}) [Asset: \"{assetPath}\"]";
                    }

                    return $"\"{objName}\" ({obj.GetType().Name})";
                }

                if (prop.isArray)
                {
                    return $"[{prop.arraySize} items]";
                }

                string formattedVal = UnityResultFormatter.FormatSerializedPropertyValue(prop);
                return formattedVal ?? prop.propertyType.ToString();
            }
            catch (Exception ex)
            {
                return $"<error: {ex.Message}>";
            }
        }

        private static bool IsLeafValue(object val)
        {
            if (val == null) return true;
            var t = val.GetType();
            if (t.IsPrimitive || val is string || val is decimal || t.IsEnum) return true;
            if (val is DateTime || val is TimeSpan || val is Guid || val is DateTimeOffset) return true;
            if (val is Type || val is Delegate || val is MemberInfo || val is Assembly) return true;
            if (val is UnityEngine.Object) return true;
            if (t.IsValueType && (t.Namespace == "UnityEngine" || t.Namespace?.StartsWith("UnityEngine.") == true)) return true;
            return false;
        }

        private static string FormatValueSnippet(object val)
        {
            if (val == null) return "null";
            if (val is UnityEngine.Object uo)
            {
                if (uo == null) return $"null ({val.GetType().Name})";
                string assetPath = null;
                try
                {
                    assetPath = AssetDatabase.GetAssetPath(uo);
                }
                catch { }

                string uoName = "Unnamed";
                try { uoName = uo.name; } catch { }

                if (!string.IsNullOrEmpty(assetPath))
                {
                    return $"\"{uoName}\" ({uo.GetType().Name}) [Asset: \"{assetPath}\"]";
                }
                return $"\"{uoName}\" ({uo.GetType().Name})";
            }

            if (val is string str) return $"\"{str}\"";
            if (val is bool b) return b ? "true" : "false";
            if (val is Type t) return $"typeof({t.FullName ?? t.Name})";
            if (val is Delegate del) return $"Delegate: {del.Method.Name}()";
            if (val is MemberInfo mi) return $"{mi.MemberType}: {mi.Name}";
            if (val is Assembly ass) return $"Assembly: {ass.GetName().Name}";

            if (val.GetType().IsPrimitive || val is decimal || val.GetType().IsEnum)
            {
                return Convert.ToString(val, CultureInfo.InvariantCulture);
            }

            if (val is IFormattable formattable)
            {
                try
                {
                    return formattable.ToString(null, CultureInfo.InvariantCulture);
                }
                catch { }
            }

            return val.ToString();
        }

        private static bool IsIgnoredComponentProperty(string propName)
        {
            if (string.IsNullOrEmpty(propName)) return false;

            switch (propName.ToLowerInvariant())
            {
                case "rigidbody":
                case "rigidbody2d":
                case "camera":
                case "light":
                case "animation":
                case "constantforce":
                case "renderer":
                case "audio":
                case "guitext":
                case "networkview":
                case "guielement":
                case "guitexture":
                case "collider":
                case "collider2d":
                case "hingejoint":
                case "particleemitter":
                case "particlesystem":
                case "transform":
                case "gameobject":
                case "tag":
                case "name":
                case "hideflags":
                // EditMode mutating properties that instantiate copies / leak assets:
                case "material":
                case "materials":
                case "mesh":
                case "physicmaterial":
                case "trailmaterial":
                case "trailmaterials":
                    return true;
                default:
                    return false;
            }
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

        #endregion

        #region Asset & Prefab Search

        /// <summary>
        /// Searches project assets by filter, type, or label, returning normalized project-relative paths.
        /// </summary>
        public static string FindAssets(string filter = null, string type = null, string label = null, int maxItems = 50)
        {
            maxItems = Math.Max(1, maxItems);

            var queryParts = new List<string>();
            if (!string.IsNullOrWhiteSpace(filter))
            {
                queryParts.Add(filter.Trim());
            }
            if (!string.IsNullOrWhiteSpace(type))
            {
                string cleanType = type.Trim();
                if (!cleanType.StartsWith("t:", StringComparison.OrdinalIgnoreCase))
                {
                    cleanType = "t:" + cleanType;
                }
                queryParts.Add(cleanType);
            }
            if (!string.IsNullOrWhiteSpace(label))
            {
                string cleanLabel = label.Trim();
                if (!cleanLabel.StartsWith("l:", StringComparison.OrdinalIgnoreCase))
                {
                    cleanLabel = "l:" + cleanLabel;
                }
                queryParts.Add(cleanLabel);
            }

            string searchQuery = string.Join(" ", queryParts);
            string[] guids = null;
            try
            {
                guids = AssetDatabase.FindAssets(searchQuery);
            }
            catch (Exception ex)
            {
                return $"(Asset search failed: {ex.Message})";
            }

            if (guids == null || guids.Length == 0)
            {
                return string.IsNullOrEmpty(searchQuery)
                    ? "(No assets found in project)"
                    : $"(No assets found matching query: '{searchQuery}')";
            }

            var sb = new StringBuilder();
            sb.AppendLine($"Assets found ({guids.Length}):");

            int count = 0;
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < guids.Length; i++)
            {
                string rawPath = null;
                try
                {
                    rawPath = AssetDatabase.GUIDToAssetPath(guids[i]);
                }
                catch { }
                if (string.IsNullOrEmpty(rawPath)) continue;

                // Normalize path to project-relative with forward slashes
                string normalizedPath = rawPath.Replace('\\', '/');
                if (!seenPaths.Add(normalizedPath)) continue;

                if (count >= maxItems)
                {
                    sb.AppendLine(ItemTruncationMarker);
                    break;
                }

                count++;
                Type assetType = null;
                try
                {
                    assetType = AssetDatabase.GetMainAssetTypeAtPath(normalizedPath);
                }
                catch { }

                string typeName = assetType != null ? assetType.Name : "Asset";
                sb.AppendLine($"  {count}. {normalizedPath} [{typeName}]");
            }

            if (count == 0)
            {
                return string.IsNullOrEmpty(searchQuery)
                    ? "(No assets found in project)"
                    : $"(No assets found matching query: '{searchQuery}')";
            }

            return LimitOutput(sb.ToString().TrimEnd());
        }

        /// <summary>
        /// Searches project prefabs by optional name/filter, returning normalized project-relative paths.
        /// </summary>
        public static string FindPrefabs(string filter = null, int maxItems = 50)
        {
            return FindAssets(filter, type: "Prefab", label: null, maxItems: maxItems);
        }

        /// <summary>
        /// Finds a single asset by name or path and returns detailed metadata.
        /// </summary>
        public static string FindAsset(string nameOrPath)
        {
            if (string.IsNullOrWhiteSpace(nameOrPath))
            {
                return "(Missing asset name or path)";
            }

            try
            {
                string clean = nameOrPath.Trim().Replace('\\', '/');
                string targetPath = clean;

                if (!clean.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) &&
                    !clean.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
                {
                    string[] guids = AssetDatabase.FindAssets(clean);
                    if (guids == null || guids.Length == 0)
                    {
                        return $"(Asset not found matching: \"{nameOrPath}\")";
                    }

                    string foundPath = null;
                    for (int g = 0; g < guids.Length; g++)
                    {
                        string p = null;
                        try
                        {
                            p = AssetDatabase.GUIDToAssetPath(guids[g]);
                        }
                        catch { }
                        if (!string.IsNullOrEmpty(p))
                        {
                            foundPath = p.Replace('\\', '/');
                            break;
                        }
                    }

                    if (string.IsNullOrEmpty(foundPath))
                    {
                        return $"(Asset not found matching: \"{nameOrPath}\")";
                    }
                    targetPath = foundPath;
                }

                var assetObj = AssetDatabase.LoadMainAssetAtPath(targetPath);
                if (assetObj == null)
                {
                    return $"(Could not load asset at path: \"{targetPath}\")";
                }

                var sb = new StringBuilder();
                string assetName = "Unnamed";
                try { assetName = assetObj.name; } catch { }
                sb.AppendLine($"Asset: \"{assetName}\"");
                sb.AppendLine($"  Path: \"{targetPath}\"");
                sb.AppendLine($"  Type: {assetObj.GetType().FullName}");
                sb.AppendLine($"  GUID: {AssetDatabase.AssetPathToGUID(targetPath)}");

                return LimitOutput(sb.ToString().TrimEnd());
            }
            catch (Exception ex)
            {
                return $"(Asset search failed: {ex.Message})";
            }
        }

        #endregion

        #region Selection & Main Camera Viewport

        /// <summary>
        /// Returns a high-density summary of the active Editor selection, or a clear notification if empty.
        /// </summary>
        public static string Selection(int maxItems = 50)
        {
            maxItems = Math.Max(1, maxItems);
            var activeGo = UnityEditor.Selection.activeGameObject;
            var activeObj = UnityEditor.Selection.activeObject;
            var allObjects = UnityEditor.Selection.objects;

            var validObjects = new List<UnityEngine.Object>();
            if (allObjects != null)
            {
                for (int i = 0; i < allObjects.Length; i++)
                {
                    if (allObjects[i] != null)
                    {
                        validObjects.Add(allObjects[i]);
                    }
                }
            }

            if (activeGo == null && activeObj == null && validObjects.Count == 0)
            {
                return "(No active selection in Editor)";
            }

            var sb = new StringBuilder();

            if (activeGo != null)
            {
                string goName = "Unnamed";
                bool activeSelf = false;
                bool activeInHierarchy = false;
                string tag = "Untagged";
                int layer = 0;
                string layerName = "";

                try { goName = activeGo.name; } catch { }
                try { activeSelf = activeGo.activeSelf; } catch { }
                try { activeInHierarchy = activeGo.activeInHierarchy; } catch { }
                try { tag = activeGo.tag; } catch { }
                try { layer = activeGo.layer; } catch { }
                try { layerName = LayerMask.LayerToName(layer); } catch { }

                sb.AppendLine($"Active Selection (GameObject): \"{goName}\"");
                string path = GetHierarchyPath(activeGo.transform);
                sb.AppendLine($"  Path: {(string.IsNullOrEmpty(path) ? "/" : path)}");
                sb.AppendLine($"  Active: {(activeSelf ? "true" : "false")} (in hierarchy: {(activeInHierarchy ? "true" : "false")})");
                sb.AppendLine($"  Tag: \"{tag}\" | Layer: {layer} ({layerName})");

                if (activeGo.transform != null)
                {
                    try
                    {
                        var t = activeGo.transform;
                        string pos = t.position.ToString("F2", CultureInfo.InvariantCulture);
                        string rot = t.eulerAngles.ToString("F2", CultureInfo.InvariantCulture);
                        string scale = t.localScale.ToString("F2", CultureInfo.InvariantCulture);
                        sb.AppendLine($"  World Pos: {pos} | World Rot: {rot} | Scale: {scale}");
                    }
                    catch { }
                }

                Component[] comps = null;
                try
                {
                    comps = activeGo.GetComponents<Component>();
                }
                catch { }

                var compNames = new List<string>();
                if (comps != null)
                {
                    for (int i = 0; i < comps.Length; i++)
                    {
                        var c = comps[i];
                        string cName = "Missing Script";
                        if (c != null)
                        {
                            try { cName = c.GetType().Name; } catch { cName = "Unknown Component"; }
                        }
                        compNames.Add(cName);
                    }
                }
                sb.AppendLine($"  Components ({compNames.Count}): [{string.Join(", ", compNames)}]");
            }
            else if (activeObj != null)
            {
                string assetPath = null;
                try
                {
                    assetPath = AssetDatabase.GetAssetPath(activeObj);
                }
                catch { }

                string objName = "Unnamed";
                try { objName = activeObj.name; } catch { }

                sb.AppendLine($"Active Selection (Asset): \"{objName}\" ({activeObj.GetType().Name})");
                if (!string.IsNullOrEmpty(assetPath))
                {
                    sb.AppendLine($"  Asset Path: \"{assetPath}\"");
                }
            }

            int totalCount = validObjects.Count > 0 ? validObjects.Count : (activeObj != null ? 1 : (activeGo != null ? 1 : 0));
            if (totalCount > 1)
            {
                sb.AppendLine($"Total Selected Objects ({totalCount}):");
                int limit = Math.Min(validObjects.Count, 10);
                for (int i = 0; i < limit; i++)
                {
                    var o = validObjects[i];
                    string oName = "Unnamed";
                    string oType = "Object";
                    try { oName = o.name; } catch { }
                    try { oType = o.GetType().Name; } catch { }
                    sb.AppendLine($"  - \"{oName}\" ({oType})");
                }
                if (validObjects.Count > 10)
                {
                    sb.AppendLine($"  ... and {validObjects.Count - 10} more");
                }
            }

            if (sb.Length == 0)
            {
                return "(No active selection in Editor)";
            }

            return LimitOutput(sb.ToString().TrimEnd());
        }

        /// <summary>
        /// Returns high-density viewport, projection, and configuration settings for the main scene camera.
        /// </summary>
        public static string MainCamera()
        {
            Camera cam = null;
            try
            {
                cam = Camera.main;
            }
            catch { }

            if (cam == null)
            {
                try
                {
                    if (Camera.allCamerasCount > 0)
                    {
                        cam = Camera.allCameras[0];
                    }
                }
                catch { }
            }

            if (cam == null)
            {
                try
                {
                    cam = UnityEngine.Object.FindObjectOfType<Camera>();
                }
                catch { }
            }

            if (cam == null)
            {
                return "(No active Camera found in scene)";
            }

            var sb = new StringBuilder();
            string camName = "Camera";
            string goName = "null";
            bool active = false;
            try
            {
                camName = cam.name;
                goName = cam.gameObject != null ? cam.gameObject.name : cam.name;
                active = cam.gameObject != null && cam.gameObject.activeInHierarchy;
            }
            catch { }

            sb.AppendLine($"Main Camera: \"{camName}\" (GameObject: \"{goName}\", active: {(active ? "true" : "false")})");

            if (cam.transform != null)
            {
                try
                {
                    string pos = cam.transform.position.ToString("F2", CultureInfo.InvariantCulture);
                    string rot = cam.transform.eulerAngles.ToString("F2", CultureInfo.InvariantCulture);
                    sb.AppendLine($"  Position: {pos} | Rotation: {rot}");
                }
                catch { }
            }

            try
            {
                if (cam.orthographic)
                {
                    string orthoSize = cam.orthographicSize.ToString("F2", CultureInfo.InvariantCulture);
                    sb.AppendLine($"  Projection: Orthographic (Size: {orthoSize})");
                }
                else
                {
                    string fov = cam.fieldOfView.ToString("F1", CultureInfo.InvariantCulture);
                    sb.AppendLine($"  Projection: Perspective (FOV: {fov}°)");
                }

                string near = cam.nearClipPlane.ToString("F2", CultureInfo.InvariantCulture);
                string far = cam.farClipPlane.ToString("F2", CultureInfo.InvariantCulture);
                sb.AppendLine($"  Clipping Planes: Near={near}, Far={far}");

                var r = cam.rect;
                string rectStr = $"[x:{r.x.ToString("F2", CultureInfo.InvariantCulture)}, y:{r.y.ToString("F2", CultureInfo.InvariantCulture)}, w:{r.width.ToString("F2", CultureInfo.InvariantCulture)}, h:{r.height.ToString("F2", CultureInfo.InvariantCulture)}]";
                sb.AppendLine($"  Viewport Rect: {rectStr}");
                sb.AppendLine($"  Pixel Dimensions: {cam.pixelWidth} x {cam.pixelHeight}");
                sb.AppendLine($"  Clear Flags: {cam.clearFlags} | Background: {cam.backgroundColor}");
                sb.AppendLine($"  Culling Mask: {cam.cullingMask} | Depth: {cam.depth} | Rendering Path: {cam.renderingPath}");
            }
            catch (Exception ex)
            {
                sb.AppendLine($"  <Error reading camera settings: {ex.Message}>");
            }

            return LimitOutput(sb.ToString().TrimEnd());
        }

        #endregion

        #region Help

        /// <summary>
        /// Returns a compact cheatsheet of all available Inspect helper methods and signatures.
        /// </summary>
        public static string Help()
        {
            var sb = new StringBuilder();
            sb.AppendLine("UnityLeanMcp Inspection Helpers (UnityLeanMcp.Inspect):");
            sb.AppendLine("• Hierarchy([root], [maxDepth], [maxItems]) - Indented tree of GameObjects, active states, and components.");
            sb.AppendLine("• GameObject(go, [maxItems])                - Inspect GameObject, transform, components, and fields.");
            sb.AppendLine("• Component(comp, [maxItems])              - Inspect Component type, enabled state, and properties.");
            sb.AppendLine("• Object(target, [maxDepth], [maxItems])   - General inspection of ScriptableObjects, materials, or C# objects.");
            sb.AppendLine("• FindAssets([filter], [type], [label])    - Search project assets by name, type (e.g. 'Scene'), or label.");
            sb.AppendLine("• FindPrefabs([filter], [maxItems])        - Shortcut to find prefabs matching a filter.");
            sb.AppendLine("• FindAsset(nameOrPath)                    - Inspect a single asset's path, type, and GUID.");
            sb.AppendLine("• Selection([maxItems])                    - Current Unity Editor selection and selected objects.");
            sb.AppendLine("• MainCamera()                             - Active Camera projection, FOV, viewport rect, and culling mask.");
            return sb.ToString().TrimEnd();
        }

        #endregion

        #region Output Budget & Guardrail Helpers

        internal static string LimitOutput(
            string value,
            int maxCharacters = DefaultMaxOutputCharacters,
            int maxBytes = DefaultMaxOutputBytes)
        {
            if (string.IsNullOrEmpty(value))
            {
                return value ?? string.Empty;
            }

            if (value.Length <= maxCharacters && Encoding.UTF8.GetByteCount(value) <= maxBytes)
            {
                return value;
            }

            string marker = GetOutputTruncationMarker(maxCharacters, maxBytes);
            if (marker.Length == 0)
            {
                return string.Empty;
            }

            int markerBytes = Encoding.UTF8.GetByteCount(marker);
            int prefixLength = Math.Min(value.Length, maxCharacters - marker.Length);

            while (prefixLength > 0 &&
                   (Encoding.UTF8.GetByteCount(value, 0, prefixLength) + markerBytes > maxBytes ||
                    (prefixLength < value.Length && char.IsHighSurrogate(value[prefixLength - 1]))))
            {
                prefixLength--;
            }

            return value.Substring(0, prefixLength) + marker;
        }

        private static string GetOutputTruncationMarker(int maxCharacters, int maxBytes)
        {
            if (FitsOutputBudget(OutputTruncationMarker, maxCharacters, maxBytes))
                return OutputTruncationMarker;

            const string shortMarker = "... (truncated)";
            if (FitsOutputBudget(shortMarker, maxCharacters, maxBytes))
                return shortMarker;

            const string compactMarker = "[truncated]";
            if (FitsOutputBudget(compactMarker, maxCharacters, maxBytes))
                return compactMarker;

            const string ellipsisMarker = "...";
            if (FitsOutputBudget(ellipsisMarker, maxCharacters, maxBytes))
                return ellipsisMarker;

            const string singleCharacterMarker = ".";
            return FitsOutputBudget(singleCharacterMarker, maxCharacters, maxBytes) ? singleCharacterMarker : string.Empty;
        }

        private static bool FitsOutputBudget(string value, int maxCharacters, int maxBytes)
        {
            return value.Length <= maxCharacters &&
                Encoding.UTF8.GetByteCount(value) <= maxBytes;
        }

        #endregion
    }
}
