using System;
using System.Collections.Generic;
using System.Globalization;

namespace UnityEngine
{
    public static class JsonUtility
    {
        private static readonly System.Text.Json.JsonSerializerOptions Options = new() { IncludeFields = true };
        public static string ToJson<T>(T value, bool prettyPrint = false) => System.Text.Json.JsonSerializer.Serialize(value, Options);
        public static T? FromJson<T>(string json) => System.Text.Json.JsonSerializer.Deserialize<T>(json, Options);
    }

    public class Object
    {
        public string name { get; set; } = string.Empty;
        public bool _isDestroyed { get; internal set; }
        public override string ToString() => string.IsNullOrEmpty(name) ? GetType().Name : name;

        public static bool operator ==(Object? x, Object? y)
        {
            bool xNull = ReferenceEquals(x, null) || x._isDestroyed;
            bool yNull = ReferenceEquals(y, null) || y._isDestroyed;
            if (xNull && yNull) return true;
            if (xNull || yNull) return false;
            return ReferenceEquals(x, y);
        }

        public static bool operator !=(Object? x, Object? y) => !(x == y);
        public override bool Equals(object? obj) => this == (obj as Object);
        public override int GetHashCode() => ReferenceEquals(this, null) ? 0 : base.GetHashCode();

        public static void DestroyImmediate(Object? obj)
        {
            if (!ReferenceEquals(obj, null))
            {
                obj._isDestroyed = true;
                if (obj is GameObject go)
                {
                    if (go.transform != null) go.transform._isDestroyed = true;
                    foreach (var c in go.GetComponents<Component>())
                    {
                        if (c != null) c._isDestroyed = true;
                    }
                }
            }
        }

        public static Func<Type, object[]>? FindObjectsOfTypeFunc;

        public static T? FindObjectOfType<T>() where T : Object
        {
            var arr = FindObjectsOfType<T>();
            return arr.Length > 0 ? arr[0] : null;
        }

        public static T[] FindObjectsOfType<T>() where T : Object
        {
            if (FindObjectsOfTypeFunc != null)
            {
                var custom = FindObjectsOfTypeFunc(typeof(T));
                var res = new List<T>();
                foreach (var obj in custom)
                {
                    if (obj is T t) res.Add(t);
                }
                return res.ToArray();
            }

            if (typeof(T) == typeof(Camera) || typeof(T).IsSubclassOf(typeof(Camera)))
            {
                var res = new List<T>();
                foreach (var c in Camera.AllCamerasList)
                {
                    if (c is T t) res.Add(t);
                }
                return res.ToArray();
            }

            return Array.Empty<T>();
        }
    }

    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public override string ToString() => $"({x.ToString("F2", CultureInfo.InvariantCulture)}, {y.ToString("F2", CultureInfo.InvariantCulture)}, {z.ToString("F2", CultureInfo.InvariantCulture)})";
        public string ToString(string format, IFormatProvider provider) => $"({x.ToString(format, provider)}, {y.ToString(format, provider)}, {z.ToString(format, provider)})";
    }

    public struct Rect
    {
        public float x, y, width, height;
        public Rect(float x, float y, float width, float height) { this.x = x; this.y = y; this.width = width; this.height = height; }
        public override string ToString() => $"(x:{x.ToString("F2", CultureInfo.InvariantCulture)}, y:{y.ToString("F2", CultureInfo.InvariantCulture)}, w:{width.ToString("F2", CultureInfo.InvariantCulture)}, h:{height.ToString("F2", CultureInfo.InvariantCulture)})";
    }

    public struct Color
    {
        public float r, g, b, a;
        public Color(float r, float g, float b, float a = 1f) { this.r = r; this.g = g; this.b = b; this.a = a; }
        public override string ToString() => $"RGBA({r.ToString("F3", CultureInfo.InvariantCulture)}, {g.ToString("F3", CultureInfo.InvariantCulture)}, {b.ToString("F3", CultureInfo.InvariantCulture)}, {a.ToString("F3", CultureInfo.InvariantCulture)})";
    }

    public static class LayerMask
    {
        public static string LayerToName(int layer) => layer == 0 ? "Default" : $"Layer{layer}";
    }

    public class Transform : Component, System.Collections.IEnumerable
    {
        private readonly List<Transform> _children = new();
        public Transform? parent { get; set; }
        public int childCount => _children.Count;
        public Vector3 position { get; set; }
        public Vector3 localPosition { get; set; }
        public Vector3 eulerAngles { get; set; }
        public Vector3 localEulerAngles { get; set; }
        public Vector3 localScale { get; set; } = new Vector3(1, 1, 1);

        public Transform GetChild(int index) => _children[index];
        public void AddChild(Transform child)
        {
            child.parent = this;
            _children.Add(child);
        }
        public System.Collections.IEnumerator GetEnumerator() => _children.GetEnumerator();
    }

    public class Component : Object
    {
        public GameObject gameObject { get; internal set; } = null!;
        public Transform transform => gameObject.transform;
    }

    public class Behaviour : Component
    {
        public bool enabled { get; set; } = true;
    }

    public class MonoBehaviour : Behaviour { }
    public class Collider : Component { public bool enabled { get; set; } = true; }
    public class Renderer : Component { public bool enabled { get; set; } = true; }

    public class Camera : Behaviour
    {
        public static Camera? main;
        public static readonly List<Camera> AllCamerasList = new();
        public static Camera[] allCameras => AllCamerasList.ToArray();
        public static int allCamerasCount => AllCamerasList.Count;

        public bool orthographic { get; set; }
        public float orthographicSize { get; set; } = 5f;
        public float fieldOfView { get; set; } = 60f;
        public float nearClipPlane { get; set; } = 0.3f;
        public float farClipPlane { get; set; } = 1000f;
        public Rect rect { get; set; } = new Rect(0, 0, 1, 1);
        public int pixelWidth { get; set; } = 1920;
        public int pixelHeight { get; set; } = 1080;
        public string clearFlags { get; set; } = "Skybox";
        public Color backgroundColor { get; set; } = new Color(0.19f, 0.30f, 0.47f);
        public int cullingMask { get; set; } = -1;
        public float depth { get; set; } = 0f;
        public string renderingPath { get; set; } = "UsePlayerSettings";
    }

    public class GameObject : Object
    {
        private readonly List<Component?> _components = new();
        public bool activeSelf { get; set; } = true;
        public bool activeInHierarchy
        {
            get
            {
                if (!activeSelf) return false;
                var cur = transform.parent;
                var seen = new HashSet<Transform>();
                while (cur != null && seen.Add(cur))
                {
                    if (cur.gameObject != null && !cur.gameObject.activeSelf) return false;
                    cur = cur.parent;
                }
                return true;
            }
        }
        public string tag { get; set; } = "Untagged";
        public int layer { get; set; } = 0;
        public Transform transform { get; }

        public GameObject(string name = "")
        {
            this.name = name;
            transform = new Transform { gameObject = this, name = name };
            _components.Add(transform);
        }

        public T AddComponent<T>() where T : Component, new()
        {
            var comp = new T { gameObject = this, name = typeof(T).Name };
            _components.Add(comp);
            return comp;
        }

        public void AddRawComponent(Component? comp)
        {
            if (comp != null) comp.gameObject = this;
            _components.Add(comp);
        }

        public T? GetComponent<T>() where T : Component
        {
            foreach (var c in _components)
            {
                if (c is T t) return t;
            }
            return null;
        }

        public T[] GetComponents<T>() where T : class
        {
            var result = new List<T>();
            foreach (var c in _components)
            {
                if (c == null)
                {
                    if (typeof(T) == typeof(Component) || typeof(T) == typeof(object))
                    {
                        result.Add(null!);
                    }
                }
                else if (c is T t)
                {
                    result.Add(t);
                }
            }
            return result.ToArray();
        }
    }

    public static class Debug
    {
        public static void LogWarning(object? message) { }
    }
}

namespace UnityEngine.SceneManagement
{
    public struct Scene
    {
        public string name { get; set; }
        public string path { get; set; }
        public bool isLoaded { get; set; }
        public bool isDirty { get; set; }
        public int rootCount => RootObjects != null ? RootObjects.Count : 0;
        public List<GameObject> RootObjects { get; set; }

        public GameObject[] GetRootGameObjects() => RootObjects != null ? RootObjects.ToArray() : Array.Empty<GameObject>();
    }

    public static class SceneManager
    {
        public static readonly List<Scene> Scenes = new();
        public static int sceneCount => Scenes.Count;
        public static Scene GetActiveScene() => Scenes.Count > 0 ? Scenes[0] : default;
        public static Scene GetSceneAt(int index) => Scenes[index];
    }
}

namespace UnityEditor
{
    public class InitializeOnLoadAttribute : System.Attribute { }
    public class InitializeOnLoadMethodAttribute : System.Attribute { }
    public static class SessionState
    {
        private static readonly Dictionary<string, string> Values = new();
        public static string? GetString(string key, string? fallback) => Values.TryGetValue(key, out var value) ? value : fallback;
        public static void SetString(string key, string value) => Values[key] = value;
        public static void EraseString(string key) => Values.Remove(key);
    }
    public class Editor { }
    public static class EditorApplication
    {
        public static bool isCompiling;
        public static event Action? update;
        public static void Tick() => update?.Invoke();
        public static string applicationContentsPath { get; set; } = string.Empty;
    }

    public enum SerializedPropertyType
    {
        Generic, Integer, Boolean, Float, String, Color, ObjectReference, LayerMask, Enum, Vector2, Vector3, Vector4, Rect, ArraySize, Character, AnimationCurve, Bounds, Gradient
    }

    public class SerializedProperty
    {
        public string name { get; set; } = string.Empty;
        public string displayName { get; set; } = string.Empty;
        public string propertyPath { get; set; } = string.Empty;
        public SerializedPropertyType propertyType { get; set; }
        public int intValue { get; set; }
        public bool boolValue { get; set; }
        public float floatValue { get; set; }
        public string stringValue { get; set; } = string.Empty;
        public UnityEngine.Object? objectReferenceValue { get; set; }
        public bool isArray { get; set; }
        public int arraySize { get; set; }

        private readonly List<SerializedProperty> _visibleProperties = new();
        private int _currentIndex = -1;

        public void AddChildProperty(SerializedProperty prop) => _visibleProperties.Add(prop);

        public bool NextVisible(bool enterChildren)
        {
            _currentIndex++;
            if (_currentIndex < _visibleProperties.Count)
            {
                var cur = _visibleProperties[_currentIndex];
                name = cur.name;
                displayName = cur.displayName;
                propertyPath = cur.propertyPath;
                propertyType = cur.propertyType;
                intValue = cur.intValue;
                boolValue = cur.boolValue;
                floatValue = cur.floatValue;
                stringValue = cur.stringValue;
                objectReferenceValue = cur.objectReferenceValue;
                isArray = cur.isArray;
                arraySize = cur.arraySize;
                return true;
            }
            return false;
        }
    }

    public class SerializedObject
    {
        public static readonly Dictionary<UnityEngine.Object, List<SerializedProperty>> CustomProperties = new();
        public UnityEngine.Object targetObject { get; }
        public readonly List<SerializedProperty> Properties = new();

        public SerializedObject(UnityEngine.Object target)
        {
            targetObject = target;
            if (target != null && CustomProperties.TryGetValue(target, out var props))
            {
                Properties.AddRange(props);
            }
        }

        public SerializedProperty GetIterator()
        {
            var iterator = new SerializedProperty();
            foreach (var p in Properties) iterator.AddChildProperty(p);
            return iterator;
        }
    }

    public static class Selection
    {
        public static UnityEngine.GameObject? activeGameObject { get; set; }
        public static UnityEngine.Object? activeObject { get; set; }
        public static UnityEngine.Object[] objects { get; set; } = Array.Empty<UnityEngine.Object>();
        public static UnityEngine.GameObject[] gameObjects { get; set; } = Array.Empty<UnityEngine.GameObject>();
    }

    public static class AssetDatabase
    {
        public static Func<string, string[]>? FindAssetsFunc;
        public static Func<string, string>? GUIDToAssetPathFunc;
        public static Func<string, Type>? GetMainAssetTypeAtPathFunc;
        public static Func<UnityEngine.Object, string>? GetAssetPathFunc;

        public static string[] FindAssets(string filter) => FindAssetsFunc?.Invoke(filter) ?? Array.Empty<string>();
        public static string GUIDToAssetPath(string guid) => GUIDToAssetPathFunc?.Invoke(guid) ?? string.Empty;
        public static Type GetMainAssetTypeAtPath(string path) => GetMainAssetTypeAtPathFunc?.Invoke(path) ?? typeof(UnityEngine.Object);
        public static string GetAssetPath(UnityEngine.Object obj) => GetAssetPathFunc?.Invoke(obj) ?? string.Empty;
        public static UnityEngine.Object? LoadMainAssetAtPath(string path) => new UnityEngine.Object { name = System.IO.Path.GetFileNameWithoutExtension(path) };
        public static string AssetPathToGUID(string path) => "test_guid_" + Math.Abs(path.GetHashCode()).ToString("x");
    }

    namespace Compilation
    {
        public enum AssembliesType
        {
            Editor,
            Player,
            PlayerWithoutTestAssemblies
        }

        public class Assembly
        {
            public string name { get; set; } = string.Empty;
            public string outputPath { get; set; } = string.Empty;
            public string[] compiledAssemblyReferences { get; set; } = System.Array.Empty<string>();
            public string[] allReferences { get; set; } = System.Array.Empty<string>();
        }

        public static class CompilationPipeline
        {
            public enum PrecompiledAssemblySources
            {
                UserAssembly = 1,
                UnityEngine = 2,
                UnityEditor = 4,
                SystemAssembly = 8,
                UnityAssembly = 16,
                All = -1
            }

            public static Assembly[] GetAssemblies(AssembliesType assembliesType) => System.Array.Empty<Assembly>();
            public static string[] GetPrecompiledAssemblyPaths(PrecompiledAssemblySources sources) => System.Array.Empty<string>();
            public static string? GetPrecompiledAssemblyPathFromAssemblyName(string assemblyName) => null;
        }
    }
}

namespace UnityLeanMcp
{
    public static class UnityResultFormatter
    {
        public const int DefaultMaxOutputCharacters = 64 * 1024;
        public const int DefaultMaxOutputBytes = 64 * 1024;
        public static string FormatSerializedPropertyValue(UnityEditor.SerializedProperty prop)
        {
            return prop.propertyType switch
            {
                UnityEditor.SerializedPropertyType.Integer => prop.intValue.ToString(),
                UnityEditor.SerializedPropertyType.Boolean => prop.boolValue ? "true" : "false",
                UnityEditor.SerializedPropertyType.Float => prop.floatValue.ToString(CultureInfo.InvariantCulture),
                UnityEditor.SerializedPropertyType.String => $"\"{prop.stringValue}\"",
                _ => prop.propertyType.ToString()
            };
        }
    }

    internal static class UnityLeanMcpPaths
    {
        public static string GetResultFilePath(string kind, string id) => System.IO.Path.Combine(ProjectRoot, kind + "_" + id + ".json");
        public static string GetRefreshResultFile(string id) => GetResultFilePath("refresh", id);
        public static string TestResultsFile => System.IO.Path.Combine(ProjectRoot, "tests.json");
        public static string RefreshResultFile => System.IO.Path.Combine(ProjectRoot, "refresh.json");
        public static string LogFile => System.IO.Path.Combine(ProjectRoot, "Temp", "unity_lean_mcp_worker.log");
        public static string ProjectRoot { get; set; } = System.IO.Directory.GetCurrentDirectory();
    }

    internal static class OperationKinds
    {
        public const string Eval = "eval", Coverage = "coverage", Test = "test", Refresh = "refresh", Recompile = "recompile";
    }

    internal static class RunTestsHandler
    {
        public static bool ActiveStateKnown = true;
        public static bool Active = false;
        public static bool TryGetTestRunnerActiveState(out bool active) { active = Active; return ActiveStateKnown; }
        public static void CleanupTestRun(string id) { }
        public static void RestoreCoverage(string id) { }
    }

    internal static class UnityLeanMcpOperationStore
    {
        public static BeginOperationResult TryBegin(string id, string kind, string status, out UnityLeanMcpOperationState? existing)
        {
            var result = UnityCommandGate.TryBegin(kind, id, status, out _);
            var snapshot = UnityCommandGate.ReadSnapshot();
            existing = snapshot == null ? null : new UnityLeanMcpOperationState { kind = snapshot.Kind, operationId = snapshot.OperationId };
            return (BeginOperationResult)result;
        }
        public static void Update(string id, string status) => UnityCommandGate.Update(id, status);
        public static Action<string, string, string>? Write;
        public static void WriteAtomic(string path, string json, string id, int attempts = 5) => Write!(path, json, id);
        public static bool TryWriteStaticHistory(string path, string json, string id) => true;
    }

    internal enum BeginOperationResult { Started, AlreadyStarted, Busy, Invalid }
    internal sealed class UnityLeanMcpOperationState { public string kind = ""; public string operationId = ""; }
    internal enum CommandExecutionTarget { EditModeOnly }
    internal interface ICommandHandler { void Handle(string payload, System.IO.StreamWriter writer); }
    internal static class OperationStatus { public const string Requested = "Requested", Refreshing = "Refreshing", Recompiling = "Recompiling"; }
    internal sealed class UnityRefreshResult { public string operationId = ""; public bool success; public string message = ""; }
    internal static class UnityLeanMcpCompilationTracker
    {
        public static bool RefreshPending, CompilationRequested;
        public static void ClearCapturedDiagnostics() { }
        public static void ObserveOperationUntilSettled() { }
    }
}
