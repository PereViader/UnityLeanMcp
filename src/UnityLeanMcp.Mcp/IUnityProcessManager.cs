using System.Threading;
using System.Threading.Tasks;

namespace UnityLeanMcp.Mcp;

public interface IUnityProcessManager
{
    IUnityPathResolver PathResolver { get; }
    IUnityExecutableLocator ExecutableLocator { get; }
    bool IsUnityRunning(out int? processId);
    string GetUnityMode(int? pid = null);
    string? GetProjectEditorVersion();
    int ReadPortFile();
    Task<bool> StartUnityAsync(CancellationToken cancellationToken = default);
    Task<bool> WaitForHealthyAsync(CancellationToken cancellationToken = default);
    Task EnsureUnityRunningAsync(CancellationToken cancellationToken = default);
    Task<bool> StopUnityAsync(bool force = false, CancellationToken cancellationToken = default);
    void PurgeOperationState();
}
