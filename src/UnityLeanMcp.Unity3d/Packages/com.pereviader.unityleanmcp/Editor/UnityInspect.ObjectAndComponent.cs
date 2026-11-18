#nullable disable
#pragma warning disable CS0618

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace UnityLeanMcp
{
    public static partial class Inspect
    {
        private static readonly HashSet<string> s_IgnoredComponentProperties = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // EditMode mutating properties that instantiate copies / leak assets:
            "material", "materials", "fontmaterial", "fontmaterials",
            "mesh", "physicmaterial", "trailmaterial", "trailmaterials",
            "profile", "materialtemplate", "coefficients",
            // Component-specific legacy shortcuts and metadata:
            "rigidbody", "rigidbody2d", "camera", "light", "animation",
            "constantforce", "renderer", "audio", "guitext", "networkview",
            "guielement", "guitexture", "collider", "collider2d",
            "hingejoint", "particleemitter", "particlesystem",
            "transform", "gameobject", "tag", "name", "hideflags"
        };

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
                    InspectGameObject(go, maxItems, sb);
                }
                else if (target is VisualElement ve)
                {
                    int itemCount = 0;
                    bool itemLimitReached = false;
                    var visitedElements = new HashSet<VisualElement>(ReferenceIdentityComparer<VisualElement>.Instance);
                    TraverseVisualElement(ve, 0, maxDepth, maxItems, ref itemCount, ref itemLimitReached, visitedElements, sb);
                }
                else if (target is Component comp)
                {
                    InspectComponent(comp, maxItems, sb);
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
                    InspectDictionary(dict, depth, maxItems, sb);
                }
                else if (target is IEnumerable enumerable && !(target is string))
                {
                    InspectEnumerable(enumerable, depth, maxItems, sb);
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

        private static void InspectDictionary(IDictionary dict, int depth, int maxItems, StringBuilder sb)
        {
            string indent = new string(' ', depth * 2);
            sb.AppendLine($"{indent}{dict.GetType().Name} ({dict.Count} entries):");

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

        private static void InspectEnumerable(IEnumerable enumerable, int depth, int maxItems, StringBuilder sb)
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

        private static void InspectGameObject(GameObject go, int maxItems, StringBuilder sb)
        {
            string path = GetHierarchyPath(go.transform);
            string assetPath = null;
            try { assetPath = AssetDatabase.GetAssetPath(go); } catch { }

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
                    if (go.transform is RectTransform rt)
                    {
                        sb.AppendLine($"  {FormatRectTransformSummary(rt)}");
                        if (rt.localEulerAngles.x != 0 || rt.localEulerAngles.y != 0 || rt.localEulerAngles.z != 0 ||
                            rt.localScale.x != 1 || rt.localScale.y != 1 || rt.localScale.z != 1)
                        {
                            string rot = rt.localEulerAngles.ToString("F2", CultureInfo.InvariantCulture);
                            string scale = rt.localScale.ToString("F2", CultureInfo.InvariantCulture);
                            sb.AppendLine($"    Transform: localRot={rot}, localScale={scale}");
                        }
                    }
                    else
                    {
                        sb.AppendLine($"  {FormatLocalTransformLine(go.transform)}");
                    }

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

            try
            {
                var canvasComp = go.GetComponent<Canvas>();
                if (canvasComp != null)
                {
                    sb.AppendLine($"  Canvas: renderMode={canvasComp.renderMode}, sortingOrder={canvasComp.sortingOrder}, sortingLayer=\"{canvasComp.sortingLayerName}\"");
                }
            }
            catch { }

            try
            {
                var uiDocComp = go.GetComponent<UIDocument>();
                if (uiDocComp != null && uiDocComp.rootVisualElement != null)
                {
                    int elemCount = 0;
                    try { elemCount = uiDocComp.rootVisualElement.childCount; } catch { }
                    sb.AppendLine($"  UIDocument: sortingOrder={uiDocComp.sortingOrder.ToString("F0", CultureInfo.InvariantCulture)}, rootVisualElement children={elemCount}");
                }
            }
            catch { }

            Component[] comps = null;
            try { comps = go.GetComponents<Component>(); } catch { }

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

        private static void InspectComponent(Component comp, int maxItems, StringBuilder sb)
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
                if (comp is RectTransform rt)
                {
                    sb.AppendLine($"  {FormatRectTransformSummary(rt)}, children={rt.childCount}");
                }
                else if (comp is Canvas canvas)
                {
                    sb.AppendLine($"  Canvas: renderMode={canvas.renderMode}, sortingOrder={canvas.sortingOrder}, sortingLayer=\"{canvas.sortingLayerName}\"");
                }
                else if (comp is UIDocument uiDoc)
                {
                    sb.AppendLine($"  UIDocument: sortingOrder={uiDoc.sortingOrder.ToString("F0", CultureInfo.InvariantCulture)}");
                    if (uiDoc.rootVisualElement != null)
                    {
                        sb.AppendLine("  VisualTree:");
                        int itemCount = 0;
                        bool itemLimitReached = false;
                        var visitedElements = new HashSet<VisualElement>(ReferenceIdentityComparer<VisualElement>.Instance);
                        TraverseVisualElement(uiDoc.rootVisualElement, 0, DefaultMaxDepth, maxItems, ref itemCount, ref itemLimitReached, visitedElements, sb, "    ");
                        return;
                    }
                }
                else if (comp is Transform tr)
                {
                    sb.AppendLine($"  {FormatLocalTransformLine(tr)}, children={tr.childCount}");
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

        private static string FormatLocalTransformLine(Transform tr)
        {
            string pos = tr.localPosition.ToString("F2", CultureInfo.InvariantCulture);
            string rot = tr.localEulerAngles.ToString("F2", CultureInfo.InvariantCulture);
            string scale = tr.localScale.ToString("F2", CultureInfo.InvariantCulture);
            return $"Transform: localPos={pos}, localRot={rot}, localScale={scale}";
        }

        private static void InspectComponentInline(Component comp, int maxItems, StringBuilder sb, string prefix)
        {
            var seenProperties = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int count = 0;

            // 1. Inspect serialized properties using SerializedObject
            try
            {
                using (var so = new SerializedObject(comp))
                {
                    var sp = so.GetIterator();
                    bool enterChildren = true;

                    while (sp.NextVisible(enterChildren))
                    {
                        enterChildren = false;
                        if (sp.name == "m_Script") continue;

                        if (count >= maxItems)
                        {
                            sb.AppendLine($"{prefix}{ItemTruncationMarker}");
                            return;
                        }

                        TrackSerializedPropertyAliases(seenProperties, sp);
                        count++;

                        string valStr = FormatSerializedPropertyWithAsset(sp);
                        sb.AppendLine($"{prefix}{sp.displayName} ({sp.name}): {valStr}");
                    }
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
                    if (field.IsStatic || seenProperties.Contains(field.Name)) continue;
                    if (field.IsDefined(typeof(ObsoleteAttribute), inherit: true)) continue;

                    if (count >= maxItems)
                    {
                        sb.AppendLine($"{prefix}{ItemTruncationMarker}");
                        return;
                    }

                    count++;
                    seenProperties.Add(field.Name);
                    AppendReflectedMember(prefix, field.Name, () => field.GetValue(comp), sb);
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
                    AppendReflectedMember(prefix, prop.Name, () => prop.GetValue(comp), sb);
                }
            }
            catch { }
        }

        private static void TrackSerializedPropertyAliases(HashSet<string> seenProperties, SerializedProperty sp)
        {
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
        }

        private static void AppendReflectedMember(string prefix, string name, Func<object> getter, StringBuilder sb)
        {
            string valStr;
            try
            {
                valStr = FormatValueSnippet(getter());
            }
            catch (Exception ex)
            {
                valStr = $"<error: {ex.InnerException?.Message ?? ex.Message}>";
            }
            sb.AppendLine($"{prefix}{name}: {valStr}");
        }

        private static void InspectUnityObject(UnityEngine.Object uo, int maxItems, StringBuilder sb)
        {
            string assetPath = null;
            try { assetPath = AssetDatabase.GetAssetPath(uo); } catch { }

            string uoName = "Unnamed";
            try { uoName = uo.name; } catch { }

            sb.AppendLine($"{uo.GetType().Name}: \"{uoName}\"");
            if (!string.IsNullOrEmpty(assetPath))
            {
                sb.AppendLine($"  Asset Path: \"{assetPath}\"");
            }

            int count = 0;
            try
            {
                using (var so = new SerializedObject(uo))
                {
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
            }
            catch
            {
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
                if (f.IsStatic || f.IsDefined(typeof(ObsoleteAttribute), inherit: true)) continue;

                count++;
                AppendGenericMember(indent, f.Name, () => f.GetValue(target), depth, maxDepth, maxItems, visited, sb);
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
                if (IsIgnoredComponentProperty(p.Name, isComponent: target is Component || target is UnityEngine.Object)) continue;

                count++;
                AppendGenericMember(indent, p.Name, () => p.GetValue(target), depth, maxDepth, maxItems, visited, sb);
            }
        }

        private static void AppendGenericMember(
            string indent,
            string memberName,
            Func<object> getter,
            int depth,
            int maxDepth,
            int maxItems,
            HashSet<object> visited,
            StringBuilder sb)
        {
            object val;
            try
            {
                val = getter();
            }
            catch (Exception ex)
            {
                sb.AppendLine($"{indent}  {memberName}: <error: {ex.InnerException?.Message ?? ex.Message}>");
                return;
            }

            if (IsLeafValue(val))
            {
                sb.AppendLine($"{indent}  {memberName}: {FormatValueSnippet(val)}");
            }
            else if (visited.Contains(val))
            {
                sb.AppendLine($"{indent}  {memberName}: {CycleTruncationMarker}");
            }
            else if (depth >= maxDepth)
            {
                sb.AppendLine($"{indent}  {memberName}: {DepthTruncationMarker}");
            }
            else
            {
                sb.AppendLine($"{indent}  {memberName}:");
                InspectObjectCore(val, depth + 1, maxDepth, maxItems, visited, sb);
            }
        }

        private static string FormatSerializedPropertyWithAsset(SerializedProperty prop)
        {
            try
            {
                if (prop.propertyType == SerializedPropertyType.ObjectReference)
                {
                    var obj = prop.objectReferenceValue;
                    return obj == null ? "null" : FormatUnityObjectReference(obj);
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

        private static string FormatUnityObjectReference(UnityEngine.Object obj)
        {
            string assetPath = null;
            try { assetPath = AssetDatabase.GetAssetPath(obj); } catch { }

            string objName = "Unnamed";
            try { objName = obj.name; } catch { }

            if (!string.IsNullOrEmpty(assetPath))
            {
                return $"\"{objName}\" ({obj.GetType().Name}) [Asset: \"{assetPath}\"]";
            }

            return $"\"{objName}\" ({obj.GetType().Name})";
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
                return uo == null ? $"null ({val.GetType().Name})" : FormatUnityObjectReference(uo);
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

        internal static bool IsIgnoredComponentProperty(string propName, bool isComponent = true)
        {
            if (string.IsNullOrEmpty(propName) || !isComponent)
            {
                return false;
            }

            return s_IgnoredComponentProperties.Contains(propName);
        }

        #endregion
    }
}
