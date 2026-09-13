namespace UnityEngine
{
    public class Object { }
    public class GameObject { }
    public static class Debug
    {
        public static void LogWarning(object? message) { }
    }
}

namespace UnityEditor
{
    public class Editor { }
    public static class EditorApplication
    {
        public static string applicationContentsPath => string.Empty;
    }
}
