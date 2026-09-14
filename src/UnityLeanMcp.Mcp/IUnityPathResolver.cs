namespace UnityLeanMcp.Mcp;

public interface IUnityPathResolver
{
    string ProjectRoot { get; }
    string TempDir { get; }
    string OperationFile { get; }
    string CompilationErrorsFile { get; }
    string PortFile { get; }
    string LogFile { get; }
    string PidFile { get; }
    string StartupLockFile { get; }
    string TestRunningFile { get; }
    string GetResultFilePath(UnityOperationKind kind, string? operationId = null);
}
