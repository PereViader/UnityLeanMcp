using System;
using System.IO;

namespace UnityLeanMcp.Mcp;

public class UnityPathResolver : IUnityPathResolver
{
    public string ProjectRoot { get; }
    public string TempDir => Path.Combine(ProjectRoot, "Temp");
    public string OperationFile => Path.Combine(TempDir, "unity_lean_mcp_operation.json");
    public string CompilationErrorsFile => Path.Combine(TempDir, "unity_compilation_errors.txt");
    public string PortFile => Path.Combine(TempDir, "unity_lean_mcp_port.txt");
    public string LogFile => Path.Combine(ProjectRoot, "unity_background_log.txt");
    public string PidFile => Path.Combine(TempDir, "unity_lean_mcp_process.pid");
    public string StartupLockFile => Path.Combine(TempDir, "unity_lean_mcp_startup.lock");
    public string TestRunningFile => Path.Combine(TempDir, "unity_test_running.txt");
    public string GetResultFilePath(UnityOperationKind kind, string? operationId = null) => kind switch
    {
        UnityOperationKind.Refresh or UnityOperationKind.Recompile => string.IsNullOrEmpty(operationId)
            ? Path.Combine(TempDir, "unity_refresh_result.json")
            : Path.Combine(TempDir, $"unity_refresh_{operationId}.json"),
        UnityOperationKind.Test => string.IsNullOrEmpty(operationId)
            ? Path.Combine(TempDir, "unity_test_results.json")
            : Path.Combine(TempDir, $"unity_test_{operationId}.json"),
        UnityOperationKind.Eval => string.IsNullOrEmpty(operationId)
            ? Path.Combine(TempDir, "unity_eval_result.json")
            : Path.Combine(TempDir, $"unity_eval_{operationId}.json"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };

    public UnityPathResolver(string projectRoot)
    {
        if (string.IsNullOrWhiteSpace(projectRoot))
        {
            throw new ArgumentException("Project root cannot be null or empty.", nameof(projectRoot));
        }

        ProjectRoot = Path.GetFullPath(projectRoot);
    }
}
