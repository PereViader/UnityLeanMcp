using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace UnityLeanMcp.Mcp.Tests;

internal static class TestProcessProvider
{
    public static UnityProcessManager WithTrustedTestProcessProvider(this UnityProcessManager manager)
    {
        return new UnityProcessManager(
            manager.PathResolver,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<UnityProcessManager>.Instance,
            executableLocator: manager.ExecutableLocator,
            processProvider: () => new[] { Process.GetCurrentProcess() });
    }

    public static void WriteTrustedPidFile(string projectRoot)
    {
        using var process = Process.GetCurrentProcess();
        string? executablePath = process.MainModule?.FileName;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new InvalidOperationException("The test process executable path is unavailable.");
        }

        var identity = new
        {
            ProcessId = process.Id,
            StartTimeUtcTicks = process.StartTime.ToUniversalTime().Ticks,
            ExecutablePath = executablePath,
            ProjectRoot = Path.GetFullPath(projectRoot)
        };

        string pidFile = Path.Combine(projectRoot, "Temp", "unity_lean_mcp_process.pid");
        File.WriteAllText(pidFile, process.Id.ToString());
        File.WriteAllText(pidFile + ".identity.json", JsonSerializer.Serialize(identity));
    }

    public static bool TryGetRefreshOperationId(string command, out string operationId)
    {
        string[] parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2 &&
            parts[0].Equals("POLL_REFRESH", StringComparison.OrdinalIgnoreCase))
        {
            operationId = parts[1];
            return true;
        }

        operationId = "";
        return false;
    }

    public static void WriteRefreshResult(IUnityPathResolver pathResolver, string operationId)
    {
        var result = new UnityRefreshResult
        {
            OperationId = operationId,
            Success = true,
            Message = "Refresh completed"
        };

        string resultPath = pathResolver.GetResultFilePath(UnityOperationKind.Refresh, operationId);
        File.WriteAllText(resultPath, JsonSerializer.Serialize(result));
    }
}
