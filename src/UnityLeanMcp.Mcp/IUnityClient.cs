using System;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol;

namespace UnityLeanMcp.Mcp;

public sealed record UnityClientOptions(int PollIntervalMs = 500, TimeSpan? BusyGracePeriod = null);

public interface IUnityClient
{
    int PollIntervalMs { get; }
    TimeSpan BusyGracePeriod { get; }
    Task<string> GetStatusAsync(CancellationToken cancellationToken = default);
    Task<UnityRefreshResult> RefreshAsync(bool isRecompile = false, IProgress<ProgressNotificationValue>? progress = null, CancellationToken cancellationToken = default);
    Task<UnityEvalResult> EvalAsync(string code, IProgress<ProgressNotificationValue>? progress = null, CancellationToken cancellationToken = default);
    Task<UnityTestRunResult> RunTestsAsync(
        string[]? testNames,
        string[]? groupNames,
        string[]? categoryNames,
        string[]? assemblyNames,
        string? mode,
        bool failedOnly = false,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default);
}
