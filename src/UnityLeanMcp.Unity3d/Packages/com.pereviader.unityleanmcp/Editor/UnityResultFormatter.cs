#nullable disable

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityLeanMcp
{
    public static class UnityResultFormatter
    {
        public const int DefaultMaxDepth = 32;
        public const int DefaultMaxItems = 100;
        public const int DefaultMaxOutputCharacters = 64 * 1024;
        public const int DefaultMaxOutputBytes = 64 * 1024;

        internal const string CycleTruncationMarker = "... (truncated: cycle detected)";
        internal const string DepthTruncationMarker = "... (truncated: maximum depth reached)";
        internal const string ItemTruncationMarker = "... (truncated: maximum item count reached)";
        internal const string OutputTruncationMarker = "... (truncated: maximum output size reached)";

        internal enum GraphGuardResult
        {
            Safe,
            Cycle,
            Depth,
            Items,
            Output
        }

        private sealed class ReferenceIdentityComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceIdentityComparer Instance = new ReferenceIdentityComparer();

            public new bool Equals(object x, object y) => ReferenceEquals(x, y);

            public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
        }

        internal sealed class FormattingContext
        {
            private readonly HashSet<object> m_ActiveReferences =
                new HashSet<object>(ReferenceIdentityComparer.Instance);
            private int m_ItemsVisited;
            private long m_EstimatedOutputCharacters;
            private long m_EstimatedOutputBytes;

            public int MaxDepth { get; }
            public int MaxItems { get; }
            public int MaxOutputCharacters { get; }
            public int MaxOutputBytes { get; }

            public FormattingContext(int maxDepth, int maxItems, int maxOutputCharacters, int maxOutputBytes)
            {
                MaxDepth = maxDepth;
                MaxItems = maxItems;
                MaxOutputCharacters = maxOutputCharacters;
                MaxOutputBytes = maxOutputBytes;
            }

            public bool TryEnter(object value)
            {
                return value == null || value.GetType().IsValueType || m_ActiveReferences.Add(value);
            }

            public void Exit(object value)
            {
                if (value != null && !value.GetType().IsValueType)
                {
                    m_ActiveReferences.Remove(value);
                }
            }

            public bool TryTakeItem()
            {
                if (m_ItemsVisited >= MaxItems)
                {
                    return false;
                }

                m_ItemsVisited++;
                return true;
            }

            private GraphGuardResult TryAddEstimatedOutput(int characters, int bytes)
            {
                m_EstimatedOutputCharacters += characters;
                m_EstimatedOutputBytes += bytes;
                return m_EstimatedOutputCharacters <= MaxOutputCharacters &&
                    m_EstimatedOutputBytes <= MaxOutputBytes
                    ? GraphGuardResult.Safe
                    : GraphGuardResult.Output;
            }

            public GraphGuardResult InspectChildren(object value, int depth, bool prettyPrint)
            {
                if (value == null || IsLeaf(value))
                {
                    return GraphGuardResult.Safe;
                }

                if (value is UnityEngine.Object)
                {
                    return GraphGuardResult.Safe;
                }

                if (value is IEnumerable enumerable && !(value is string))
                {
                    GraphGuardResult estimateResult = TryAddEstimatedOutput(1, 1); // [
                    if (estimateResult != GraphGuardResult.Safe)
                    {
                        return estimateResult;
                    }

                    int itemIndex = 0;
                    try
                    {
                        foreach (var item in enumerable)
                        {
                            if (!TryTakeItem())
                            {
                                return GraphGuardResult.Items;
                            }

                            if (itemIndex++ > 0)
                            {
                                estimateResult = TryAddEstimatedOutput(1, 1); // ,
                                if (estimateResult != GraphGuardResult.Safe)
                                {
                                    return estimateResult;
                                }
                            }

                            if (prettyPrint)
                            {
                                estimateResult = TryAddEstimatedOutput(1 + ((depth + 1) * 4), 1 + ((depth + 1) * 4));
                                if (estimateResult != GraphGuardResult.Safe)
                                {
                                    return estimateResult;
                                }
                            }

                            var itemResult = InspectValue(item, depth + 1, prettyPrint);
                            if (itemResult != GraphGuardResult.Safe)
                            {
                                return itemResult;
                            }
                        }
                    }
                    catch
                    {
                        // JsonUtility will report or ignore unsupported members in its
                        // normal way. A throwing custom enumerator is not a graph cycle.
                    }

                    return TryAddEstimatedOutput(1, 1); // ]
                }

                GraphGuardResult objectEstimate = TryAddEstimatedOutput(1, 1); // {
                if (objectEstimate != GraphGuardResult.Safe)
                {
                    return objectEstimate;
                }

                int fieldIndex = 0;
                for (var t = value.GetType(); t != null && t != typeof(object) && t != typeof(UnityEngine.Object); t = t.BaseType)
                {
                    foreach (var field in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    {
                        if (field.IsStatic || field.IsNotSerialized ||
                            (!field.IsPublic && !field.IsDefined(typeof(SerializeField), inherit: true)))
                        {
                            continue;
                        }

                        if (fieldIndex++ > 0)
                        {
                            objectEstimate = TryAddEstimatedOutput(1, 1); // ,
                            if (objectEstimate != GraphGuardResult.Safe)
                            {
                                return objectEstimate;
                            }
                        }

                        if (prettyPrint)
                        {
                            objectEstimate = TryAddEstimatedOutput(1 + ((depth + 1) * 4), 1 + ((depth + 1) * 4));
                            if (objectEstimate != GraphGuardResult.Safe)
                            {
                                return objectEstimate;
                            }
                        }

                        objectEstimate = EstimateJsonString(field.Name);
                        if (objectEstimate != GraphGuardResult.Safe)
                        {
                            return objectEstimate;
                        }

                        objectEstimate = TryAddEstimatedOutput(1, 1); // :
                        if (objectEstimate != GraphGuardResult.Safe)
                        {
                            return objectEstimate;
                        }

                        object fieldValue;
                        try
                        {
                            fieldValue = field.GetValue(value);
                        }
                        catch
                        {
                            continue;
                        }

                        var fieldResult = InspectValue(fieldValue, depth + 1, prettyPrint);
                        if (fieldResult != GraphGuardResult.Safe)
                        {
                            return fieldResult;
                        }
                    }
                }

                return TryAddEstimatedOutput(1, 1); // }
            }

            private GraphGuardResult InspectValue(object value, int depth, bool prettyPrint)
            {
                if (value == null)
                {
                    return TryAddEstimatedOutput(4, 4); // null
                }

                if (depth > MaxDepth)
                {
                    return GraphGuardResult.Depth;
                }

                if (value is string text)
                {
                    return EstimateJsonString(text);
                }

                if (IsLeaf(value) || value is UnityEngine.Object)
                {
                    string leafText = value.ToString();
                    return TryAddEstimatedOutput(leafText.Length, Encoding.UTF8.GetByteCount(leafText));
                }

                if (!TryEnter(value))
                {
                    return GraphGuardResult.Cycle;
                }

                try
                {
                    return InspectChildren(value, depth, prettyPrint);
                }
                finally
                {
                    Exit(value);
                }
            }

            private GraphGuardResult EstimateJsonString(string value)
            {
                int characters = 2;
                int bytes = 2;
                for (int i = 0; i < value.Length; i++)
                {
                    char character = value[i];
                    if (character == '"' || character == '\\' || character < ' ')
                    {
                        characters += character < ' ' && character != '\t' && character != '\n' && character != '\r' ? 6 : 2;
                        bytes += character < ' ' && character != '\t' && character != '\n' && character != '\r' ? 6 : 2;
                    }
                    else
                    {
                        characters++;
                        bytes += Encoding.UTF8.GetByteCount(value, i, 1);
                    }
                }

                return TryAddEstimatedOutput(characters, bytes);
            }

            private static bool IsLeaf(object value)
            {
                Type type = value.GetType();
                return type.IsPrimitive || value is decimal || type.IsEnum;
            }
        }

        private static int s_MaxDepth = DefaultMaxDepth;
        private static int s_MaxItems = DefaultMaxItems;
        private static int s_MaxOutputCharacters = DefaultMaxOutputCharacters;
        private static int s_MaxOutputBytes = DefaultMaxOutputBytes;

        /// <summary>
        /// Maximum recursive child depth. The root result is depth zero.
        /// </summary>
        public static int MaxDepth
        {
            get => Volatile.Read(ref s_MaxDepth);
            set => SetPositiveLimit(ref s_MaxDepth, value, nameof(MaxDepth), allowZero: true);
        }

        /// <summary>
        /// Maximum number of enumerable items formatted during one result formatting call.
        /// </summary>
        public static int MaxItems
        {
            get => Volatile.Read(ref s_MaxItems);
            set => SetPositiveLimit(ref s_MaxItems, value, nameof(MaxItems), allowZero: true);
        }

        /// <summary>
        /// Maximum UTF-16 character count returned by one result formatting call.
        /// </summary>
        public static int MaxOutputCharacters
        {
            get => Volatile.Read(ref s_MaxOutputCharacters);
            set => SetPositiveLimit(ref s_MaxOutputCharacters, value, nameof(MaxOutputCharacters));
        }

        /// <summary>
        /// Maximum UTF-8 byte count returned by one result formatting call.
        /// </summary>
        public static int MaxOutputBytes
        {
            get => Volatile.Read(ref s_MaxOutputBytes);
            set => SetPositiveLimit(ref s_MaxOutputBytes, value, nameof(MaxOutputBytes));
        }

        private static void SetPositiveLimit(ref int target, int value, string parameterName, bool allowZero = false)
        {
            if (value < 0 || (!allowZero && value == 0))
            {
                throw new ArgumentOutOfRangeException(parameterName, value, "The formatting limit must be positive.");
            }

            Interlocked.Exchange(ref target, value);
        }

        public static void EnsureInitialized()
        {
        }

        public static string FormatResult(object result, bool isVoidStatement = false, bool prettyPrint = true)
        {
            var context = new FormattingContext(MaxDepth, MaxItems, MaxOutputCharacters, MaxOutputBytes);
            return LimitOutput(FormatResult(result, isVoidStatement, prettyPrint, context, 0), context);
        }

        private static string FormatResult(
            object result,
            bool isVoidStatement,
            bool prettyPrint,
            FormattingContext context,
            int depth)
        {
            if (result == null)
            {
                return isVoidStatement ? null : "null";
            }

            if (depth > context.MaxDepth)
            {
                return DepthTruncationMarker;
            }

            if (!context.TryEnter(result))
            {
                return CycleTruncationMarker;
            }

            try
            {
                return FormatEnteredResult(result, prettyPrint, context, depth);
            }
            finally
            {
                context.Exit(result);
            }
        }

        private static string FormatEnteredResult(object result, bool prettyPrint, FormattingContext context, int depth)
        {
            if (result is bool b)
            {
                return LimitOutput(b ? "true" : "false", context);
            }

            var type = result.GetType();

            if (type.IsPrimitive || result is string || result is decimal || type.IsEnum)
            {
                return LimitOutput(result.ToString(), context);
            }

            if (result is UnityEngine.Object unityObj && unityObj == null)
            {
                if (result is GameObject) return LimitOutput("null (GameObject)", context);
                if (result is Transform) return LimitOutput("null (Transform)", context);
                if (result is Component) return LimitOutput("null (Component)", context);
                return LimitOutput($"null ({result.GetType().Name})", context);
            }

            if (result is Transform t)
            {
                return LimitOutput(FormatTransform(t), context);
            }

            if (result is GameObject go)
            {
                return LimitOutput(FormatGameObject(go), context);
            }

            if (result is Component comp)
            {
                return LimitOutput(FormatComponent(comp), context);
            }

            if (result is ScriptableObject so)
            {
                return LimitOutput(FormatScriptableObject(so), context);
            }

            if (result is Scene scene)
            {
                return LimitOutput(FormatScene(scene), context);
            }

            if (result is SerializedObject serializedObj)
            {
                return LimitOutput(FormatSerializedObject(serializedObj), context);
            }

            if (result is SerializedProperty prop)
            {
                return LimitOutput(FormatSerializedProperty(prop), context);
            }

            if (IsAnonymousType(result))
            {
                return LimitOutput(FormatAnonymousType(result, child => FormatChild(child, prettyPrint, context, depth), prettyPrint), context);
            }

            if (result is IEnumerable enumerable && !(result is string))
            {
                return LimitOutput(FormatEnumerable(enumerable, child => FormatChild(child, prettyPrint, context, depth), prettyPrint), context);
            }

            string mathFormatted = FormatUnityMath(result);
            if (mathFormatted != null)
            {
                return LimitOutput(mathFormatted, context);
            }

            GraphGuardResult graphResult = context.InspectChildren(result, depth, prettyPrint);
            if (graphResult != GraphGuardResult.Safe)
            {
                return LimitOutput(GetGraphGuardMarker(graphResult), context);
            }

            string json = FormatJsonUtilityFallback(result, prettyPrint);
            if (json != null)
            {
                return LimitOutput(json, context);
            }

            return LimitOutput(result.ToString(), context);
        }

        private static string GetGraphGuardMarker(GraphGuardResult graphResult)
        {
            switch (graphResult)
            {
                case GraphGuardResult.Cycle:
                    return CycleTruncationMarker;
                case GraphGuardResult.Depth:
                    return DepthTruncationMarker;
                case GraphGuardResult.Items:
                    return ItemTruncationMarker;
                case GraphGuardResult.Output:
                    return OutputTruncationMarker;
                default:
                    return null;
            }
        }

        private static string FormatChild(object child, bool prettyPrint, FormattingContext context, int parentDepth)
        {
            if (!context.TryTakeItem())
            {
                return ItemTruncationMarker;
            }

            return FormatResult(child, false, prettyPrint, context, parentDepth + 1);
        }

        internal static string LimitOutput(string value, FormattingContext context)
        {
            if (value == null)
            {
                return null;
            }

            if (value.Length <= context.MaxOutputCharacters &&
                Encoding.UTF8.GetByteCount(value) <= context.MaxOutputBytes)
            {
                return value;
            }

            string marker = GetOutputTruncationMarker(context);
            if (marker.Length == 0)
            {
                return string.Empty;
            }

            int markerBytes = Encoding.UTF8.GetByteCount(marker);
            int maxPrefixBytes = context.MaxOutputBytes - markerBytes;
            int prefixLength = Math.Min(value.Length, context.MaxOutputCharacters - marker.Length);
            if (prefixLength > 0 && prefixLength < value.Length && char.IsHighSurrogate(value[prefixLength - 1]))
            {
                prefixLength--;
            }

            int currentBytes = prefixLength > 0 ? Encoding.UTF8.GetByteCount(value, 0, prefixLength) : 0;
            while (prefixLength > 0 && currentBytes > maxPrefixBytes)
            {
                int step = prefixLength >= 2 &&
                           char.IsHighSurrogate(value[prefixLength - 2]) &&
                           char.IsLowSurrogate(value[prefixLength - 1])
                    ? 2
                    : 1;
                prefixLength -= step;
                currentBytes -= Encoding.UTF8.GetByteCount(value, prefixLength, step);
                if (prefixLength > 0 && char.IsHighSurrogate(value[prefixLength - 1]))
                {
                    prefixLength--;
                    currentBytes -= Encoding.UTF8.GetByteCount(value, prefixLength, 1);
                }
            }

            return value.Substring(0, prefixLength) + marker;
        }

        private static string GetOutputTruncationMarker(FormattingContext context)
        {
            if (FitsOutputBudget(OutputTruncationMarker, context))
            {
                return OutputTruncationMarker;
            }

            const string shortMarker = "... (truncated)";
            if (FitsOutputBudget(shortMarker, context))
            {
                return shortMarker;
            }

            const string compactMarker = "[truncated]";
            if (FitsOutputBudget(compactMarker, context))
            {
                return compactMarker;
            }

            const string ellipsisMarker = "...";
            if (FitsOutputBudget(ellipsisMarker, context))
            {
                return ellipsisMarker;
            }

            const string singleCharacterMarker = ".";
            return FitsOutputBudget(singleCharacterMarker, context) ? singleCharacterMarker : string.Empty;
        }

        private static bool FitsOutputBudget(string value, FormattingContext context)
        {
            return value.Length <= context.MaxOutputCharacters &&
                Encoding.UTF8.GetByteCount(value) <= context.MaxOutputBytes;
        }

        public static string FormatSerializedPropertyValue(SerializedProperty prop)
        {
            try
            {
                switch (prop.propertyType)
                {
                    case SerializedPropertyType.Integer:
                        return prop.intValue.ToString();
                    case SerializedPropertyType.Boolean:
                        return prop.boolValue ? "true" : "false";
                    case SerializedPropertyType.Float:
                        return prop.floatValue.ToString(CultureInfo.InvariantCulture);
                    case SerializedPropertyType.String:
                        return $"\"{prop.stringValue}\"";
                    case SerializedPropertyType.Color:
                        return prop.colorValue.ToString();
                    case SerializedPropertyType.ObjectReference:
                        var obj = prop.objectReferenceValue;
                        return obj != null ? $"\"{obj.name}\" ({obj.GetType().Name})" : "null";
                    case SerializedPropertyType.Enum:
                        return prop.enumNames != null && prop.enumValueIndex >= 0 && prop.enumValueIndex < prop.enumNames.Length
                            ? prop.enumNames[prop.enumValueIndex]
                            : prop.enumValueIndex.ToString();
                    case SerializedPropertyType.Vector2:
                        return prop.vector2Value.ToString("F2", CultureInfo.InvariantCulture);
                    case SerializedPropertyType.Vector3:
                        return prop.vector3Value.ToString("F2", CultureInfo.InvariantCulture);
                    case SerializedPropertyType.Vector4:
                        return prop.vector4Value.ToString("F2", CultureInfo.InvariantCulture);
                    case SerializedPropertyType.Rect:
                        return prop.rectValue.ToString("F2", CultureInfo.InvariantCulture);
                    case SerializedPropertyType.ArraySize:
                        return prop.intValue.ToString();
                    case SerializedPropertyType.Character:
                        return ((char)prop.intValue).ToString();
                    default:
                        return null;
                }
            }
            catch
            {
                return null;
            }
        }

        private static string FormatTransform(Transform t)
        {
            if (t == null) return "null (Transform)";
            string pos = t.localPosition.ToString("F2", CultureInfo.InvariantCulture);
            string rot = t.localEulerAngles.ToString("F2", CultureInfo.InvariantCulture);
            return $"Transform \"{t.name}\" [children: {t.childCount}, localPos: {pos}, localRot: {rot}]";
        }

        private static string FormatGameObject(GameObject go)
        {
            if (go == null) return "null (GameObject)";
            var compNames = go.GetComponents<Component>()
                .Where(c => c != null)
                .Select(c => c.GetType().Name);

            return $"{go.name} (GameObject) [active: {go.activeSelf}, tag: \"{go.tag}\", layer: {go.layer}, components: {string.Join(", ", compNames)}]";
        }

        private static string FormatComponent(Component comp)
        {
            if (comp == null) return "null (Component)";
            string goName = comp.gameObject != null ? comp.gameObject.name : "null";
            return $"{comp.GetType().Name} (Component on \"{goName}\")";
        }

        private static string FormatScriptableObject(ScriptableObject so)
        {
            if (so == null) return $"null ({so?.GetType().Name ?? "ScriptableObject"})";
            return $"{so.GetType().Name} (ScriptableObject) [name: \"{so.name}\", assetPath: \"{AssetDatabase.GetAssetPath(so)}\"]";
        }

        private static string FormatScene(Scene scene)
        {
            return $"Scene \"{scene.name}\" [path: \"{scene.path}\", isLoaded: {scene.isLoaded}, isDirty: {scene.isDirty}, rootCount: {scene.rootCount}]";
        }

        private static string FormatSerializedObject(SerializedObject serializedObj)
        {
            try
            {
                var target = serializedObj.targetObject;
                return $"SerializedObject on \"{target?.name}\" ({target?.GetType().Name})";
            }
            catch
            {
                return "SerializedObject (targetObject disposed or inaccessible)";
            }
        }

        private static string FormatSerializedProperty(SerializedProperty prop)
        {
            string propVal = FormatSerializedPropertyValue(prop);
            return propVal != null
                ? $"SerializedProperty \"{prop.propertyPath}\" ({prop.propertyType}) = {propVal}"
                : $"SerializedProperty \"{prop.propertyPath}\" ({prop.propertyType})";
        }

        internal static bool IsAnonymousType(object value)
        {
            if (value == null) return false;
            var type = value.GetType();
            return type.IsSealed &&
                   type.Namespace == null &&
                   type.Name.Contains("AnonymousType") &&
                   type.IsDefined(typeof(CompilerGeneratedAttribute), false);
        }

        private static string FormatAnonymousType(object value, Func<object, string> formatChild, bool prettyPrint)
        {
            var type = value.GetType();
            var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
            if (props.Length == 0) return "{}";

            var validProps = new List<PropertyInfo>();
            for (int i = 0; i < props.Length; i++)
            {
                var p = props[i];
                if (!p.CanRead || p.GetIndexParameters().Length > 0) continue;
                validProps.Add(p);
            }
            if (validProps.Count == 0) return "{}";

            var sb = new StringBuilder();
            if (prettyPrint)
            {
                sb.AppendLine("{");
                for (int i = 0; i < validProps.Count; i++)
                {
                    var prop = validProps[i];
                    object propVal = null;
                    try { propVal = prop.GetValue(value, null); } catch (Exception ex) { propVal = $"<error: {ex.Message}>"; }
                    string childStr = formatChild != null ? formatChild(propVal) : FormatResult(propVal, false, prettyPrint);
                    sb.Append("  ").Append(prop.Name).Append(" = ").Append(childStr);
                    if (i < validProps.Count - 1) sb.Append(",");
                    sb.AppendLine();
                }
                sb.Append("}");
            }
            else
            {
                sb.Append("{ ");
                for (int i = 0; i < validProps.Count; i++)
                {
                    var prop = validProps[i];
                    object propVal = null;
                    try { propVal = prop.GetValue(value, null); } catch (Exception ex) { propVal = $"<error: {ex.Message}>"; }
                    string childStr = formatChild != null ? formatChild(propVal) : FormatResult(propVal, false, prettyPrint);
                    sb.Append(prop.Name).Append(" = ").Append(childStr);
                    if (i < validProps.Count - 1) sb.Append(", ");
                }
                sb.Append(" }");
            }
            return sb.ToString();
        }

        private static string FormatEnumerable(IEnumerable enumerable, Func<object, string> childFormatter, bool prettyPrint)
        {
            var items = new List<string>();
            int count = 0;
            Func<object, string> formatter = childFormatter ?? (child => FormatResult(child, false, prettyPrint));
            foreach (var item in enumerable)
            {
                count++;
                if (count > MaxItems)
                {
                    items.Add(ItemTruncationMarker);
                    break;
                }

                string formattedItem = formatter(item);
                items.Add(formattedItem);
                if (formattedItem == ItemTruncationMarker)
                {
                    break;
                }
            }
            return "[" + string.Join(", ", items) + "]";
        }

        private static string FormatUnityMath(object value)
        {
            switch (value)
            {
                case Vector2 v:
                    return v.ToString("F2", CultureInfo.InvariantCulture);
                case Vector3 v:
                    return v.ToString("F2", CultureInfo.InvariantCulture);
                case Vector4 v:
                    return v.ToString("F2", CultureInfo.InvariantCulture);
                case Vector2Int v:
                    return v.ToString();
                case Vector3Int v:
                    return v.ToString();
                case Quaternion q:
                    return q.ToString("F2", CultureInfo.InvariantCulture);
                case Color c:
                    return c.ToString("F3", CultureInfo.InvariantCulture);
                case Color32 c32:
                    return $"RGBA({c32.r}, {c32.g}, {c32.b}, {c32.a})";
                case Bounds b:
                    return $"Center: {b.center.ToString("F2", CultureInfo.InvariantCulture)}, Extents: {b.extents.ToString("F2", CultureInfo.InvariantCulture)}";
                case BoundsInt bi:
                    return $"Position: {bi.position}, Size: {bi.size}";
                case Rect r:
                    return r.ToString("F2", CultureInfo.InvariantCulture);
                case RectInt ri:
                    return ri.ToString();
                case Ray ray:
                    return $"Origin: {ray.origin.ToString("F2", CultureInfo.InvariantCulture)}, Direction: {ray.direction.ToString("F2", CultureInfo.InvariantCulture)}";
                case Ray2D ray2:
                    return $"Origin: {ray2.origin.ToString("F2", CultureInfo.InvariantCulture)}, Direction: {ray2.direction.ToString("F2", CultureInfo.InvariantCulture)}";
                case Plane plane:
                    return $"Normal: {plane.normal.ToString("F2", CultureInfo.InvariantCulture)}, Distance: {plane.distance.ToString("F2", CultureInfo.InvariantCulture)}";
                default:
                    return null;
            }
        }

        private static string FormatJsonUtilityFallback(object value, bool prettyPrint)
        {
            try
            {
                string json = JsonUtility.ToJson(value, prettyPrint);
                if (!string.IsNullOrEmpty(json) && json.Trim() != "{}")
                {
                    return json;
                }
            }
            catch
            {
            }
            return null;
        }
    }
}
