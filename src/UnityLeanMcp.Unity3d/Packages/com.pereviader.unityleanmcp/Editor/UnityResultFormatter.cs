using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityLeanMcp
{
    public static class UnityResultFormatter
    {
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
            if (result == null)
            {
                return isVoidStatement ? null : "null";
            }

            if (result is bool b)
            {
                return b ? "true" : "false";
            }

            var type = result.GetType();

            if (type.IsPrimitive || result is string || result is decimal || type.IsEnum)
            {
                return result.ToString();
            }

            if (result is UnityEngine.Object unityObj && unityObj == null)
            {
                if (result is GameObject) return "null (GameObject)";
                if (result is Transform) return "null (Transform)";
                if (result is Component) return "null (Component)";
                return $"null ({result.GetType().Name})";
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
                        string formatted = formatter.Format(result, child => FormatResult(child, false, prettyPrint), prettyPrint);
                        if (formatted != null)
                        {
                            return formatted;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"UnityLeanMcp: Formatter {formatter.GetType().Name} failed: {ex.Message}");
                }
            }

            return result.ToString();
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
                        items.Add("... (truncated)");
                        break;
                    }
                    items.Add(childFormatter(item));
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
