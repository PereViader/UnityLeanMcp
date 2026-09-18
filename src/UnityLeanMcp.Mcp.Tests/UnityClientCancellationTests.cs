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
            testNames: null,
            groupNames: null,
            categoryNames: null,
            assemblyNames: null,
            mode: "EditMode",
            failedOnly: false,
            progress: null,
            cancellationToken: cancellation.Token);

        await transport.TestDispatched.Task;
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await testTask);
        Assert.Equal(1, poller.CancellationCount);
        Assert.Equal("test", poller.LastKind);
        Assert.Equal(transport.OperationId, poller.LastOperationId);
    }

    [Fact]
    public async Task RefreshAsync_CancellationDuringInitialDispatch_CancelsDispatchedOperation()
    {
        string projectRoot = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "unity_refresh_cancel_" + Guid.NewGuid().ToString("N"));
        var pathResolver = new UnityPathResolver(projectRoot);
        var processManager = new StubProcessManager(pathResolver);
        var transport = new DispatchCancellationTransport { DelayRefresh = true };
        var poller = new RecordingOperationPoller();
        var client = new UnityClient(
            processManager,
            pathResolver,
            NullLogger<UnityClient>.Instance,
            transport,
            poller);

        using var cancellation = new CancellationTokenSource();
        Task<UnityRefreshResult> refreshTask = client.RefreshAsync(cancellationToken: cancellation.Token);

        await transport.RefreshDispatched.Task;
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await refreshTask);
        Assert.Equal(1, poller.CancellationCount);
        Assert.Equal("refresh", poller.LastKind);
        Assert.Equal(transport.OperationId, poller.LastOperationId);
    }

    private sealed class DispatchCancellationTransport : IUnitySocketTransport
    {
        public bool DelayRefresh { get; set; }

        private readonly TaskCompletionSource<string?> _refreshResponse =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> RefreshDispatched { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

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
            if (command.StartsWith("POLL_REFRESH ", StringComparison.Ordinal) ||
                command.StartsWith("POLL_REFRESH", StringComparison.Ordinal))
            {
                return Task.FromResult<string?>("READY");
            }

            if (command.StartsWith("REFRESH ", StringComparison.Ordinal) ||
                command.StartsWith("RECOMPILE ", StringComparison.Ordinal))
            {
                if (!DelayRefresh)
                {
                    return Task.FromResult<string?>("REFRESHING");
                }

                string[] parts = command.Split(' ', 2);
                OperationId = parts[1];
                RefreshDispatched.TrySetResult(true);
                cancellationToken.Register(() => _refreshResponse.TrySetCanceled(cancellationToken));
                return _refreshResponse.Task;
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
}
