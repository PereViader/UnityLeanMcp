using System;
using System.IO;
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
        Assert.Equal(CancellationToken.None, poller.LastCancellationToken);
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
        Assert.Equal(CancellationToken.None, poller.LastCancellationToken);
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
        Assert.Equal(CancellationToken.None, poller.LastCancellationToken);
    }

    [Theory]
    [InlineData("eval")]
    [InlineData("test")]
    public async Task LostAcknowledgement_CancellationBeforeRetry_CancelsSameOperation(string kind)
    {
        var paths = new UnityPathResolver(System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        using var cancellation = new CancellationTokenSource();
        var transport = new LostAcknowledgementTransport(cancellation);
        var poller = new RecordingOperationPoller();
        var client = new UnityClient(new StubProcessManager(paths), paths,
            NullLogger<UnityClient>.Instance, transport, poller);

        Task operation = kind == "eval"
            ? client.EvalAsync("return 42;", cancellation.Token)
            : client.RunTestsAsync(testNames: null, groupNames: null, categoryNames: null, assemblyNames: null, mode: "editmode", failedOnly: false, cancellationToken: cancellation.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.Equal(1, poller.CancellationCount);
        Assert.Equal(transport.OperationId, poller.LastOperationId);
        Assert.Equal(kind, poller.LastKind);
        Assert.Equal(CancellationToken.None, poller.LastCancellationToken);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelOperationAsync_TransientTransportFailure_RetriesUntilAcknowledged(bool throws)
    {
        var paths = new UnityPathResolver(System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        var transport = new RetryingCancellationTransport(throws);
        var poller = new OperationPoller(new StubProcessManager(paths), paths, transport);
        await poller.CancelOperationAsync("owned-id", "eval");
        Assert.Equal(2, transport.Attempts);
    }

    [Theory]
    [InlineData(UnityOperationKind.Eval, false)]
    [InlineData(UnityOperationKind.Eval, true)]
    [InlineData(UnityOperationKind.Test, false)]
    [InlineData(UnityOperationKind.Test, true)]
    [InlineData(UnityOperationKind.Refresh, false)]
    [InlineData(UnityOperationKind.Refresh, true)]
    [InlineData(UnityOperationKind.Recompile, false)]
    [InlineData(UnityOperationKind.Recompile, true)]
    public async Task CancelOperationAsync_NullSocketResponse_StopsOnlyForMatchingTerminalResult(
        UnityOperationKind kind, bool foreignResultExists)
    {
        string directory = Path.Combine(Path.GetTempPath(), "cancel_terminal_" + Guid.NewGuid().ToString("N"));
        var paths = new UnityPathResolver(directory);
        string resultPath = paths.GetResultFilePath(kind, "owned-id");
        Directory.CreateDirectory(Path.GetDirectoryName(resultPath)!);
        string idProperty = kind == UnityOperationKind.Test ? "runId" : "operationId";
        using var cancellation = new CancellationTokenSource();
        var transport = new HeldCancellationTransport();
        var poller = new OperationPoller(new StubProcessManager(paths, isRunning: true), paths, transport);
        Task? operation = null;
        try
        {
            if (foreignResultExists)
                File.WriteAllText(resultPath, $"{{\"{idProperty}\":\"foreign-id\",\"success\":true}}");

            operation = poller.CancelOperationAsync("owned-id", kind.ToString(), cancellation.Token);
            Task firstEvent = await Task.WhenAny(transport.Dispatched.Task, operation);
            Assert.Same(transport.Dispatched.Task, firstEvent);
            Assert.False(operation.IsCompleted);

            File.WriteAllText(resultPath, $"{{\"{idProperty}\":\"owned-id\",\"success\":true}}");
            transport.Response.SetResult(null);
            await operation;

            Assert.Equal(1, transport.Attempts);
            Assert.True(File.Exists(resultPath));
        }
        finally
        {
            cancellation.Cancel();
            transport.Response.TrySetResult(null);
            if (operation != null)
            {
                try { await operation; } catch { }
            }
            Directory.Delete(directory, true);
        }
    }

    private sealed class HeldCancellationTransport : IUnitySocketTransport
    {
        public TaskCompletionSource<bool> Dispatched { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<string?> Response { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Attempts { get; private set; }

        public Task<string?> SendCommandAsync(int port, string projectRoot, string command,
            int timeoutSeconds = 10, CancellationToken cancellationToken = default)
        {
            Assert.Equal("CANCEL_OPERATION owned-id", command);
            if (++Attempts > 1)
                throw new InvalidOperationException("Cancellation retried despite a matching terminal result.");
            Dispatched.SetResult(true);
            return Response.Task.WaitAsync(cancellationToken);
        }

        public Task<bool> IsSocketReadyAsync(int port, string projectRoot, int timeoutSeconds = 2,
            CancellationToken cancellationToken = default) => Task.FromResult(false);
    }

    private sealed class RetryingCancellationTransport(bool throws) : IUnitySocketTransport
    {
        public int Attempts { get; private set; }
        public Task<string?> SendCommandAsync(int port, string projectRoot, string command,
            int timeoutSeconds = 10, CancellationToken cancellationToken = default)
        {
            Assert.Equal("CANCEL_OPERATION owned-id", command);
            if (++Attempts == 1)
            {
                if (throws) throw new System.IO.IOException("simulated domain reload disconnect");
                return Task.FromResult<string?>(null);
            }
            return Task.FromResult<string?>("CANCELLED");
        }
        public Task<bool> IsSocketReadyAsync(int port, string projectRoot, int timeoutSeconds = 2,
            CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private sealed class LostAcknowledgementTransport(CancellationTokenSource cancellation) : IUnitySocketTransport
    {
        public string? OperationId { get; private set; }
        public Task<string?> SendCommandAsync(int port, string projectRoot, string command,
            int timeoutSeconds = 10, CancellationToken cancellationToken = default)
        {
            if (command.StartsWith("REFRESH ")) return Task.FromResult<string?>("REFRESHING");
            Assert.True(command.StartsWith("EVAL ") || command.StartsWith("RUN_TESTS "));
            OperationId = command.Split(' ')[1];
            // Admission happened, but no acknowledgement reached the caller. Cancel before
            // returning null: the next cancellable await is the dispatch retry delay.
            cancellation.Cancel();
            return Task.FromResult<string?>(null);
        }
        public Task<bool> IsSocketReadyAsync(int port, string projectRoot, int timeoutSeconds = 2,
            CancellationToken cancellationToken = default) => Task.FromResult(true);
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
            string projectRoot, string command,
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
            string projectRoot, int timeoutSeconds = 2,
            CancellationToken cancellationToken = default) => Task.FromResult(true);
    }
}
