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

            if (typeof(T) == typeof(UnityEngine.UIElements.UIDocument) || typeof(T).IsSubclassOf(typeof(UnityEngine.UIElements.UIDocument)))
            {
                var res = new List<T>();
                foreach (var d in UnityEngine.UIElements.UIDocument.AllUIDocuments)
                {
                    if (d is T t && !d._isDestroyed) res.Add(t);
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
        public static bool operator ==(Vector2 a, Vector2 b) => a.x == b.x && a.y == b.y;
        public static bool operator !=(Vector2 a, Vector2 b) => !(a == b);
        public override bool Equals(object? obj) => obj is Vector2 other && this == other;
        public override int GetHashCode() => HashCode.Combine(x, y);
    }

    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public static Vector3 zero => new Vector3(0f, 0f, 0f);
        public static Vector3 one => new Vector3(1f, 1f, 1f);
        public static Vector3 up => new Vector3(0f, 1f, 0f);
        public static Vector3 forward => new Vector3(0f, 0f, 1f);
        public float sqrMagnitude => x * x + y * y + z * z;
        public float magnitude => (float)Math.Sqrt(sqrMagnitude);
        public Vector3 normalized
        {
            get
            {
                float m = magnitude;
                return m > 1e-6f ? new Vector3(x / m, y / m, z / m) : zero;
            }
        }
        public static float Dot(Vector3 a, Vector3 b) => a.x * b.x + a.y * b.y + a.z * b.z;
        public static Vector3 operator +(Vector3 a, Vector3 b) => new Vector3(a.x + b.x, a.y + b.y, a.z + b.z);
        public static Vector3 operator -(Vector3 a, Vector3 b) => new Vector3(a.x - b.x, a.y - b.y, a.z - b.z);
        public static Vector3 operator -(Vector3 a) => new Vector3(-a.x, -a.y, -a.z);
        public static Vector3 operator *(Vector3 a, float d) => new Vector3(a.x * d, a.y * d, a.z * d);
        public static Vector3 operator *(float d, Vector3 a) => new Vector3(a.x * d, a.y * d, a.z * d);
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
        public Vector3 size
        {
            get => new Vector3(extents.x * 2f, extents.y * 2f, extents.z * 2f);
            set => extents = new Vector3(value.x * 0.5f, value.y * 0.5f, value.z * 0.5f);
        }
        public Vector3 min => center - extents;
        public Vector3 max => center + extents;
        public void Encapsulate(Vector3 point)
        {
            var curMin = min;
            var curMax = max;
            var newMin = new Vector3(Math.Min(curMin.x, point.x), Math.Min(curMin.y, point.y), Math.Min(curMin.z, point.z));
            var newMax = new Vector3(Math.Max(curMax.x, point.x), Math.Max(curMax.y, point.y), Math.Max(curMax.z, point.z));
            center = (newMin + newMax) * 0.5f;
            extents = (newMax - newMin) * 0.5f;
        }
        public void Encapsulate(Bounds bounds)
        {
            Encapsulate(bounds.min);
            Encapsulate(bounds.max);
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
        public static Func<Type, Object[]>? FindObjectsOfTypeAllFunc;
        public static T[] FindObjectsOfTypeAll<T>() where T : Object => Array.Empty<T>();
        public static Object[] FindObjectsOfTypeAll(Type type) => FindObjectsOfTypeAllFunc?.Invoke(type) ?? Array.Empty<Object>();
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
        public Vector3 forward { get; set; } = Vector3.forward;
        public Vector3? LastLookAtPosition { get; private set; }
        public Vector3? LastLookAtUp { get; private set; }

        public Vector3 TransformDirection(Vector3 direction)
        {
            float rx = eulerAngles.x * Mathf.Deg2Rad;
            float ry = eulerAngles.y * Mathf.Deg2Rad;
            float rz = eulerAngles.z * Mathf.Deg2Rad;

            float cz = (float)Math.Cos(rz), sz = (float)Math.Sin(rz);
            float x1 = direction.x * cz - direction.y * sz;
            float y1 = direction.x * sz + direction.y * cz;
            float z1 = direction.z;

            float cx = (float)Math.Cos(rx), sx = (float)Math.Sin(rx);
            float x2 = x1;
            float y2 = y1 * cx - z1 * sx;
            float z2 = y1 * sx + z1 * cx;

            float cy = (float)Math.Cos(ry), sy = (float)Math.Sin(ry);
            return new Vector3(
                x2 * cy + z2 * sy,
                y2,
                -x2 * sy + z2 * cy);
        }

        public void LookAt(Vector3 worldPosition) => LookAt(worldPosition, Vector3.up);

        public void LookAt(Vector3 worldPosition, Vector3 worldUp)
        {
            LastLookAtPosition = worldPosition;
            LastLookAtUp = worldUp;
            var diff = worldPosition - position;
            if (diff.sqrMagnitude > 1e-6f)
            {
                forward = diff.normalized;
            }
        }

        public Transform GetChild(int index) => _children[index];
        public void AddChild(Transform child)
        {
            child.parent = this;
            _children.Add(child);
        }
        public bool IsChildOf(Transform parent)
        {
            var cur = this;
            var seen = new HashSet<Transform>();
            while (cur != null && seen.Add(cur))
            {
                if (ReferenceEquals(cur, parent)) return true;
                cur = cur.parent;
            }
            return false;
        }
        public System.Collections.IEnumerator GetEnumerator() => _children.GetEnumerator();
    }

    public class RectTransform : Transform
    {
        public Vector2 anchoredPosition { get; set; }
        public Vector2 sizeDelta { get; set; }
        public Rect rect { get; set; }
        public Vector2 anchorMin { get; set; } = new Vector2(0.5f, 0.5f);
        public Vector2 anchorMax { get; set; } = new Vector2(0.5f, 0.5f);
        public Vector2 pivot { get; set; } = new Vector2(0.5f, 0.5f);
        public Vector2 offsetMin { get; set; }
        public Vector2 offsetMax { get; set; }
    }

    public class Component : Object
    {
        public GameObject gameObject { get; internal set; } = null!;
        public Transform transform => gameObject.transform;
    }

    public class Behaviour : Component
    {
        public bool enabled { get; set; } = true;
        public bool isActiveAndEnabled => enabled && (gameObject == null || gameObject.activeInHierarchy);
    }

    public class MonoBehaviour : Behaviour { }
    public class Collider : Component { public bool enabled { get; set; } = true; public Bounds bounds { get; set; } }
    public class Renderer : Component
    {
        public bool enabled { get; set; } = true;
        public Bounds bounds { get; set; }
        public Material[]? sharedMaterials { get; set; }
        public Material? sharedMaterial
        {
            get => sharedMaterials != null && sharedMaterials.Length > 0 ? sharedMaterials[0] : null;
            set => sharedMaterials = value != null ? new[] { value } : Array.Empty<Material>();
        }
    }
    public class AudioSource : Behaviour { }
    public class AudioListener : Behaviour { }
    public class Rigidbody : Component { }
    public class Shader : Object
    {
        public static Func<string, Shader?>? FindFunc;
        public static Shader? Find(string name) => FindFunc != null ? FindFunc(name) : new Shader { name = name };
    }
    public class Material : Object
    {
        public Shader? shader { get; set; }
        public Texture? mainTexture { get; set; }
        public Material() { }
        public Material(Shader? shader) { this.shader = shader; }
        public void SetTexture(string name, Texture? value) { }
    }

    public enum RenderMode
    {
        ScreenSpaceOverlay = 0,
        ScreenSpaceCamera = 1,
        WorldSpace = 2
    }

    public class Canvas : Behaviour
    {
        public RenderMode renderMode { get; set; } = RenderMode.ScreenSpaceOverlay;
        public int sortingOrder { get; set; }
        public string sortingLayerName { get; set; } = "Default";
        public bool isRootCanvas { get; set; } = true;
        public float scaleFactor { get; set; } = 1f;
        public Rect pixelRect { get; set; } = new Rect(0, 0, 1920, 1080);
        public static void ForceUpdateCanvases() { }
    }

    public static class Mathf
    {
        public const float Deg2Rad = (float)(Math.PI / 180.0);
        public static int Clamp(int value, int min, int max) => Math.Max(min, Math.Min(max, value));
        public static float Clamp(float value, float min, float max) => Math.Max(min, Math.Min(max, value));
        public static float Max(float a, float b) => Math.Max(a, b);
        public static float Min(float a, float b) => Math.Min(a, b);
        public static float Tan(float f) => (float)Math.Tan(f);
        public static float Atan(float f) => (float)Math.Atan(f);
        public static float Sin(float f) => (float)Math.Sin(f);
        public static float Abs(float f) => Math.Abs(f);
    }

    public class Texture : Object { }
    public enum TextureFormat { RGB24, RGBA32 }
    public class Texture2D : Texture
    {
        public static Action<Texture2D>? OnReadPixelsAction;
        public int width { get; set; }
        public int height { get; set; }
        public Texture2D(int width, int height, TextureFormat format = TextureFormat.RGBA32, bool mipChain = false)
        {
            this.width = width;
            this.height = height;
        }
        public void ReadPixels(Rect source, int destX, int destY, bool recalculateMipMaps = true)
        {
            OnReadPixelsAction?.Invoke(this);
        }
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

    public enum CameraClearFlags
    {
        Skybox = 1,
        Color = 2,
        SolidColor = 2,
        Depth = 3,
        Nothing = 4
    }

    public class Camera : Behaviour
    {
        public static Camera? main;
        public static Action<Camera>? OnBeforeRenderAction;
        public static readonly List<Camera> AllCamerasList = new();
        public static Camera[] allCameras => AllCamerasList.ToArray();
        public static int allCamerasCount => AllCamerasList.Count;

        public UnityEngine.SceneManagement.Scene scene { get; set; }
        public bool orthographic { get; set; }
        public float orthographicSize { get; set; } = 5f;
        public float fieldOfView { get; set; } = 60f;
        public float nearClipPlane { get; set; } = 0.3f;
        public float farClipPlane { get; set; } = 1000f;
        public Rect rect { get; set; } = new Rect(0, 0, 1, 1);
        public int pixelWidth { get; set; } = 1920;
        public int pixelHeight { get; set; } = 1080;
        public CameraClearFlags clearFlags { get; set; } = CameraClearFlags.Skybox;
        public Color backgroundColor { get; set; } = new Color(0.19f, 0.30f, 0.47f);
        public int cullingMask { get; set; } = -1;
        public float depth { get; set; } = 0f;
        public string renderingPath { get; set; } = "UsePlayerSettings";
        public RenderTexture? targetTexture { get; set; }
        public void Render() => OnBeforeRenderAction?.Invoke(this);
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
        public UnityEngine.SceneManagement.Scene scene { get; set; }
        public Transform transform { get; internal set; }

        public GameObject(string name = "")
        {
            this.name = name;
            transform = new Transform { gameObject = this, name = name };
            _components.Add(transform);
        }

        public GameObject(string name, params Type[] components)
        {
            this.name = name;
            if (components != null && components.Any(t => typeof(RectTransform).IsAssignableFrom(t)))
            {
                var rt = new RectTransform { gameObject = this, name = name };
                transform = rt;
                _components.Add(rt);
            }
            else
            {
                transform = new Transform { gameObject = this, name = name };
                _components.Add(transform);
            }

            if (components != null)
            {
                foreach (var t in components)
                {
                    if (typeof(Transform).IsAssignableFrom(t)) continue;
                    var comp = (Component)Activator.CreateInstance(t)!;
                    comp.gameObject = this;
                    comp.name = t.Name;
                    _components.Add(comp);
                }
            }
        }

        public T AddComponent<T>() where T : Component, new()
        {
            if (typeof(RectTransform).IsAssignableFrom(typeof(T)))
            {
                var rt = new RectTransform { gameObject = this, name = typeof(T).Name };
                _components.Remove(transform);
                transform = rt;
                _components.Insert(0, rt);
                return (T)(Component)rt;
            }

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

        public T[] GetComponentsInChildren<T>(bool includeInactive = false) where T : class
        {
            var result = new List<T>();
            CollectComponentsInChildren(transform, includeInactive, result);
            return result.ToArray();
        }

        private static void CollectComponentsInChildren<T>(Transform tr, bool includeInactive, List<T> result) where T : class
        {
            if (tr?.gameObject == null) return;
            if (!includeInactive && !tr.gameObject.activeSelf) return;

            foreach (var c in tr.gameObject._components)
            {
                if (c is T t)
                {
                    result.Add(t);
                }
            }

            foreach (Transform child in tr)
            {
                CollectComponentsInChildren(child, includeInactive, result);
            }
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
        public static string dataPath { get; set; } = System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), "Assets");
        public static string unityVersion { get; set; } = "6000.7.0f1";
        public static bool isPlaying { get; set; } = false;
        public static bool isBatchMode { get; set; } = false;
        public delegate void LogCallback(string condition, string stackTrace, LogType type);
        public static event LogCallback? logMessageReceivedThreaded { add { } remove { } }
    }

    public static class SystemInfo
    {
        public static UnityEngine.Rendering.GraphicsDeviceType graphicsDeviceType { get; set; } = UnityEngine.Rendering.GraphicsDeviceType.Direct3D11;
    }

    public static class ScreenCapture
    {
        public static Action<string, int>? CaptureScreenshotAction;
        public static void CaptureScreenshot(string filename, int superSize = 1)
        {
            CaptureScreenshotAction?.Invoke(filename, superSize);
        }
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
    public enum GraphicsDeviceType
    {
        Direct3D11 = 2,
        Null = 4,
        Metal = 16,
        OpenGLCore = 17,
        Vulkan = 21
    }

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

        public static Func<Camera, StandardRequest, bool>? SupportsRenderRequestFunc;
        public static Action<Camera, StandardRequest>? SubmitRenderRequestAction;

        public static bool SupportsRenderRequest(Camera camera, StandardRequest data) =>
            SupportsRenderRequestFunc?.Invoke(camera, data) ?? false;
        public static void SubmitRenderRequest(Camera camera, StandardRequest data) =>
            SubmitRenderRequestAction?.Invoke(camera, data);
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
        private static bool s_Enabled;
        public static Action<bool>? SetEnabledAction;
        public static bool enabled
        {
            get => s_Enabled;
            set
            {
                SetEnabledAction?.Invoke(value);
                s_Enabled = value;
            }
        }
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
    public enum ImportAssetOptions { Default = 0, ForceSynchronousImport = 1 }

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
        public static Func<UnityEngine.Object, bool>? IsPersistentFunc;
        public static Action? UnloadUnusedAssetsImmediateAction;
        public static bool IsPersistent(UnityEngine.Object target) => IsPersistentFunc?.Invoke(target) ?? false;
        public static void UnloadUnusedAssetsImmediate() => UnloadUnusedAssetsImmediateAction?.Invoke();
        public static UnityEngine.GameObject CreateGameObjectWithHideFlags(string name, UnityEngine.HideFlags flags, params Type[] components)
        {
            var go = new UnityEngine.GameObject(name, components);
            go.hideFlags = flags;
            return go;
        }
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

    public sealed class LogEntry
    {
        public string message = string.Empty;
        public string file = string.Empty;
        public int line;
        public int mode;
    }

    public static class LogEntries
    {
        public sealed class StubEntry
        {
            public string Message { get; set; } = string.Empty;
            public string File { get; set; } = string.Empty;
            public int Line { get; set; }
            public int Mode { get; set; }
            public int Count { get; set; } = 1;
        }

        public static readonly List<StubEntry> Entries = new();
        public static int StartGettingEntriesCallCount { get; private set; }
        public static int EndGettingEntriesCallCount { get; private set; }
        public static bool ThrowOnGetEntry { get; set; }
        public static int? UnfilteredErrorCount { get; set; }
        public static int? UnfilteredWarningCount { get; set; }
        public static int? UnfilteredLogCount { get; set; }
        public static string FilteringText { get; set; } = string.Empty;

        public static void Reset()
        {
            Entries.Clear();
            StartGettingEntriesCallCount = 0;
            EndGettingEntriesCallCount = 0;
            ThrowOnGetEntry = false;
            UnfilteredErrorCount = null;
            UnfilteredWarningCount = null;
            UnfilteredLogCount = null;
            FilteringText = string.Empty;
        }

        public static int StartGettingEntries()
        {
            StartGettingEntriesCallCount++;
            return Entries.Count;
        }

        public static void EndGettingEntries()
        {
            EndGettingEntriesCallCount++;
        }

        public static int GetCount() => Entries.Count;

        public static bool GetEntryInternal(int row, LogEntry outputEntry)
        {
            if (ThrowOnGetEntry)
            {
                throw new InvalidOperationException("Simulated LogEntries failure");
            }

            if (row < 0 || row >= Entries.Count || outputEntry == null)
            {
                return false;
            }

            var src = Entries[row];
            outputEntry.message = src.Message;
            outputEntry.file = src.File;
            outputEntry.line = src.Line;
            outputEntry.mode = src.Mode;
            return true;
        }

        public static int GetEntryCount(int row)
        {
            if (row < 0 || row >= Entries.Count)
            {
                return 0;
            }

            return Entries[row].Count;
        }

        public static void GetCountsByType(ref int errorCount, ref int warningCount, ref int logCount)
        {
            const int errorMask = (1 << 0) | (1 << 1) | (1 << 4) | (1 << 6) | (1 << 8) | (1 << 11) | (1 << 17) | (1 << 20) | (1 << 21);
            const int warningMask = (1 << 7) | (1 << 9) | (1 << 12);

            int computedErrors = 0;
            int computedWarnings = 0;
            int computedLogs = 0;
            foreach (StubEntry entry in Entries)
            {
                int count = Math.Max(1, entry.Count);
                if ((entry.Mode & errorMask) != 0)
                {
                    computedErrors += count;
                }
                else if ((entry.Mode & warningMask) != 0)
                {
                    computedWarnings += count;
                }
                else
                {
                    computedLogs += count;
                }
            }

            errorCount = UnfilteredErrorCount ?? computedErrors;
            warningCount = UnfilteredWarningCount ?? computedWarnings;
            logCount = UnfilteredLogCount ?? computedLogs;
        }

        public static string GetFilteringText() => FilteringText;
    }
    public class EditorWindow : UnityEngine.ScriptableObject
    {
        public static EditorWindow? focusedWindow { get; set; }
        public static Func<Type, bool, string?, bool, EditorWindow?>? GetWindowFunc;
        public int FocusCount { get; private set; }
        public int RepaintCount { get; private set; }
        public bool IsClosed { get; private set; }

        public virtual void Focus()
        {
            FocusCount++;
            focusedWindow = this;
        }

        public virtual void Repaint()
        {
            RepaintCount++;
        }

        public virtual void Close()
        {
            IsClosed = true;
        }

        public static EditorWindow? GetWindow(Type t, bool utility = false, string? title = null, bool focus = true)
        {
            if (GetWindowFunc != null)
            {
                return GetWindowFunc(t, utility, title, focus);
            }

            if (Activator.CreateInstance(t) is EditorWindow ew)
            {
                if (focus)
                {
                    focusedWindow = ew;
                }
                return ew;
            }
            return null;
        }
    }

    public class GameView : EditorWindow
    {
    }
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
        private static Action? s_Update;
        public static Action? UpdateSubscribeAction;
        public static event Action? update
        {
            add
            {
                UpdateSubscribeAction?.Invoke();
                s_Update += value;
            }
            remove
            {
                s_Update -= value;
            }
        }
        public static event Action? projectChanged { add { } remove { } }
        public static event Action? quitting { add { } remove { } }
        public static void Tick() => s_Update?.Invoke();
        public static Action? QueuePlayerLoopUpdateAction;
        public static void QueuePlayerLoopUpdate() => QueuePlayerLoopUpdateAction?.Invoke();
        public static string applicationContentsPath { get; set; } = string.Empty;
    }

    public enum SerializedPropertyType
    {
        Generic, Integer, Boolean, Float, String, Color, ObjectReference, LayerMask, Enum, Vector2, Vector3, Vector4, Rect, ArraySize, Character, AnimationCurve, Bounds, Gradient, Quaternion, ExposedReference, FixedBufferSize, Vector2Int, Vector3Int, RectInt, BoundsInt, ManagedReference, Hash128
    }

    public class SerializedProperty
    {
        public static readonly List<(string PropertyPath, string RelativePath)> FindPropertyRelativeCalls = new();
        public static readonly List<(string PropertyPath, bool EnterChildren)> NextVisibleCalls = new();

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
        public int objectReferenceInstanceIDValue { get; set; }
        private bool _isArray;
        public bool isArray
        {
            get => _isArray || propertyType == SerializedPropertyType.String;
            set => _isArray = value;
        }
        public int arraySize { get; set; }

        private readonly List<SerializedProperty> _visibleProperties = new();
        private readonly Dictionary<string, SerializedProperty> _relativeProperties = new(StringComparer.Ordinal);
        private readonly List<SerializedProperty> _arrayElements = new();
        private int _currentIndex = -1;
        private SerializedProperty? _currentProperty;

        public void AddChildProperty(SerializedProperty prop) => _visibleProperties.Add(prop);

        public void SetRelativeProperty(string relativePath, SerializedProperty prop)
        {
            _relativeProperties[relativePath] = prop;
        }

        public void AddArrayElement(SerializedProperty element)
        {
            _arrayElements.Add(element);
            isArray = true;
            arraySize = _arrayElements.Count;
        }

        public SerializedProperty? FindPropertyRelative(string relativePropertyPath)
        {
            FindPropertyRelativeCalls.Add((propertyPath, relativePropertyPath));
            return _relativeProperties.TryGetValue(relativePropertyPath, out var prop) ? prop : null;
        }

        public SerializedProperty GetArrayElementAtIndex(int index)
        {
            return _arrayElements[index];
        }

        public bool NextVisible(bool enterChildren)
        {
            if (_currentProperty != null)
            {
                NextVisibleCalls.Add((_currentProperty.propertyPath, enterChildren));
                if (enterChildren && _currentProperty._visibleProperties.Count > 0)
                {
                    _visibleProperties.InsertRange(_currentIndex + 1, _currentProperty._visibleProperties);
                }
            }

            _currentIndex++;
            if (_currentIndex < _visibleProperties.Count)
            {
                var cur = _visibleProperties[_currentIndex];
                _currentProperty = cur;
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
                objectReferenceInstanceIDValue = cur.objectReferenceInstanceIDValue;
                isArray = cur.isArray;
                arraySize = cur.arraySize;
                _relativeProperties.Clear();
                foreach (var kv in cur._relativeProperties)
                {
                    _relativeProperties[kv.Key] = kv.Value;
                }
                _arrayElements.Clear();
                _arrayElements.AddRange(cur._arrayElements);
                return true;
            }
            _currentProperty = null;
            return false;
        }
    }

    public class SerializedObject : IDisposable
    {
        public static readonly Dictionary<UnityEngine.Object, List<SerializedProperty>> CustomProperties = new();
        public static readonly List<UnityEngine.Object> CreatedTargets = new();
        public static int DisposeCount { get; set; }
        public UnityEngine.Object targetObject { get; }
        public bool IsDisposed { get; private set; }
        public readonly List<SerializedProperty> Properties = new();

        public SerializedObject(UnityEngine.Object target)
        {
            targetObject = target;
            if (target != null)
            {
                CreatedTargets.Add(target);
                if (CustomProperties.TryGetValue(target, out var props))
                {
                    Properties.AddRange(props);
                }
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
        public static Action<ImportAssetOptions>? RefreshFunc;
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
        public static void Refresh(ImportAssetOptions options = ImportAssetOptions.Default) => RefreshFunc?.Invoke(options);
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

        public static class EditorSceneManager
        {
            public static Func<UnityEngine.SceneManagement.Scene>? NewPreviewSceneFunc;
            public static Func<UnityEngine.SceneManagement.Scene, bool>? ClosePreviewSceneFunc;

            public static UnityEngine.SceneManagement.Scene NewPreviewScene() =>
                NewPreviewSceneFunc != null
                    ? NewPreviewSceneFunc()
                    : new UnityEngine.SceneManagement.Scene { name = "PreviewScene", isLoaded = true };

            public static bool ClosePreviewScene(UnityEngine.SceneManagement.Scene scene) =>
                ClosePreviewSceneFunc?.Invoke(scene) ?? true;
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

        public static Func<string, UnityEngine.GameObject?>? LoadPrefabContentsFunc;
        public static Action<UnityEngine.GameObject>? UnloadPrefabContentsAction;
        public static Func<UnityEngine.Object, UnityEngine.SceneManagement.Scene, UnityEngine.Object?>? InstantiatePrefabInSceneFunc;

        public static UnityEngine.GameObject? LoadPrefabContents(string assetPath) =>
            LoadPrefabContentsFunc != null
                ? LoadPrefabContentsFunc(assetPath)
                : AssetDatabase.LoadMainAssetAtPath(assetPath) as UnityEngine.GameObject;

        public static void UnloadPrefabContents(UnityEngine.GameObject contentsRoot) =>
            UnloadPrefabContentsAction?.Invoke(contentsRoot);

        public static UnityEngine.Object? InstantiatePrefab(UnityEngine.Object assetComponentOrGameObject, UnityEngine.SceneManagement.Scene destinationScene) =>
            InstantiatePrefabInSceneFunc?.Invoke(assetComponentOrGameObject, destinationScene);
    }

    public static class ShaderUtil
    {
        public static Func<UnityEngine.Shader, bool>? ShaderHasErrorFunc;
        public static bool ShaderHasError(UnityEngine.Shader s) => ShaderHasErrorFunc?.Invoke(s) ?? false;
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
            public Assembly[] assemblyReferences { get; set; } = System.Array.Empty<Assembly>();
            public string[] defines { get; set; } = System.Array.Empty<string>();
            public int flags { get; set; }
            public ScriptCompilerOptions compilerOptions { get; set; } = new ScriptCompilerOptions();
            public string[] allReferences { get; set; } = System.Array.Empty<string>();
        }

        public class ScriptCompilerOptions
        {
            public string[] AdditionalCompilerArguments { get; set; } = System.Array.Empty<string>();
            public bool AllowUnsafeCode { get; set; }
            public string AnalyzerConfigPath { get; set; } = string.Empty;
            public string ApiCompatibilityLevel { get; set; } = string.Empty;
            public string CodeOptimization { get; set; } = string.Empty;
            public string LanguageVersion { get; set; } = string.Empty;
            public string[] ResponseFiles { get; set; } = System.Array.Empty<string>();
            public string[] RoslynAdditionalFilePaths { get; set; } = System.Array.Empty<string>();
            public string[] RoslynAnalyzerDllPaths { get; set; } = System.Array.Empty<string>();
            public string RoslynAnalyzerRulesetPath { get; set; } = string.Empty;
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
            public static Func<string[]>? GetPrecompiledAssemblyPathsFunc;
            public static Action? RequestScriptCompilationAction;

            public static event Action<object>? compilationStarted { add { } remove { } }
            public static event Action<object>? compilationFinished { add { } remove { } }
            public static event Action<string, CompilerMessage[]>? assemblyCompilationFinished { add { } remove { } }

            public static Assembly[] GetAssemblies() => GetAssembliesFunc?.Invoke() ?? System.Array.Empty<Assembly>();
            public static Assembly[] GetAssemblies(AssembliesType assembliesType) => GetAssembliesFunc?.Invoke() ?? System.Array.Empty<Assembly>();
            public static string[] GetPrecompiledAssemblyPaths(PrecompiledAssemblySources sources) => GetPrecompiledAssemblyPathsFunc?.Invoke() ?? System.Array.Empty<string>();
            public static string? GetPrecompiledAssemblyPathFromAssemblyName(string assemblyName) => null;
            public static void RequestScriptCompilation() => RequestScriptCompilationAction?.Invoke();
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
            public static Func<ExecutionSettings, string>? OnExecuteWithGuid;
            public static Action<string>? OnCancelTestRun;
            public static ICallbacks? LastRegisteredCallbacks;

            public void RegisterCallbacks<T>(T callbacks) where T : ICallbacks
            {
                LastRegisteredCallbacks = callbacks;
            }
            public string Execute(ExecutionSettings settings)
            {
                if (OnExecuteWithGuid != null)
                    return OnExecuteWithGuid(settings);

                OnExecute?.Invoke(settings);
                return Guid.NewGuid().ToString("N");
            }
            public static void CancelTestRun(string guid) => OnCancelTestRun?.Invoke(guid);
            internal static object? IsRunActive() => ActiveStateKnown ? (object)Active : null;
        }
    }

    namespace PackageManager
    {
        public class PackageInfo
        {
            public string resolvedPath { get; set; } = string.Empty;
            public static Func<PackageInfo[]>? GetAllRegisteredPackagesFunc;
            public static PackageInfo[] GetAllRegisteredPackages() => GetAllRegisteredPackagesFunc?.Invoke() ?? System.Array.Empty<PackageInfo>();
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

    internal static class UnityLeanMcpServer
    {
        public static bool IsPlayModeActive { get; internal set; }
        public static bool IsPlayModeChanging { get; internal set; }
    }

    internal static class OperationExecutionEngine
    {
        private static readonly object s_Lock = new object();
        private static System.Threading.CancellationTokenSource? s_ActiveCts;
        private static string? s_ActiveOperationId;
        private static bool s_ActiveIsCancelable;

        public static System.Threading.CancellationTokenSource RegisterActiveOperation(string operationId, bool isCancelable)
        {
            var cts = new System.Threading.CancellationTokenSource();
            System.Threading.CancellationTokenSource? oldCts;
            lock (s_Lock)
            {
                oldCts = s_ActiveCts;
                s_ActiveCts = cts;
                s_ActiveOperationId = operationId;
                s_ActiveIsCancelable = isCancelable;
            }
            oldCts?.Dispose();
            return cts;
        }

        public static System.Threading.CancellationToken GetActiveOperationToken()
        {
            lock (s_Lock)
            {
                if (s_ActiveCts == null)
                {
                    return System.Threading.CancellationToken.None;
                }
                try
                {
                    return s_ActiveCts.Token;
                }
                catch (ObjectDisposedException)
                {
                    return System.Threading.CancellationToken.None;
                }
            }
        }

        public static bool TryCancel(string operationId)
        {
            System.Threading.CancellationTokenSource? cts = null;
            lock (s_Lock)
            {
                if (s_ActiveCts != null && s_ActiveIsCancelable && s_ActiveOperationId == operationId)
                {
                    cts = s_ActiveCts;
                }
            }
            if (cts != null)
            {
                cts.Cancel();
                return true;
            }
            return false;
        }

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
            System.Threading.CancellationTokenSource? toDispose = null;
            lock (s_Lock)
            {
                if (s_ActiveOperationId == operationId)
                {
                    toDispose = s_ActiveCts;
                    s_ActiveCts = null;
                    s_ActiveOperationId = null;
                    s_ActiveIsCancelable = false;
                }
            }
            toDispose?.Dispose();

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
    }
}

namespace UnityEngine.UIElements
{
    public class VisualElement
    {
        private readonly List<VisualElement> _children = new();
        private readonly List<string> _classes = new();

        public string name { get; set; } = string.Empty;
        public bool visible { get; set; } = true;
        public bool enabledSelf { get; set; } = true;
        public bool enabledInHierarchy { get; set; } = true;
        public Rect layout { get; set; }
        public Rect worldBound { get; set; }

        public int childCount => _children.Count;
        public VisualElement this[int index] => _children[index];

        public void Add(VisualElement child)
        {
            _children.Add(child);
        }

        public void AddToClassList(string className)
        {
            if (!_classes.Contains(className)) _classes.Add(className);
        }

        public IEnumerable<string> GetClasses() => _classes;
        public IEnumerable<VisualElement> Children() => _children;
    }

    public class TextElement : VisualElement
    {
        public string text { get; set; } = string.Empty;
    }

    public class Label : TextElement
    {
        public Label() { }
        public Label(string text) { this.text = text; }
    }

    public class Button : TextElement
    {
        public Button() { }
        public Button(Action? clickEvent) { }
    }

    public class UIDocument : Behaviour
    {
        public static readonly List<UIDocument> AllUIDocuments = new();

        public VisualElement? rootVisualElement { get; set; }
        public float sortingOrder { get; set; }
        public Object? visualTreeAsset { get; set; }
        public Object? panelSettings { get; set; }

        public UIDocument()
        {
            AllUIDocuments.Add(this);
        }
    }
}

namespace UnityEngine.UI
{
    public class Graphic : MonoBehaviour
    {
        public virtual Material? material { get; set; }
        public virtual Material? defaultMaterial { get; set; }
        public bool raycastTarget { get; set; } = true;
    }

    public class MaskableGraphic : Graphic { }

    public class Image : MaskableGraphic { }

    public class Text : MaskableGraphic
    {
        public string text { get; set; } = string.Empty;
    }
}

namespace TMPro
{
    public class TMP_Text : UnityEngine.UI.MaskableGraphic
    {
        public string text { get; set; } = string.Empty;
        public UnityEngine.Material? fontSharedMaterial { get; set; }
        public UnityEngine.Material[]? fontSharedMaterials { get; set; }
    }

    public class TextMeshProUGUI : TMP_Text { }

    public class TextMeshPro : TMP_Text { }
}
