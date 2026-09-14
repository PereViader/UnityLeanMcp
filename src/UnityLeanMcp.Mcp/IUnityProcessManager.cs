using System.Threading;
using System.Threading.Tasks;

namespace UnityLeanMcp.Mcp;

public interface IUnityProcessManager
{
    IUnityPathResolver PathResolver { get; }
    IUnityExecutableLocator ExecutableLocator { get; }
    bool IsUnityRunning(out int? processId);
    string GetUnityMode(int? pid = null);
    int ReadPortFile();
    Task EnsureUnityRunningAsync(CancellationToken cancellationToken = default);
    Task<bool> StopUnityAsync(bool force = false, CancellationToken cancellationToken = default);
    void PurgeOperationState();
}
