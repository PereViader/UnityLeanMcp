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

    public enum HideFlags
    {
        None = 0,
        HideAndDontSave = 61
    }

    public class Object
    {
        public string name { get; set; } = string.Empty;
        public HideFlags hideFlags { get; set; }
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

        public static void DestroyImmediate(Object? obj, bool allowDestroyingAssets) => DestroyImmediate(obj);
        public static void Destroy(Object? obj) => DestroyImmediate(obj);

        public static Func<Type, object[]>? FindObjectsOfTypeFunc;

        public static T? FindObjectOfType<T>() where T : Object
        {
            var arr = FindObjectsOfType<T>();
            return arr.Length > 0 ? arr[0] : null;
        }

        public static T? FindFirstObjectByType<T>() where T : Object
        {
            return FindObjectOfType<T>();
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

    [AttributeUsage(AttributeTargets.Field)]
    public sealed class SerializeField : Attribute { }

    public struct Vector2
    {
        public float x, y;
        public Vector2(float x, float y) { this.x = x; this.y = y; }
        public override string ToString() => $"({x.ToString("F2", CultureInfo.InvariantCulture)}, {y.ToString("F2", CultureInfo.InvariantCulture)})";
        public string ToString(string format, IFormatProvider provider) => $"({x.ToString(format, provider)}, {y.ToString(format, provider)})";
    }

    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public override string ToString() => $"({x.ToString("F2", CultureInfo.InvariantCulture)}, {y.ToString("F2", CultureInfo.InvariantCulture)}, {z.ToString("F2", CultureInfo.InvariantCulture)})";
        public string ToString(string format, IFormatProvider provider) => $"({x.ToString(format, provider)}, {y.ToString(format, provider)}, {z.ToString(format, provider)})";
    }

    public struct Vector4
    {
        public float x, y, z, w;
        public Vector4(float x, float y, float z, float w) { this.x = x; this.y = y; this.z = z; this.w = w; }
        public override string ToString() => $"({x.ToString("F2", CultureInfo.InvariantCulture)}, {y.ToString("F2", CultureInfo.InvariantCulture)}, {z.ToString("F2", CultureInfo.InvariantCulture)}, {w.ToString("F2", CultureInfo.InvariantCulture)})";
        public string ToString(string format, IFormatProvider provider) => $"({x.ToString(format, provider)}, {y.ToString(format, provider)}, {z.ToString(format, provider)}, {w.ToString(format, provider)})";
    }

    public struct Vector2Int
    {
        public int x, y;
        public Vector2Int(int x, int y) { this.x = x; this.y = y; }
        public override string ToString() => $"({x}, {y})";
    }

    public struct Vector3Int
    {
        public int x, y, z;
        public Vector3Int(int x, int y, int z) { this.x = x; this.y = y; this.z = z; }
        public override string ToString() => $"({x}, {y}, {z})";
    }

    public struct Quaternion
    {
        public float x, y, z, w;
        public Quaternion(float x, float y, float z, float w) { this.x = x; this.y = y; this.z = z; this.w = w; }
        public override string ToString() => $"({x.ToString("F2", CultureInfo.InvariantCulture)}, {y.ToString("F2", CultureInfo.InvariantCulture)}, {z.ToString("F2", CultureInfo.InvariantCulture)}, {w.ToString("F2", CultureInfo.InvariantCulture)})";
        public string ToString(string format, IFormatProvider provider) => $"({x.ToString(format, provider)}, {y.ToString(format, provider)}, {z.ToString(format, provider)}, {w.ToString(format, provider)})";
    }

    public struct Rect
    {
        public float x, y, width, height;
        public Rect(float x, float y, float width, float height) { this.x = x; this.y = y; this.width = width; this.height = height; }
        public override string ToString() => $"(x:{x.ToString("F2", CultureInfo.InvariantCulture)}, y:{y.ToString("F2", CultureInfo.InvariantCulture)}, w:{width.ToString("F2", CultureInfo.InvariantCulture)}, h:{height.ToString("F2", CultureInfo.InvariantCulture)})";
        public string ToString(string format, IFormatProvider provider) => $"(x:{x.ToString(format, provider)}, y:{y.ToString(format, provider)}, width:{width.ToString(format, provider)}, height:{height.ToString(format, provider)})";
    }

    public struct RectInt
    {
        public int x, y, width, height;
        public RectInt(int x, int y, int width, int height) { this.x = x; this.y = y; this.width = width; this.height = height; }
        public override string ToString() => $"(x:{x}, y:{y}, width:{width}, height:{height})";
    }

    public struct Color
    {
        public float r, g, b, a;
        public Color(float r, float g, float b, float a = 1f) { this.r = r; this.g = g; this.b = b; this.a = a; }
        public override string ToString() => $"RGBA({r.ToString("F3", CultureInfo.InvariantCulture)}, {g.ToString("F3", CultureInfo.InvariantCulture)}, {b.ToString("F3", CultureInfo.InvariantCulture)}, {a.ToString("F3", CultureInfo.InvariantCulture)})";
        public string ToString(string format, IFormatProvider provider) => $"RGBA({r.ToString(format, provider)}, {g.ToString(format, provider)}, {b.ToString(format, provider)}, {a.ToString(format, provider)})";
    }

    public struct Color32
    {
        public byte r, g, b, a;
        public Color32(byte r, byte g, byte b, byte a) { this.r = r; this.g = g; this.b = b; this.a = a; }
    }

    public struct Bounds
    {
        public Vector3 center, extents;
        public Bounds(Vector3 center, Vector3 size)
        {
            this.center = center;
            this.extents = new Vector3(size.x * 0.5f, size.y * 0.5f, size.z * 0.5f);
        }
    }

    public struct BoundsInt
    {
        public Vector3Int position, size;
        public BoundsInt(Vector3Int position, Vector3Int size) { this.position = position; this.size = size; }
    }

    public struct Ray
    {
        public Vector3 origin, direction;
        public Ray(Vector3 origin, Vector3 direction) { this.origin = origin; this.direction = direction; }
    }

    public struct Ray2D
    {
        public Vector2 origin, direction;
        public Ray2D(Vector2 origin, Vector2 direction) { this.origin = origin; this.direction = direction; }
    }

    public struct Plane
    {
        public Vector3 normal;
        public float distance;
        public Plane(Vector3 normal, float distance) { this.normal = normal; this.distance = distance; }
    }

    public static class LayerMask
    {
        public static string LayerToName(int layer) => layer == 0 ? "Default" : $"Layer{layer}";
    }

    public class ScriptableObject : Object
    {
        public static T CreateInstance<T>() where T : ScriptableObject, new() => new T();
    }

    public static class Resources
    {
        public static T[] FindObjectsOfTypeAll<T>() where T : Object => Array.Empty<T>();
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
    public class AudioSource : Behaviour { }
    public class Rigidbody : Component { }
    public class Material : Object { }

    public static class Mathf
    {
        public static int Clamp(int value, int min, int max) => Math.Max(min, Math.Min(max, value));
        public static float Clamp(float value, float min, float max) => Math.Max(min, Math.Min(max, value));
    }

    public class Texture : Object { }
    public enum TextureFormat { RGB24, RGBA32 }
    public class Texture2D : Texture
    {
        public int width { get; set; }
        public int height { get; set; }
        public Texture2D(int width, int height, TextureFormat format = TextureFormat.RGBA32, bool mipChain = false)
        {
            this.width = width;
            this.height = height;
        }
        public void ReadPixels(Rect source, int destX, int destY, bool recalculateMipMaps = true) { }
        public void Apply(bool updateMipmaps = true, bool makeNoLongerReadable = false) { }
        public byte[] EncodeToPNG() => new byte[128];
    }

    public enum RenderTextureFormat { Default }
    public enum RenderTextureReadWrite { Default }
    public class RenderTexture : Texture
    {
        public static RenderTexture? active { get; set; }
        public int width { get; set; }
        public int height { get; set; }
        public static RenderTexture GetTemporary(int width, int height, int depthBuffer = 0, RenderTextureFormat format = RenderTextureFormat.Default, RenderTextureReadWrite readWrite = RenderTextureReadWrite.Default)
        {
            return new RenderTexture { width = width, height = height };
        }
        public static void ReleaseTemporary(RenderTexture temp) { }
    }

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
        public RenderTexture? targetTexture { get; set; }
        public void Render() { }
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

    public enum LogType
    {
        Error,
        Assert,
        Warning,
        Log,
        Exception
    }

    public static class Application
    {
        public static string unityVersion { get; set; } = "6000.7.0f1";
        public static bool isPlaying { get; set; } = false;
        public delegate void LogCallback(string condition, string stackTrace, LogType type);
        public static event LogCallback? logMessageReceivedThreaded { add { } remove { } }
    }

    public static class Debug
    {
        public static void Log(object? message) { }
        public static void LogError(object? message) { }
        public static void LogWarning(object? message) { }
        public static void LogException(Exception? exception) { }
    }
}

namespace UnityEngine.Rendering
{
    public static class GraphicsSettings
    {
        public static RenderPipelineAsset? currentRenderPipeline { get; set; }
    }

    public class RenderPipelineAsset : Object { }

    public static class RenderPipeline
    {
        public class StandardRequest
        {
            public RenderTexture? destination;
        }

        public static bool SupportsRenderRequest(Camera camera, StandardRequest data) => false;
        public static void SubmitRenderRequest(Camera camera, StandardRequest data) { }
    }
}

namespace UnityEngine.TestTools
{
    public struct CoveredSequencePoint
    {
        public System.Reflection.MethodBase? method;
        public uint ilOffset;
        public uint hitCount;
        public string filename;
        public uint line;
        public uint column;
    }

    public static class Coverage
    {
        public static bool enabled { get; set; }
        public static Func<System.Reflection.MethodBase, CoveredSequencePoint[]>? GetSequencePointsForFunc;
        public static CoveredSequencePoint[] GetSequencePointsFor(System.Reflection.MethodBase method) =>
            GetSequencePointsForFunc?.Invoke(method) ?? Array.Empty<CoveredSequencePoint>();
        public static void ResetAll() { }
    }
}

namespace UnityEngine.SceneManagement
{
    public struct Scene
    {
        public int handle { get; set; }
        public bool IsValid() => !string.IsNullOrEmpty(name);
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
    public static class AssemblyReloadEvents
    {
        public delegate void AssemblyReloadCallback();
        public static event AssemblyReloadCallback? beforeAssemblyReload { add { } remove { } }
    }
    public static class EditorUtility
    {
        public static bool scriptCompilationFailed;
    }
    public static class EditorSettings
    {
        public static bool prefabModeAllowAutoSave { get; set; } = true;
    }
    public static class SessionState
    {
        private static readonly Dictionary<string, string> Values = new();
        private static readonly Dictionary<string, int> IntValues = new();
        public static int SetStringCount { get; set; }
        public static string? GetString(string key, string? fallback) => Values.TryGetValue(key, out var value) ? value : fallback;
        public static void SetString(string key, string value)
        {
            SetStringCount++;
            Values[key] = value;
        }
        public static void EraseString(string key) => Values.Remove(key);
        public static int GetInt(string key, int fallback) => IntValues.TryGetValue(key, out var value) ? value : fallback;
        public static void SetInt(string key, int value) => IntValues[key] = value;
        public static void EraseInt(string key) => IntValues.Remove(key);
    }
    public class Editor { }
    public class SceneView
    {
        public static SceneView? lastActiveSceneView { get; set; }
        public static readonly List<SceneView> SceneViewsList = new();
        public static System.Collections.ArrayList sceneViews => new System.Collections.ArrayList(SceneViewsList);
        public UnityEngine.Camera? camera { get; set; }
    }
    public static class EditorApplication
    {
        public static bool isCompiling;
        public static bool isUpdating;
        public static event Action? update;
        public static event Action? projectChanged { add { } remove { } }
        public static event Action? quitting { add { } remove { } }
        public static void Tick() => update?.Invoke();
        public static void QueuePlayerLoopUpdate() { }
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
        public UnityEngine.Color colorValue { get; set; }
        public string[]? enumNames { get; set; }
        public int enumValueIndex { get; set; }
        public UnityEngine.Vector2 vector2Value { get; set; }
        public UnityEngine.Vector3 vector3Value { get; set; }
        public UnityEngine.Vector4 vector4Value { get; set; }
        public UnityEngine.Rect rectValue { get; set; }
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
                colorValue = cur.colorValue;
                enumNames = cur.enumNames;
                enumValueIndex = cur.enumValueIndex;
                vector2Value = cur.vector2Value;
                vector3Value = cur.vector3Value;
                vector4Value = cur.vector4Value;
                rectValue = cur.rectValue;
                objectReferenceValue = cur.objectReferenceValue;
                isArray = cur.isArray;
                arraySize = cur.arraySize;
                return true;
            }
            return false;
        }
    }

    public class SerializedObject : IDisposable
    {
        public static readonly Dictionary<UnityEngine.Object, List<SerializedProperty>> CustomProperties = new();
        public static int DisposeCount { get; set; }
        public UnityEngine.Object targetObject { get; }
        public bool IsDisposed { get; private set; }
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

        public void Dispose()
        {
            IsDisposed = true;
            DisposeCount++;
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
        public static Func<string, string[], string[]>? FindAssetsInFoldersFunc;
        public static Func<string, string>? GUIDToAssetPathFunc;
        public static Func<string, Type>? GetMainAssetTypeAtPathFunc;
        public static Func<UnityEngine.Object, string>? GetAssetPathFunc;
        public static Func<string, UnityEngine.Object?>? LoadMainAssetAtPathFunc;
        public static Func<string, string>? AssetPathToGUIDFunc;
        public static Func<string, bool, string[]>? GetDependenciesFunc;
        public static Func<string, bool>? IsValidFolderFunc;

        public static string[] FindAssets(string filter) => FindAssetsFunc?.Invoke(filter) ?? Array.Empty<string>();
        public static string[] FindAssets(string filter, string[] searchInFolders) =>
            FindAssetsInFoldersFunc != null ? FindAssetsInFoldersFunc(filter, searchInFolders) : (FindAssetsFunc?.Invoke(filter) ?? Array.Empty<string>());
        public static string GUIDToAssetPath(string guid) => GUIDToAssetPathFunc?.Invoke(guid) ?? string.Empty;
        public static Type GetMainAssetTypeAtPath(string path) => GetMainAssetTypeAtPathFunc?.Invoke(path) ?? typeof(UnityEngine.Object);
        public static string GetAssetPath(UnityEngine.Object obj) => GetAssetPathFunc?.Invoke(obj) ?? string.Empty;
        public static UnityEngine.Object? LoadMainAssetAtPath(string path) =>
            LoadMainAssetAtPathFunc != null ? LoadMainAssetAtPathFunc(path) : new UnityEngine.Object { name = System.IO.Path.GetFileNameWithoutExtension(path) };
        public static string AssetPathToGUID(string path) =>
            AssetPathToGUIDFunc != null ? AssetPathToGUIDFunc(path) : "test_guid_" + Math.Abs(path.GetHashCode()).ToString("x");
        public static string[] GetDependencies(string pathName, bool recursive = true) =>
            GetDependenciesFunc?.Invoke(pathName, recursive) ?? Array.Empty<string>();
        public static string[] GetDependencies(string pathName) =>
            GetDependencies(pathName, true);
        public static bool IsValidFolder(string path) =>
            IsValidFolderFunc?.Invoke(path) ?? false;
    }

    public class PropertyModification
    {
        public UnityEngine.Object? target { get; set; }
        public string propertyPath { get; set; } = string.Empty;
        public string value { get; set; } = string.Empty;
        public UnityEngine.Object? objectReference { get; set; }
    }

    namespace SceneManagement
    {
        public class AddedComponent
        {
            public UnityEngine.Component? instanceComponent { get; set; }
        }

        public class RemovedComponent
        {
            public UnityEngine.Component? assetComponent { get; set; }
        }

        public class PrefabStage
        {
            public enum Mode
            {
                InIsolation = 0,
                InContext = 1
            }

            public string assetPath { get; set; } = string.Empty;
            public string prefabAssetPath { get; set; } = string.Empty;
            public UnityEngine.GameObject? prefabContentsRoot { get; set; }
            public UnityEngine.SceneManagement.Scene scene { get; set; }
            public Mode mode { get; set; } = Mode.InIsolation;
            public bool autoSave { get; set; } = true;
        }

        public static class PrefabStageUtility
        {
            public static Func<PrefabStage?>? GetCurrentPrefabStageFunc;
            public static PrefabStage? GetCurrentPrefabStage() => GetCurrentPrefabStageFunc?.Invoke();
        }
    }

    public static class PrefabUtility
    {
        public static Func<UnityEngine.Object, bool>? IsPartOfAnyPrefabInstanceFunc;
        public static Func<UnityEngine.Object, UnityEngine.Object?>? GetCorrespondingObjectFromSourceFunc;
        public static Func<UnityEngine.Object, PropertyModification[]>? GetPropertyModificationsFunc;
        public static Func<UnityEngine.GameObject, List<SceneManagement.AddedComponent>>? GetAddedComponentsFunc;
        public static Func<UnityEngine.GameObject, List<SceneManagement.RemovedComponent>>? GetRemovedComponentsFunc;

        public static bool IsPartOfPrefabInstance(UnityEngine.Object target) =>
            IsPartOfAnyPrefabInstanceFunc?.Invoke(target) ?? false;

        public static bool IsPartOfAnyPrefabInstance(UnityEngine.Object target) =>
            IsPartOfAnyPrefabInstanceFunc?.Invoke(target) ?? false;

        public static T? GetCorrespondingObjectFromSource<T>(T target) where T : UnityEngine.Object =>
            GetCorrespondingObjectFromSourceFunc?.Invoke(target) as T;

        public static PropertyModification[] GetPropertyModifications(UnityEngine.Object target) =>
            GetPropertyModificationsFunc?.Invoke(target) ?? Array.Empty<PropertyModification>();

        public static List<SceneManagement.AddedComponent> GetAddedComponents(UnityEngine.GameObject instance) =>
            GetAddedComponentsFunc?.Invoke(instance) ?? new List<SceneManagement.AddedComponent>();

        public static List<SceneManagement.RemovedComponent> GetRemovedComponents(UnityEngine.GameObject instance) =>
            GetRemovedComponentsFunc?.Invoke(instance) ?? new List<SceneManagement.RemovedComponent>();
    }

    namespace Compilation
    {
        public enum AssembliesType
        {
            Editor,
            Player,
            PlayerWithoutTestAssemblies
        }

        public enum CompilerMessageType
        {
            Error,
            Warning,
            Info
        }

        public struct CompilerMessage
        {
            public string message;
            public string file;
            public int line;
            public int column;
            public CompilerMessageType type;
        }

        public enum CodeOptimization
        {
            None,
            Debug,
            Release
        }

        public class Assembly
        {
            public string name { get; set; } = string.Empty;
            public string outputPath { get; set; } = string.Empty;
            public string[] sourceFiles { get; set; } = System.Array.Empty<string>();
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

            public static CodeOptimization codeOptimization { get; set; } = CodeOptimization.Debug;
            public static Func<Assembly[]>? GetAssembliesFunc;

            public static event Action<object>? compilationStarted { add { } remove { } }
            public static event Action<object>? compilationFinished { add { } remove { } }
            public static event Action<string, CompilerMessage[]>? assemblyCompilationFinished { add { } remove { } }

            public static Assembly[] GetAssemblies() => GetAssembliesFunc?.Invoke() ?? System.Array.Empty<Assembly>();
            public static Assembly[] GetAssemblies(AssembliesType assembliesType) => GetAssembliesFunc?.Invoke() ?? System.Array.Empty<Assembly>();
            public static string[] GetPrecompiledAssemblyPaths(PrecompiledAssemblySources sources) => System.Array.Empty<string>();
            public static string? GetPrecompiledAssemblyPathFromAssemblyName(string assemblyName) => null;
        }
    }

    namespace TestTools.TestRunner.Api
    {
        public enum TestMode
        {
            EditMode = 1,
            PlayMode = 2
        }

        public enum TestStatus
        {
            Inconclusive,
            Skipped,
            Passed,
            Failed
        }

        public class Filter
        {
            public TestMode testMode;
            public string[]? testNames;
            public string[]? groupNames;
            public string[]? categoryNames;
            public string[]? assemblyNames;
        }

        public class ExecutionSettings
        {
            public Filter[] filters;
            public ExecutionSettings(params Filter[] filters) { this.filters = filters; }
        }

        public interface ITestAdaptor
        {
            string Name { get; }
            string FullName { get; }
            int TestCaseCount { get; }
            bool HasChildren { get; }
        }

        public interface ITestResultAdaptor
        {
            ITestAdaptor? Test { get; }
            string Name { get; }
            string FullName { get; }
            string ResultState { get; }
            TestStatus TestStatus { get; }
            double Duration { get; }
            string Message { get; }
            string StackTrace { get; }
            int FailCount { get; }
            int PassCount { get; }
            int SkipCount { get; }
            bool HasChildren { get; }
            IEnumerable<ITestResultAdaptor>? Children { get; }
        }

        public interface ICallbacks
        {
            void RunStarted(ITestAdaptor testsToRun);
            void RunFinished(ITestResultAdaptor result);
            void TestStarted(ITestAdaptor test);
            void TestFinished(ITestResultAdaptor result);
        }

        public interface IErrorCallbacks : ICallbacks
        {
            void OnError(string message);
        }

        public class TestRunnerApi : UnityEngine.ScriptableObject
        {
            public static bool ActiveStateKnown = true;
            public static bool Active = false;
            public static Action<ExecutionSettings>? OnExecute;
            public static Action<string>? OnCancelTestRun;

            public void RegisterCallbacks<T>(T callbacks) where T : ICallbacks { }
            public string Execute(ExecutionSettings settings)
            {
                OnExecute?.Invoke(settings);
                return Guid.NewGuid().ToString("N");
            }
            public static void CancelTestRun(string guid) => OnCancelTestRun?.Invoke(guid);
            internal static object? IsRunActive() => ActiveStateKnown ? (object)Active : null;
        }
    }
}

namespace UnityLeanMcp
{
    internal static class ProtocolCodec
    {
        public static string EscapeLine(string? text) => UnityLeanMcp.Mcp.ProtocolCodec.EscapeLine(text) ?? string.Empty;
        public static string UnescapeLine(string? text) => UnityLeanMcp.Mcp.ProtocolCodec.UnescapeLine(text) ?? string.Empty;
    }

    internal static class CommandHelper
    {
        public static Type? FindType(string name) => null;
        public static bool IsAssetImportWorkerProcess() => false;
        public static string ReadFileWithRetry(string path, int maxRetries = 3, int delayMs = 10) =>
            System.IO.File.Exists(path) ? System.IO.File.ReadAllText(path) : string.Empty;
        public static bool DeleteFileWithRetry(string path, int maxRetries = 3, int delayMs = 10)
        {
            if (System.IO.File.Exists(path))
            {
                System.IO.File.Delete(path);
                return true;
            }
            return false;
        }
    }

    internal static class UnityLeanMcpServer
    {
        public static bool IsPlayModeActive { get; internal set; }
        public static bool IsPlayModeChanging { get; internal set; }
    }

    [Serializable]
    public class ConsoleLogEntry
    {
        public string type = "";
        public string logType = "";
        public string message = "";
        public string stackTrace = "";
    }

    internal static class OperationExecutionEngine
    {
        public static System.Threading.CancellationTokenSource RegisterActiveOperation(string operationId, bool isCancelable) =>
            new System.Threading.CancellationTokenSource();

        public static bool TryCancel(string operationId) => false;

        public static void FinishOperation(
            string operationId,
            string operationKind,
            string resultFilePath,
            bool success,
            string message,
            double duration,
            string? payload,
            List<ConsoleLogEntry>? logs = null,
            bool interrupted = false)
        {
            if (!UnityCommandGate.IsOwnedBy(operationId, operationKind))
            {
                return;
            }

            var runResult = new UnityOperationResult
            {
                operationId = operationId,
                success = success,
                interrupted = interrupted,
                message = message,
                resultState = interrupted ? OperationStatus.Interrupted : (success ? OperationStatus.Completed : "Failed")
            };
            UnityCommandGate.PublishResult(operationKind, operationId, resultFilePath, UnityEngine.JsonUtility.ToJson(runResult, true));
        }

        public static void Execute(
            string operationId,
            string operationKind,
            string resultFilePath,
            bool isVoid,
            Func<System.Threading.CancellationToken, object> invoker,
            bool canCancel = true)
        {
        }

        public static void MarkInterrupted(string operationKind, string resultFilePath, string message, string? targetOperationId = null)
        {
        }
    }

    internal static class UnityLeanMcpPaths
    {
        public static void EnsureInitialized() { }
        public static string TempDir => System.IO.Path.Combine(ProjectRoot, "Temp");
        public static string GetResultFilePath(string kind, string id) => System.IO.Path.Combine(ProjectRoot, kind + "_" + id + ".json");
        public static string GetTestResultsFile(string id) => GetResultFilePath("test", id);
        public static string GetEvalResultFile(string id) => GetResultFilePath("eval", id);
        public static string GetRefreshResultFile(string id) => GetResultFilePath("refresh", id);
        public static string TestResultsFile => System.IO.Path.Combine(ProjectRoot, "tests.json");
        public static string TestRunningFile => System.IO.Path.Combine(ProjectRoot, "test_running.json");
        public static string TestCancellationFile => System.IO.Path.Combine(ProjectRoot, "test_cancel.txt");
        public static string RefreshResultFile => System.IO.Path.Combine(ProjectRoot, "refresh.json");
        public static string DiagnosticsFile => System.IO.Path.Combine(ProjectRoot, "Temp", "unity_compile_diagnostics.txt");
        public static string LogFile => System.IO.Path.Combine(ProjectRoot, "Temp", "unity_lean_mcp_worker.log");
        public static string ProjectRoot { get; set; } = System.IO.Directory.GetCurrentDirectory();
    }

    internal static class OperationKinds
    {
        public const string Eval = "eval", Test = "test", Refresh = "refresh", Recompile = "recompile";
    }

    internal sealed class CancelledRunIdSet
    {
        public void Add(string id)
        {
            if (!string.IsNullOrEmpty(id))
            {
                WorkerThreadSnapshots.TryWriteTestCancellationRequest(UnityLeanMcpPaths.TestCancellationFile, id);
            }
        }

        public void Clear()
        {
            try
            {
                if (System.IO.File.Exists(UnityLeanMcpPaths.TestCancellationFile))
                {
                    System.IO.File.Delete(UnityLeanMcpPaths.TestCancellationFile);
                }
            }
            catch { }
        }
    }

    internal partial class RunTestsHandler
    {
        public static bool ActiveStateKnown
        {
            get => UnityEditor.TestTools.TestRunner.Api.TestRunnerApi.ActiveStateKnown;
            set => UnityEditor.TestTools.TestRunner.Api.TestRunnerApi.ActiveStateKnown = value;
        }
        public static bool Active
        {
            get => UnityEditor.TestTools.TestRunner.Api.TestRunnerApi.Active;
            set => UnityEditor.TestTools.TestRunner.Api.TestRunnerApi.Active = value;
        }
        public static readonly CancelledRunIdSet CancelledRunIds = new();
    }

    internal enum BeginOperationResult { Started, AlreadyStarted, Busy, Invalid }
    internal sealed class UnityLeanMcpOperationState { public string kind = ""; public string operationId = ""; public string status = ""; }
    internal enum CommandExecutionTarget { WorkerThread, MainThread, EditModeOnly }
    internal interface ICommandHandler
    {
        CommandExecutionTarget ExecutionTarget => CommandExecutionTarget.EditModeOnly;
        bool IsMutating => false;
        bool RequiresCompilationSettled => false;
        void Handle(string payload, System.IO.StreamWriter writer);
    }
    internal static class OperationStatus
    {
        public const string Queued = "Queued", Running = "Running", Executing = "Executing", Compiling = "Compiling",
            Refreshing = "Refreshing", Recompiling = "Recompiling", Cancelling = "Cancelling", Requested = "Requested",
            WaitingForUnity = "WaitingForUnity", Reloading = "Reloading", ShuttingDown = "ShuttingDown",
            Interrupted = "Interrupted", Cancelled = "Cancelled", Completed = "Completed";
    }
    internal sealed class UnityRefreshResult { public string operationId = ""; public bool success; public bool interrupted; public string message = ""; }
    internal sealed class UnityOperationResult
    {
        public string operationId = "";
        public string runId = "";
        public bool success;
        public bool interrupted;
        public string resultState = "";
        public string message = "";
    }

    [Serializable]
    public class FailedTestInfo
    {
        public string name = "";
        public string fullName = "";
        public string message = "";
        public string stackTrace = "";
        public double duration;
    }

    [Serializable]
    public class RunTestsArgs
    {
        public string mode = "";
        public string[]? testNames;
        public string[]? groupNames;
        public string[]? categoryNames;
        public string[]? assemblyNames;
        public bool failedOnly;
        public bool coverage;
    }

    [Serializable]
    public class UnityTestRunState
    {
        public string runId = "";
        public string mode = "";
        public string[]? testNames;
        public string[]? groupNames;
        public string[]? categoryNames;
        public string[]? assemblyNames;
        public string status = "";
        public string jobGuid = "";
        public string startedUtc = "";
        public int totalTests;
        public int completedTests;
        public int passCount;
        public int failCount;
        public int skipCount;
        public string currentTestName = "";
    }

    [Serializable]
    public class UnityTestRunResult
    {
        public string runId = "";
        public bool success;
        public int failCount;
        public int passCount;
        public int skipCount;
        public string message = "";
        public string resultState = "";
        public List<FailedTestInfo> failedTests = new();

        public string operationId => runId;
        public bool interrupted =>
            string.Equals(resultState, "Interrupted", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(resultState, "Cancelled", StringComparison.OrdinalIgnoreCase);
    }

    [Serializable]
    public class GetCoverageArgs
    {
        public string[]? paths;
    }

    [Serializable]
    public class UncoveredMethodSpan
    {
        public string method = "";
        public int startLine;
        public int endLine;
    }

    [Serializable]
    public class CoverageFileReport
    {
        public string path = "";
        public int totalPoints;
        public int coveredPoints;
        public int[] uncoveredLines = Array.Empty<int>();
        public List<UncoveredMethodSpan> uncoveredSpans = new();
    }

    [Serializable]
    public class CoverageResponsePayload
    {
        public List<CoverageFileReport> files = new();
    }
}
