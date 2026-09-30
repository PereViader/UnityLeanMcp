namespace UnityEngine
{
    public static class JsonUtility
    {
        private static readonly System.Text.Json.JsonSerializerOptions Options = new() { IncludeFields = true };
        public static string ToJson<T>(T value, bool prettyPrint = false) => System.Text.Json.JsonSerializer.Serialize(value, Options);
        public static T? FromJson<T>(string json) => System.Text.Json.JsonSerializer.Deserialize<T>(json, Options);
    }
    public class Object { }
    public class GameObject { }
    public static class Debug
    {
        public static void LogWarning(object? message) { }
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
    internal static class UnityLeanMcpPaths
    {
        public static string GetResultFilePath(string kind, string id) => System.IO.Path.Combine(ProjectRoot, kind + "_" + id + ".json");
        public static string GetRefreshResultFile(string id) => GetResultFilePath("refresh", id);
        public static string TestResultsFile => System.IO.Path.Combine(ProjectRoot, "tests.json");
        public static string RefreshResultFile => System.IO.Path.Combine(ProjectRoot, "refresh.json");
        public static string LogFile => System.IO.Path.Combine(ProjectRoot, "Temp", "unity_lean_mcp_worker.log");
        public static string ProjectRoot { get; set; } = System.IO.Directory.GetCurrentDirectory();
    }
}

namespace UnityLeanMcp
{
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
}

namespace UnityLeanMcp
{
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
