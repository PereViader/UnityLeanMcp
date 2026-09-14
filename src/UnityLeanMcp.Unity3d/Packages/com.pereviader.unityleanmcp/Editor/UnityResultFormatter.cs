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
        public const int DefaultMaxOutputBytes = 256 * 1024;

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
            private int m_ItemsFormatted;
            private int m_ItemsInspected;
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
                if (m_ItemsFormatted >= MaxItems)
                {
                    return false;
                }

                m_ItemsFormatted++;
                return true;
            }

            private bool TryTakeInspectionItem()
            {
                if (m_ItemsInspected >= MaxItems)
                {
                    return false;
                }

                m_ItemsInspected++;
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
                            if (!TryTakeInspectionItem())
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
                foreach (var field in value.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (field.IsStatic || field.IsNotSerialized ||
                        (field.IsPrivate && !field.IsDefined(typeof(SerializeField), inherit: true)))
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

        private sealed class FormatterEntry
        {
            public IUnityTypeFormatter Formatter { get; }
            public int Order { get; }

            public FormatterEntry(IUnityTypeFormatter formatter, int order)
            {
                Formatter = formatter;
                Order = order;
            }
        }

        private static readonly object s_Lock = new object();
        private static readonly List<FormatterEntry> s_Entries = new List<FormatterEntry>();
        private static volatile IUnityTypeFormatter[] s_SortedFormattersSnapshot;
        private static bool s_DefaultFormattersRegistered;
        private static int s_OrderCounter;

        public static void EnsureInitialized()
        {
            EnsureDefaultFormatters();
        }

        public static void RegisterFormatter(IUnityTypeFormatter formatter)
        {
            if (formatter == null)
            {
                throw new ArgumentNullException(nameof(formatter));
            }

            lock (s_Lock)
            {
                EnsureDefaultFormattersLocked();
                s_Entries.RemoveAll(e => e.Formatter.Equals(formatter));
                s_Entries.Add(new FormatterEntry(formatter, ++s_OrderCounter));
                SortEntriesLocked();
            }
        }

        public static bool UnregisterFormatter(IUnityTypeFormatter formatter)
        {
            if (formatter == null)
            {
                return false;
            }

            lock (s_Lock)
            {
                EnsureDefaultFormattersLocked();
                int removed = s_Entries.RemoveAll(e => e.Formatter.Equals(formatter));
                if (removed > 0)
                {
                    SortEntriesLocked();
                    return true;
                }
                return false;
            }
        }

        public static bool UnregisterFormatter<T>() where T : IUnityTypeFormatter
        {
            lock (s_Lock)
            {
                EnsureDefaultFormattersLocked();
                int removed = s_Entries.RemoveAll(e => e.Formatter is T);
                if (removed > 0)
                {
                    SortEntriesLocked();
                    return true;
                }
                return false;
            }
        }

        public static void ResetToDefaults()
        {
            lock (s_Lock)
            {
                s_Entries.Clear();
                s_DefaultFormattersRegistered = false;
                EnsureDefaultFormattersLocked();
            }
        }

        public static IReadOnlyList<IUnityTypeFormatter> Formatters
        {
            get
            {
                EnsureDefaultFormatters();
                return s_SortedFormattersSnapshot;
            }
        }

        private static void EnsureDefaultFormatters()
        {
            if (s_DefaultFormattersRegistered) return;
            lock (s_Lock)
            {
                EnsureDefaultFormattersLocked();
            }
        }

        private static void EnsureDefaultFormattersLocked()
        {
            if (s_DefaultFormattersRegistered) return;

            s_Entries.Add(new FormatterEntry(new TransformFormatter(110), ++s_OrderCounter));
            s_Entries.Add(new FormatterEntry(new GameObjectFormatter(100), ++s_OrderCounter));
            s_Entries.Add(new FormatterEntry(new ComponentFormatter(90), ++s_OrderCounter));
            s_Entries.Add(new FormatterEntry(new ScriptableObjectFormatter(80), ++s_OrderCounter));
            s_Entries.Add(new FormatterEntry(new SceneFormatter(70), ++s_OrderCounter));
            s_Entries.Add(new FormatterEntry(new SerializedObjectFormatter(60), ++s_OrderCounter));
            s_Entries.Add(new FormatterEntry(new SerializedPropertyFormatter(50), ++s_OrderCounter));
            s_Entries.Add(new FormatterEntry(new EnumerableFormatter(40), ++s_OrderCounter));
            s_Entries.Add(new FormatterEntry(new JsonUtilityFallbackFormatter(-1000), ++s_OrderCounter));

            SortEntriesLocked();
            s_DefaultFormattersRegistered = true;
        }

        private static void SortEntriesLocked()
        {
            s_Entries.Sort((a, b) =>
            {
                int cmp = b.Formatter.Priority.CompareTo(a.Formatter.Priority);
                return cmp != 0 ? cmp : a.Order.CompareTo(b.Order);
            });
            s_SortedFormattersSnapshot = s_Entries.Select(e => e.Formatter).ToArray();
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

            EnsureDefaultFormatters();
            var formatters = s_SortedFormattersSnapshot;
            for (int i = 0; i < formatters.Length; i++)
            {
                var formatter = formatters[i];
                try
                {
                    if (formatter.CanFormat(result))
                    {
                        if (formatter is JsonUtilityFallbackFormatter)
                        {
                            GraphGuardResult graphResult = context.InspectChildren(result, depth, prettyPrint);
                            if (graphResult != GraphGuardResult.Safe)
                            {
                                return LimitOutput(GetGraphGuardMarker(graphResult), context);
                            }
                        }

                        string formatted = formatter.Format(
                            result,
                            child => FormatChild(child, prettyPrint, context, depth),
                            prettyPrint);
                        if (formatted != null)
                        {
                            return LimitOutput(formatted, context);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"UnityLeanMcp: Formatter {formatter.GetType().Name} failed: {ex.Message}");
                }
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

        private static string LimitOutput(string value, FormattingContext context)
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
            int prefixLength = Math.Min(value.Length, context.MaxOutputCharacters - marker.Length);
            while (prefixLength > 0 &&
                   (Encoding.UTF8.GetByteCount(value, 0, prefixLength) + markerBytes > context.MaxOutputBytes ||
                    (prefixLength < value.Length && char.IsHighSurrogate(value[prefixLength - 1]))))
            {
                prefixLength--;
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
                        return prop.vector2Value.ToString();
                    case SerializedPropertyType.Vector3:
                        return prop.vector3Value.ToString();
                    case SerializedPropertyType.Vector4:
                        return prop.vector4Value.ToString();
                    case SerializedPropertyType.Rect:
                        return prop.rectValue.ToString();
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
    }

    public class TransformFormatter : IUnityTypeFormatter
    {
        public int Priority { get; }

        public TransformFormatter(int priority = 110)
        {
            Priority = priority;
        }

        public bool CanFormat(object value) => value is Transform;

        public string Format(object value, Func<object, string> formatChild, bool prettyPrint = true)
        {
            if (value is Transform t)
            {
                if (t == null) return "null (Transform)";
                return $"Transform \"{t.name}\" [children: {t.childCount}, localPos: {t.localPosition}, localRot: {t.localEulerAngles}]";
            }
            return null;
        }

        public override bool Equals(object obj) =>
            obj != null && obj.GetType() == GetType() && ((IUnityTypeFormatter)obj).Priority == Priority;

        public override int GetHashCode() =>
            (GetType().GetHashCode() * 397) ^ Priority.GetHashCode();
    }

    public class GameObjectFormatter : IUnityTypeFormatter
    {
        public int Priority { get; }

        public GameObjectFormatter(int priority = 100)
        {
            Priority = priority;
        }

        public bool CanFormat(object value) => value is GameObject;

        public string Format(object value, Func<object, string> formatChild, bool prettyPrint = true)
        {
            if (value is GameObject go)
            {
                if (go == null) return "null (GameObject)";
                var compNames = go.GetComponents<Component>()
                    .Where(c => c != null)
                    .Select(c => c.GetType().Name);

                return $"{go.name} (GameObject) [active: {go.activeSelf}, tag: \"{go.tag}\", layer: {go.layer}, components: {string.Join(", ", compNames)}]";
            }
            return null;
        }

        public override bool Equals(object obj) =>
            obj != null && obj.GetType() == GetType() && ((IUnityTypeFormatter)obj).Priority == Priority;

        public override int GetHashCode() =>
            (GetType().GetHashCode() * 397) ^ Priority.GetHashCode();
    }

    public class ComponentFormatter : IUnityTypeFormatter
    {
        public int Priority { get; }

        public ComponentFormatter(int priority = 90)
        {
            Priority = priority;
        }

        public bool CanFormat(object value) => value is Component;

        public string Format(object value, Func<object, string> formatChild, bool prettyPrint = true)
        {
            if (value is Component comp)
            {
                if (comp == null) return "null (Component)";
                string goName = comp.gameObject != null ? comp.gameObject.name : "null";
                return $"{comp.GetType().Name} (Component on \"{goName}\")";
            }
            return null;
        }

        public override bool Equals(object obj) =>
            obj != null && obj.GetType() == GetType() && ((IUnityTypeFormatter)obj).Priority == Priority;

        public override int GetHashCode() =>
            (GetType().GetHashCode() * 397) ^ Priority.GetHashCode();
    }

    public class ScriptableObjectFormatter : IUnityTypeFormatter
    {
        public int Priority { get; }

        public ScriptableObjectFormatter(int priority = 80)
        {
            Priority = priority;
        }

        public bool CanFormat(object value) => value is ScriptableObject;

        public string Format(object value, Func<object, string> formatChild, bool prettyPrint = true)
        {
            if (value is ScriptableObject so)
            {
                if (so == null) return $"null ({value?.GetType().Name ?? "ScriptableObject"})";
                return $"{so.GetType().Name} (ScriptableObject) [name: \"{so.name}\", assetPath: \"{AssetDatabase.GetAssetPath(so)}\"]";
            }
            return null;
        }

        public override bool Equals(object obj) =>
            obj != null && obj.GetType() == GetType() && ((IUnityTypeFormatter)obj).Priority == Priority;

        public override int GetHashCode() =>
            (GetType().GetHashCode() * 397) ^ Priority.GetHashCode();
    }

    public class SceneFormatter : IUnityTypeFormatter
    {
        public int Priority { get; }

        public SceneFormatter(int priority = 70)
        {
            Priority = priority;
        }

        public bool CanFormat(object value) => value is Scene;

        public string Format(object value, Func<object, string> formatChild, bool prettyPrint = true)
        {
            if (value is Scene scene)
            {
                return $"Scene \"{scene.name}\" [path: \"{scene.path}\", isLoaded: {scene.isLoaded}, isDirty: {scene.isDirty}, rootCount: {scene.rootCount}]";
            }
            return null;
        }

        public override bool Equals(object obj) =>
            obj != null && obj.GetType() == GetType() && ((IUnityTypeFormatter)obj).Priority == Priority;

        public override int GetHashCode() =>
            (GetType().GetHashCode() * 397) ^ Priority.GetHashCode();
    }

    public class SerializedObjectFormatter : IUnityTypeFormatter
    {
        public int Priority { get; }

        public SerializedObjectFormatter(int priority = 60)
        {
            Priority = priority;
        }

        public bool CanFormat(object value) => value is SerializedObject;

        public string Format(object value, Func<object, string> formatChild, bool prettyPrint = true)
        {
            if (value is SerializedObject serializedObj)
            {
                return $"SerializedObject on \"{serializedObj.targetObject?.name}\" ({serializedObj.targetObject?.GetType().Name})";
            }
            return null;
        }

        public override bool Equals(object obj) =>
            obj != null && obj.GetType() == GetType() && ((IUnityTypeFormatter)obj).Priority == Priority;

        public override int GetHashCode() =>
            (GetType().GetHashCode() * 397) ^ Priority.GetHashCode();
    }

    public class SerializedPropertyFormatter : IUnityTypeFormatter
    {
        public int Priority { get; }

        public SerializedPropertyFormatter(int priority = 50)
        {
            Priority = priority;
        }

        public bool CanFormat(object value) => value is SerializedProperty;

        public string Format(object value, Func<object, string> formatChild, bool prettyPrint = true)
        {
            if (value is SerializedProperty prop)
            {
                string propVal = UnityResultFormatter.FormatSerializedPropertyValue(prop);
                return propVal != null
                    ? $"SerializedProperty \"{prop.propertyPath}\" ({prop.propertyType}) = {propVal}"
                    : $"SerializedProperty \"{prop.propertyPath}\" ({prop.propertyType})";
            }
            return null;
        }

        public override bool Equals(object obj) =>
            obj != null && obj.GetType() == GetType() && ((IUnityTypeFormatter)obj).Priority == Priority;

        public override int GetHashCode() =>
            (GetType().GetHashCode() * 397) ^ Priority.GetHashCode();
    }

    public class EnumerableFormatter : IUnityTypeFormatter
    {
        public int Priority { get; }

        public EnumerableFormatter(int priority = 40)
        {
            Priority = priority;
        }

        public bool CanFormat(object value) => value is IEnumerable && !(value is string);

        public string Format(object value, Func<object, string> formatChild, bool prettyPrint = true)
        {
            if (value is IEnumerable enumerable && !(value is string))
            {
                var items = new List<string>();
                int count = 0;
                Func<object, string> childFormatter = formatChild ?? (child => UnityResultFormatter.FormatResult(child, false, prettyPrint));
                foreach (var item in enumerable)
                {
                    count++;
                    if (count > 100)
                    {
                        items.Add(UnityResultFormatter.ItemTruncationMarker);
                        break;
                    }

                    string formattedItem = childFormatter(item);
                    items.Add(formattedItem);
                    if (formattedItem == UnityResultFormatter.ItemTruncationMarker)
                    {
                        break;
                    }
                }
                return "[" + string.Join(", ", items) + "]";
            }
            return null;
        }

        public override bool Equals(object obj) =>
            obj != null && obj.GetType() == GetType() && ((IUnityTypeFormatter)obj).Priority == Priority;

        public override int GetHashCode() =>
            (GetType().GetHashCode() * 397) ^ Priority.GetHashCode();
    }

    public class JsonUtilityFallbackFormatter : IUnityTypeFormatter
    {
        public int Priority { get; }

        public JsonUtilityFallbackFormatter(int priority = -1000)
        {
            Priority = priority;
        }

        public bool CanFormat(object value) => value != null;

        public string Format(object value, Func<object, string> formatChild, bool prettyPrint = true)
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

        public override bool Equals(object obj) =>
            obj != null && obj.GetType() == GetType() && ((IUnityTypeFormatter)obj).Priority == Priority;

        public override int GetHashCode() =>
            (GetType().GetHashCode() * 397) ^ Priority.GetHashCode();
    }
}
