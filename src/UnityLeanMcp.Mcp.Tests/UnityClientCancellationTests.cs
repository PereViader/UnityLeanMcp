using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace UnityLeanMcp.Mcp.Tests;

[Trait("Category", "Unit")]
public sealed class UnityClientCancellationTests
{
    [Fact]
    public async Task EvalAsync_CancellationDuringInitialDispatch_CancelsDispatchedOperation()
    {
        string projectRoot = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "unity_eval_cancel_" + Guid.NewGuid().ToString("N"));
        var pathResolver = new UnityPathResolver(projectRoot);
        var processManager = new StubProcessManager(pathResolver);
        var transport = new DispatchCancellationTransport();
        var poller = new RecordingOperationPoller();
        var client = new UnityClient(
            processManager,
            pathResolver,
            NullLogger<UnityClient>.Instance,
            transport,
            poller);

        using var cancellation = new CancellationTokenSource();
        Task<UnityEvalResult> evalTask = client.EvalAsync("return 42;", cancellation.Token);

        await transport.EvalDispatched.Task;
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await evalTask);
        Assert.Equal(1, poller.CancellationCount);
        Assert.Equal("eval", poller.LastKind);
        Assert.Equal(transport.OperationId, poller.LastOperationId);
    }

    [Fact]
    public async Task RunTestsAsync_CancellationDuringInitialDispatch_CancelsDispatchedOperation()
    {
        string projectRoot = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "unity_test_cancel_" + Guid.NewGuid().ToString("N"));
        var pathResolver = new UnityPathResolver(projectRoot);
        var processManager = new StubProcessManager(pathResolver);
        var transport = new DispatchCancellationTransport();
        var poller = new RecordingOperationPoller();
        var client = new UnityClient(
            processManager,
            pathResolver,
            NullLogger<UnityClient>.Instance,
            transport,
            poller);

        using var cancellation = new CancellationTokenSource();
        Task<UnityTestRunResult> testTask = client.RunTestsAsync(
            filter: null,
            category: null,
            mode: "EditMode",
            progress: null,
            cancellationToken: cancellation.Token);

        await transport.TestDispatched.Task;
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await testTask);
        Assert.Equal(1, poller.CancellationCount);
        Assert.Equal("test", poller.LastKind);
        Assert.Equal(transport.OperationId, poller.LastOperationId);
    }

    private sealed class DispatchCancellationTransport : IUnitySocketTransport
    {
        private readonly TaskCompletionSource<string?> _evalResponse =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> EvalDispatched { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource<string?> _testResponse =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> TestDispatched { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string? OperationId { get; private set; }

        public Task<string?> SendCommandAsync(
            int port,
            string command,
            int timeoutSeconds = 10,
            CancellationToken cancellationToken = default)
        {
            if (command.StartsWith("POLL_REFRESH CHECK ", StringComparison.Ordinal))
            {
                return Task.FromResult<string?>("READY");
            }

            if (command.StartsWith("EVAL ", StringComparison.Ordinal))
            {
                string[] parts = command.Split(' ', 3);
                OperationId = parts[1];
                EvalDispatched.TrySetResult(true);
                cancellationToken.Register(() => _evalResponse.TrySetCanceled(cancellationToken));
                return _evalResponse.Task;
            }

            if (command.StartsWith("RUN_TESTS ", StringComparison.Ordinal))
            {
                string[] parts = command.Split(' ', 3);
                OperationId = parts[1];
                TestDispatched.TrySetResult(true);
                cancellationToken.Register(() => _testResponse.TrySetCanceled(cancellationToken));
                return _testResponse.Task;
            }

            throw new InvalidOperationException($"Unexpected command: {command}");
        }

        public Task<bool> IsSocketReadyAsync(
            int port,
            int timeoutSeconds = 2,
            CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private sealed class RecordingOperationPoller : IOperationPoller
    {
        public int CancellationCount { get; private set; }
        public string? LastOperationId { get; private set; }
        public string? LastKind { get; private set; }

        public Task<TResult> PollOperationUntilTerminalAsync<TResult>(
            OperationPollingSpec<TResult> spec,
            CancellationToken cancellationToken) where TResult : class, IOperationResult, new() =>
            Task.FromException<TResult>(new InvalidOperationException("Polling should not begin."));

        public Task<TResult> PollOperationResultAsync<TResult>(
            string opId,
            string kind,
            string operationDisplayName,
            string resultFilePath,
            string pollCommand,
            int pollIntervalMs = 500,
            Func<TResult, TResult>? onResultFound = null,
            CancellationToken cancellationToken = default) where TResult : UnityOperationResult, new() =>
            Task.FromException<TResult>(new InvalidOperationException("Polling should not begin."));

        public Task CancelOperationAsync(string opId, string kind)
        {
            CancellationCount++;
            LastOperationId = opId;
            LastKind = kind;
            return Task.CompletedTask;
        }
    }

    private sealed class StubProcessManager : IUnityProcessManager
    {
        public StubProcessManager(IUnityPathResolver pathResolver)
        {
            PathResolver = pathResolver;
        }

        public IUnityPathResolver PathResolver { get; }
        public IUnityExecutableLocator ExecutableLocator => throw new NotSupportedException();
        public bool IsUnityRunning(out int? processId)
        {
            processId = 1;
            return true;
        }

        public string GetUnityMode(int? pid = null) => "Batchmode";
        public int ReadPortFile() => 1;
        public Task EnsureUnityRunningAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> StopUnityAsync(bool force = false, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public void PurgeOperationState() { }
    }
}
