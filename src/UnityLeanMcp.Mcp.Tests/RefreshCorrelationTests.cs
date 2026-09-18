using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace UnityLeanMcp.Mcp.Tests;

[Trait("Category", "Unit")]
public sealed class RefreshCorrelationTests
{
    [Fact]
    public async Task RefreshAsync_ReadyWithoutCorrelatedResult_DoesNotUseStaleStaticResultOrSynthesizeSuccess()
    {
        string tempDir = CreateTempProject();
        try
        {
            var processManager = new TestProcessManager(tempDir);
            string staticResultPath = processManager.PathResolver.GetResultFilePath(UnityOperationKind.Refresh);
            File.WriteAllText(staticResultPath, System.Text.Json.JsonSerializer.Serialize(new UnityRefreshResult
            {
                OperationId = "previous-operation",
                Success = true,
                Message = "stale result"
            }));

            var transport = new RecordingSocketTransport(command =>
            {
                if (command.StartsWith("POLL_REFRESH", StringComparison.Ordinal))
                {
                    return "IDLE";
                }

                if (command.StartsWith("REFRESH ", StringComparison.Ordinal))
                {
                    return "READY";
                }

                throw new InvalidOperationException($"Unexpected command: {command}");
            });
            var poller = new RecordingOperationPoller(async (spec, cancellationToken) =>
            {
                Assert.NotEqual(staticResultPath, spec.ResultFilePath);
                Assert.True(spec.ResultFilePath.Contains(spec.OperationId, StringComparison.Ordinal));
                Assert.Null(OperationPoller.TryReadJsonFile<UnityRefreshResult>(staticResultPath, spec.IsMatch));

                var customResult = await spec.CustomResponseHandler!("READY", cancellationToken);
                Assert.Null(customResult);
                return new UnityRefreshResult
                {
                    OperationId = spec.OperationId,
                    Success = false,
                    Message = "No correlated refresh result was published."
                };
            });

            var client = new UnityClient(
                processManager,
                processManager.PathResolver,
                NullLogger<UnityClient>.Instance,
                transport,
                poller);

            var result = await client.RefreshAsync(cancellationToken: CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal("No correlated refresh result was published.", result.Message);
            Assert.DoesNotContain("stale result", result.Message, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task RefreshAsync_ReadyWithOperationScopedResult_AcceptsResultAndPreservesDiagnostics()
    {
        string tempDir = CreateTempProject();
        try
        {
            var processManager = new TestProcessManager(tempDir);
            string staticResultPath = processManager.PathResolver.GetResultFilePath(UnityOperationKind.Refresh);
            File.WriteAllText(staticResultPath, System.Text.Json.JsonSerializer.Serialize(new UnityRefreshResult
            {
                OperationId = "previous-operation",
                Success = true,
                Message = "stale result"
            }));
            File.WriteAllText(
                processManager.PathResolver.CompilationErrorsFile,
                "Assets/Current.cs(4,2): warning CS0168: current diagnostic");

            var poller = new RecordingOperationPoller(async (spec, cancellationToken) =>
            {
                var currentResult = new UnityRefreshResult
                {
                    OperationId = spec.OperationId,
                    Success = true,
                    Message = "current result"
                };
                File.WriteAllText(spec.ResultFilePath, System.Text.Json.JsonSerializer.Serialize(currentResult));

                var customResult = await spec.CustomResponseHandler!("READY", cancellationToken);
                Assert.NotNull(customResult);
                Assert.Equal(spec.OperationId, customResult.OperationId);

                var completedResult = spec.OnResultFound!(customResult!);
                Assert.Equal(spec.OperationId, completedResult.OperationId);
                return completedResult;
            });

            var client = new UnityClient(
                processManager,
                processManager.PathResolver,
                NullLogger<UnityClient>.Instance,
                new StubSocketTransport(),
                poller);

            var result = await client.RefreshAsync(cancellationToken: CancellationToken.None);

            Assert.True(result.Success);
            Assert.Contains("current diagnostic", result.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("stale result", result.Message, StringComparison.Ordinal);
            Assert.True(poller.ResultFilePath!.Contains(poller.OperationId!, StringComparison.Ordinal));
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task RefreshAsync_WhenPollResponseIsIdle_ReturnsFailureWithoutCancellation()
    {
        string tempDir = CreateTempProject();
        try
        {
            var processManager = new TestProcessManager(tempDir);
            var transport = new RecordingSocketTransport(command =>
            {
                if (command.StartsWith("POLL_REFRESH", StringComparison.Ordinal))
                {
                    return "IDLE";
                }

                if (command.StartsWith("REFRESH ", StringComparison.Ordinal))
                {
                    return "REFRESHING";
                }

                throw new InvalidOperationException($"Unexpected command: {command}");
            });
            var poller = new OperationPoller(
                processManager,
                processManager.PathResolver,
                transport,
                NullLogger<OperationPoller>.Instance);
            var client = new UnityClient(
                processManager,
                processManager.PathResolver,
                NullLogger<UnityClient>.Instance,
                transport,
                poller);

            var result = await client.RefreshAsync(cancellationToken: CancellationToken.None);

            Assert.False(result.Success);
            Assert.Contains("no longer recognized by the Editor (Editor is idle)", result.Message, StringComparison.Ordinal);
            Assert.True(
                transport.Commands.FindAll(c => c.StartsWith("POLL_REFRESH", StringComparison.Ordinal)).Count == 1,
                "Refresh should poll once before returning the idle failure.");
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Theory]
    [InlineData("ERROR: Refresh could not start", "Refresh could not start")]
    [InlineData("FAILURE Refresh could not start", "Refresh could not start")]
    public async Task RefreshAsync_WhenInitialResponseIsErrorOrFailure_ReturnsImmediately(
        string initialResponse,
        string expectedMessage)
    {
        string tempDir = CreateTempProject();
        try
        {
            var processManager = new TestProcessManager(tempDir);
            var transport = new RecordingSocketTransport(command =>
            {
                if (command.StartsWith("POLL_REFRESH", StringComparison.Ordinal))
                {
                    return "IDLE";
                }

                if (command.StartsWith("REFRESH ", StringComparison.Ordinal))
                {
                    return initialResponse;
                }

                throw new InvalidOperationException($"Unexpected command: {command}");
            });
            var poller = new OperationPoller(
                processManager,
                processManager.PathResolver,
                transport,
                NullLogger<OperationPoller>.Instance);
            var client = new UnityClient(
                processManager,
                processManager.PathResolver,
                NullLogger<UnityClient>.Instance,
                transport,
                poller);

            var result = await client.RefreshAsync(cancellationToken: CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal(expectedMessage, result.Message);
            Assert.True(
                transport.Commands.FindAll(c => c.StartsWith("POLL_REFRESH", StringComparison.Ordinal)).Count == 0,
                "An initial terminal response must not trigger a refresh poll.");
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Theory]
    [InlineData("ERROR: Refresh polling failed", "Refresh polling failed", false)]
    [InlineData("FAILURE Refresh polling failed", "Refresh polling failed", false)]
    [InlineData("INTERRUPTION Refresh was interrupted", "Refresh was interrupted", true)]
    public async Task RefreshAsync_WhenPollResponseIsExplicitTerminalOutcome_ReturnsImmediately(
        string pollResponse,
        string expectedMessage,
        bool expectedInterrupted)
    {
        string tempDir = CreateTempProject();
        try
        {
            var processManager = new TestProcessManager(tempDir);
            var transport = new RecordingSocketTransport(command =>
            {
                if (command.StartsWith("POLL_REFRESH", StringComparison.Ordinal))
                {
                    return pollResponse;
                }

                if (command.StartsWith("REFRESH ", StringComparison.Ordinal))
                {
                    return "REFRESHING";
                }

                throw new InvalidOperationException($"Unexpected command: {command}");
            });
            var poller = new OperationPoller(
                processManager,
                processManager.PathResolver,
                transport,
                NullLogger<OperationPoller>.Instance);
            var client = new UnityClient(
                processManager,
                processManager.PathResolver,
                NullLogger<UnityClient>.Instance,
                transport,
                poller);

            var result = await client.RefreshAsync(cancellationToken: CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal(expectedMessage, result.Message);
            Assert.Equal(expectedInterrupted, result.Interrupted);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    private static string CreateTempProject()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "unity_refresh_correlation_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(tempDir, "Temp"));
        File.WriteAllText(Path.Combine(tempDir, "Temp", "unity_lean_mcp_port.txt"), "1");
        return tempDir;
    }

    private sealed class TestProcessManager : UnityProcessManager
    {
        public TestProcessManager(string projectRoot)
            : base(projectRoot, NullLogger<UnityProcessManager>.Instance)
        {
        }

        public override bool IsUnityRunning(out int? processId)
        {
            processId = null;
            return true;
        }

        public override Task<bool> IsSocketReadyAsync(
            int timeoutSeconds = 2,
            CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private sealed class StubSocketTransport : IUnitySocketTransport
    {
        public Task<string?> SendCommandAsync(
            int port,
            string command,
            int timeoutSeconds = 10,
            CancellationToken cancellationToken = default)
        {
            if (command.StartsWith("POLL_REFRESH", StringComparison.Ordinal))
            {
                return Task.FromResult<string?>("IDLE");
            }

            if (command.StartsWith("REFRESH", StringComparison.Ordinal))
            {
                return Task.FromResult<string?>("REFRESHING");
            }

            throw new InvalidOperationException($"Unexpected command: {command}");
        }

        public Task<bool> IsSocketReadyAsync(
            int port,
            int timeoutSeconds = 2,
            CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private sealed class RecordingSocketTransport : IUnitySocketTransport
    {
        private readonly Func<string, string?> _responseFactory;

        public RecordingSocketTransport(Func<string, string?> responseFactory)
        {
            _responseFactory = responseFactory;
        }

        public List<string> Commands { get; } = new();

        public Task<string?> SendCommandAsync(
            int port,
            string command,
            int timeoutSeconds = 10,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command);
            return Task.FromResult(_responseFactory(command));
        }

        public Task<bool> IsSocketReadyAsync(
            int port,
            int timeoutSeconds = 2,
            CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private sealed class RecordingOperationPoller : IOperationPoller
    {
        private readonly Func<OperationPollingSpec<UnityRefreshResult>, CancellationToken, Task<UnityRefreshResult>> _refresh;

        public RecordingOperationPoller(Func<OperationPollingSpec<UnityRefreshResult>, CancellationToken, Task<UnityRefreshResult>> refresh)
        {
            _refresh = refresh;
        }

        public string? OperationId { get; private set; }
        public string? ResultFilePath { get; private set; }

        public async Task<TResult> PollOperationUntilTerminalAsync<TResult>(
            OperationPollingSpec<TResult> spec,
            CancellationToken cancellationToken) where TResult : class, IOperationResult, new()
        {
            if (typeof(TResult) != typeof(UnityRefreshResult))
            {
                throw new InvalidOperationException("This test poller only supports refresh results.");
            }

            var refreshSpec = (OperationPollingSpec<UnityRefreshResult>)(object)spec;
            OperationId = refreshSpec.OperationId;
            ResultFilePath = refreshSpec.ResultFilePath;
            Assert.True(refreshSpec.RequireDurableResult);
            return (TResult)(object)await _refresh(refreshSpec, cancellationToken);
        }

        public Task<TResult> PollOperationResultAsync<TResult>(
            string opId,
            string kind,
            string operationDisplayName,
            string resultFilePath,
            string pollCommand,
            int pollIntervalMs = 500,
            Func<TResult, TResult>? onResultFound = null,
            CancellationToken cancellationToken = default) where TResult : UnityOperationResult, new() =>
            Task.FromException<TResult>(new NotSupportedException());

        public Task CancelOperationAsync(string opId, string kind) => Task.CompletedTask;
    }
}
