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
        public static string applicationContentsPath { get; set; } = string.Empty;
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
