using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace UnityLeanMcp.Mcp.Tests;

[Trait("Category", "Subsystem")]
public sealed class AutoRefreshEfficiencyTests
{
    [Fact]
    public async Task EvalAsync_WhenReadinessProbeIsReady_SkipsRefresh()
    {
        using var context = TestContext.Create("READY");

        var result = await context.Client.EvalAsync("return 42;", CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains(context.Transport.Commands, c => c.StartsWith("POLL_REFRESH CHECK ", StringComparison.Ordinal));
        Assert.DoesNotContain(context.Transport.Commands, c => c.StartsWith("REFRESH ", StringComparison.Ordinal));
        Assert.Contains(context.Transport.Commands, c => c.StartsWith("EVAL ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunTestsAsync_WhenReadinessProbeRequiresRefresh_RunsRefreshBeforeTests()
    {
        using var context = TestContext.Create("REFRESH_REQUIRED");

        var result = await context.Client.RunTestsAsync(
            testNames: null,
            groupNames: null,
            categoryNames: null,
            assemblyNames: null,
            mode: "editmode",
            cancellationToken: CancellationToken.None);

        Assert.True(result.Success);
        int refreshIndex = context.Transport.Commands.FindIndex(c => c.StartsWith("REFRESH ", StringComparison.Ordinal));
        int testIndex = context.Transport.Commands.FindIndex(c => c.StartsWith("RUN_TESTS ", StringComparison.Ordinal));
        Assert.True(refreshIndex >= 0);
        Assert.True(testIndex > refreshIndex);
    }

    [Fact]
    public async Task EvalAsync_WhenReadinessProbeReportsCompilation_WaitsWithoutSecondRefresh()
    {
        using var context = TestContext.Create("COMPILING");
        context.Transport.PendingCompilationPolls = 1;

        var result = await context.Client.EvalAsync("return 7;", CancellationToken.None);

        Assert.True(result.Success);
        Assert.DoesNotContain(context.Transport.Commands, c => c.StartsWith("REFRESH ", StringComparison.Ordinal));
        Assert.Contains(context.Transport.Commands, c => c.StartsWith("POLL_REFRESH ", StringComparison.Ordinal));
        Assert.Contains(context.Transport.Commands, c => c.StartsWith("EVAL ", StringComparison.Ordinal));
    }

    private sealed class TestContext : IDisposable
    {
        private readonly string _projectRoot;
        public RecordingTransport Transport { get; }
        public UnityClient Client { get; }

        private TestContext(string readiness)
        {
            _projectRoot = Path.Combine(Path.GetTempPath(), "unity_auto_refresh_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(_projectRoot, "Temp"));
            File.WriteAllText(Path.Combine(_projectRoot, "Temp", "unity_lean_mcp_port.txt"), "1");

            var resolver = new UnityPathResolver(_projectRoot);
            var processManager = new TestProcessManager(_projectRoot);
            Transport = new RecordingTransport(resolver, readiness);
            Client = new UnityClient(
                processManager,
                resolver,
                NullLogger<UnityClient>.Instance,
                Transport);
        }

        public static TestContext Create(string readiness) => new(readiness);

        public void Dispose()
        {
            try { Directory.Delete(_projectRoot, recursive: true); } catch { }
        }
    }

    private sealed class TestProcessManager : UnityProcessManager
    {
        public TestProcessManager(string projectRoot)
            : base(projectRoot, NullLogger<UnityProcessManager>.Instance)
        {
        }

        public override bool IsUnityRunning(out int? processId)
        {
            processId = 1;
            return true;
        }

        public override Task<bool> IsSocketReadyAsync(
            int timeoutSeconds = 2,
            CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private sealed class RecordingTransport : IUnitySocketTransport
    {
        private readonly IUnityPathResolver _resolver;
        private readonly string _readiness;
        public List<string> Commands { get; } = new();
        public int PendingCompilationPolls { get; set; }

        public RecordingTransport(IUnityPathResolver resolver, string readiness)
        {
            _resolver = resolver;
            _readiness = readiness;
        }

        public Task<string?> SendCommandAsync(
            int port,
            string command,
            int timeoutSeconds = 10,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command);

            if (command.StartsWith("POLL_REFRESH CHECK ", StringComparison.Ordinal))
            {
                return Task.FromResult<string?>(_readiness);
            }

            if (command.StartsWith("POLL_REFRESH ", StringComparison.Ordinal))
            {
                if (PendingCompilationPolls > 0)
                {
                    PendingCompilationPolls--;
                    return Task.FromResult<string?>("COMPILING");
                }

                return Task.FromResult<string?>("READY");
            }

            if (command.StartsWith("REFRESH ", StringComparison.Ordinal))
            {
                string operationId = command.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1];
                var refreshResult = new UnityRefreshResult
                {
                    OperationId = operationId,
                    Success = true,
                    Message = ""
                };
                File.WriteAllText(
                    _resolver.GetResultFilePath(UnityOperationKind.Refresh, operationId),
                    System.Text.Json.JsonSerializer.Serialize(refreshResult));
                return Task.FromResult<string?>("REFRESHING");
            }

            if (command.StartsWith("EVAL ", StringComparison.Ordinal))
            {
                string operationId = command.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1];
                var evalResult = new UnityEvalResult
                {
                    OperationId = operationId,
                    Success = true,
                    Payload = "42"
                };
                File.WriteAllText(
                    _resolver.GetResultFilePath(UnityOperationKind.Eval, operationId),
                    System.Text.Json.JsonSerializer.Serialize(evalResult));
                return Task.FromResult<string?>("RUNNING");
            }

            if (command.StartsWith("RUN_TESTS ", StringComparison.Ordinal))
            {
                string operationId = command.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1];
                var testResult = new UnityTestRunResult
                {
                    RunId = operationId,
                    Success = true,
                    ResultState = "Passed",
                    PassCount = 1
                };
                File.WriteAllText(
                    _resolver.GetResultFilePath(UnityOperationKind.Test, operationId),
                    System.Text.Json.JsonSerializer.Serialize(testResult));
                return Task.FromResult<string?>("RUNNING");
            }

            throw new InvalidOperationException($"Unexpected command: {command}");
        }

        public Task<bool> IsSocketReadyAsync(
            int port,
            int timeoutSeconds = 2,
            CancellationToken cancellationToken = default) => Task.FromResult(true);
    }
}
