using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace UnityLeanMcp.Mcp.Tests;

internal sealed class StubProcessManager : IUnityProcessManager
{
    private readonly bool _isRunning;
    private readonly int _port;
    private readonly int _processId;

    public IUnityPathResolver PathResolver { get; }
    public IUnityExecutableLocator ExecutableLocator => throw new NotSupportedException();

    public StubProcessManager(IUnityPathResolver pathResolver, bool isRunning = true, int port = 12345, int processId = 1234)
    {
        PathResolver = pathResolver;
        _isRunning = isRunning;
        _port = port;
        _processId = processId;
    }

    public bool IsUnityRunning(out int? processId)
    {
        processId = _isRunning ? _processId : null;
        return _isRunning;
    }

    public string GetUnityMode(int? pid = null) => "Batchmode";
    public int ReadPortFile() => _port;
    public Task EnsureUnityRunningAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<bool> StopUnityAsync(bool force = false, CancellationToken cancellationToken = default) => Task.FromResult(true);
    public void PurgeOperationState() { }
}

internal sealed class RecordingOperationPoller : IOperationPoller
{
    private readonly Func<OperationPollingSpec<UnityRefreshResult>, CancellationToken, Task<UnityRefreshResult>>? _refresh;

    public RecordingOperationPoller(
        Func<OperationPollingSpec<UnityRefreshResult>, CancellationToken, Task<UnityRefreshResult>>? refresh = null)
    {
        _refresh = refresh;
    }

    public int CancellationCount { get; private set; }
    public string? LastOperationId { get; private set; }
    public string? LastKind { get; private set; }
    public CancellationToken LastCancellationToken { get; private set; }
    public string? OperationId { get; private set; }
    public string? ResultFilePath { get; private set; }

    public async Task<TResult> PollOperationUntilTerminalAsync<TResult>(
        OperationPollingSpec<TResult> spec,
        CancellationToken cancellationToken) where TResult : class, IOperationResult, new()
    {
        OperationId = spec.OperationId;
        ResultFilePath = spec.ResultFilePath;

        if (typeof(TResult) == typeof(UnityRefreshResult))
        {
            var refreshSpec = (OperationPollingSpec<UnityRefreshResult>)(object)spec;
            if (_refresh != null)
            {
                Assert.True(refreshSpec.RequireDurableResult);
                return (TResult)(object)await _refresh(refreshSpec, cancellationToken);
            }

            return (TResult)(object)new UnityRefreshResult
            {
                OperationId = spec.OperationId,
                Success = true
            };
        }

        return await Task.FromException<TResult>(new InvalidOperationException("Polling should not begin."));
    }

    public Task CancelOperationAsync(string opId, string kind, CancellationToken cancellationToken = default)
    {
        CancellationCount++;
        LastOperationId = opId;
        LastKind = kind;
        LastCancellationToken = cancellationToken;
        return Task.CompletedTask;
    }
}
