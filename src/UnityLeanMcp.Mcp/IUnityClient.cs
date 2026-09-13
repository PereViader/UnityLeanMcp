using System;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol;

namespace UnityLeanMcp.Mcp;

public interface IUnityClient
{
    int PollIntervalMs { get; set; }
    Task<string> GetStatusAsync(CancellationToken cancellationToken = default);
    Task<UnityRefreshResult> RefreshAsync(bool isRecompile = false, IProgress<ProgressNotificationValue>? progress = null, CancellationToken cancellationToken = default);
    Task<UnityEvalResult> EvalAsync(string code, IProgress<ProgressNotificationValue>? progress = null, CancellationToken cancellationToken = default);
    [Obsolete("unity_execute_method has been retired; use EvalAsync instead.")]
    Task<UnityExecuteResult> ExecuteMethodAsync(string methodName, string[]? args, IProgress<ProgressNotificationValue>? progress = null, CancellationToken cancellationToken = default);
    Task<UnityTestRunResult> RunTestsAsync(
        string[]? testNames,
        string[]? groupNames,
        string[]? categoryNames,
        string[]? assemblyNames,
        string? mode,
        bool failedOnly = false,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default);

    Task<UnityTestRunResult> RunTestsAsync(
        string? filter,
        string? category,
        string? mode,
        bool failedOnly = false,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default) =>
        RunTestsAsync(
            testNames: null,
            groupNames: !string.IsNullOrEmpty(filter) ? [filter] : null,
            categoryNames: !string.IsNullOrEmpty(category) ? [category] : null,
            assemblyNames: null,
            mode: mode,
            failedOnly: failedOnly,
            progress: progress,
            cancellationToken: cancellationToken);
}
