using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using Xunit;

namespace UnityLeanMcp.Mcp.Tests;

[Trait("Category", "Subsystem")]
public class ToolProgressTests
{
    private sealed class SynchronousProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;
        public SynchronousProgress(Action<T> handler) => _handler = handler;
        public void Report(T value) => _handler(value);
    }

    [Fact]
    public async Task UnityClient_RefreshAsync_ReportsAllProgressPhases()
    {
        var receivedProgress = new List<ProgressNotificationValue>();
        var progress = new SynchronousProgress<ProgressNotificationValue>(p =>
        {
            lock (receivedProgress)
            {
                receivedProgress.Add(p);
            }
        });

        int pollCount = 0;
        bool refreshTriggered = false;

        await using var server = await MockUnityServer.StartAsync((srv, line) =>
        {
            if (line.StartsWith("REFRESH"))
            {
                refreshTriggered = true;
                return "REFRESHING";
            }
            if (line.StartsWith("POLL_REFRESH"))
            {
                int count = Interlocked.Increment(ref pollCount);
                if (count <= 1)
                {
                    return "READY";
                }
                else if (count <= 3)
                {
                    return "COMPILING";
                }
                else
                {
                    if (refreshTriggered && TestProcessProvider.TryGetRefreshOperationId(line, out string operationId))
                    {
                        srv.WriteRefreshResult(operationId);
                    }
                    return "READY";
                }
            }
            return null;
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var result = await server.Client.RefreshAsync(isRecompile: false, progress, cts.Token);

        Assert.True(result.Success);

        lock (receivedProgress)
        {
            Assert.NotEmpty(receivedProgress);

            // Phase 1: Checking Unity Editor connection...
            Assert.Contains(receivedProgress, p =>
                p.Progress == 0 &&
                p.Message != null && p.Message.Contains("Checking Unity Editor connection"));

            // Phase 2: Triggering AssetDatabase refresh...
            Assert.Contains(receivedProgress, p =>
                p.Progress == 10 &&
                p.Message != null && p.Message.Contains("Triggering AssetDatabase refresh"));

            // Phase 3: Compiling script assemblies... (30-80)
            Assert.Contains(receivedProgress, p =>
                p.Progress >= 30 && p.Progress <= 80 &&
                p.Message != null && p.Message.Contains("Compiling script assemblies"));

            // Phase 4: Compilation finished, waiting for Editor to settle... (90)
            Assert.Contains(receivedProgress, p =>
                p.Progress == 90 &&
                p.Message != null && p.Message.Contains("waiting for Editor to settle"));

            // Phase 5: AssetDatabase refresh completed. (100)
            Assert.Contains(receivedProgress, p =>
                p.Progress == 100 &&
                p.Message != null && p.Message.Contains("AssetDatabase refresh completed"));
        }
    }

    [Fact]
    public async Task UnityClient_RecompileAsync_ReportsAllProgressPhases()
    {
        var receivedProgress = new List<ProgressNotificationValue>();
        var progress = new SynchronousProgress<ProgressNotificationValue>(p =>
        {
            lock (receivedProgress)
            {
                receivedProgress.Add(p);
            }
        });

        int pollCount = 0;
        bool refreshTriggered = false;

        await using var server = await MockUnityServer.StartAsync((srv, line) =>
        {
            if (line.StartsWith("RECOMPILE"))
            {
                refreshTriggered = true;
                return "RECOMPILING";
            }
            if (line.StartsWith("POLL_REFRESH"))
            {
                int count = Interlocked.Increment(ref pollCount);
                if (count <= 1)
                {
                    return "READY";
                }
                else if (count <= 3)
                {
                    return "COMPILING";
                }
                else
                {
                    if (refreshTriggered && TestProcessProvider.TryGetRefreshOperationId(line, out string operationId))
                    {
                        srv.WriteRefreshResult(operationId);
                    }
                    return "READY";
                }
            }
            return null;
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var result = await server.Client.RefreshAsync(isRecompile: true, progress, cts.Token);

        Assert.True(result.Success);

        lock (receivedProgress)
        {
            Assert.NotEmpty(receivedProgress);

            // Phase 1: Checking Unity Editor connection...
            Assert.Contains(receivedProgress, p =>
                p.Progress == 0 &&
                p.Message != null && p.Message.Contains("Checking Unity Editor connection"));

            // Phase 2: Triggering clean script recompilation...
            Assert.Contains(receivedProgress, p =>
                p.Progress == 10 &&
                p.Message != null && p.Message.Contains("clean script recompilation"));

            // Phase 3: Compiling script assemblies...
            Assert.Contains(receivedProgress, p =>
                p.Progress >= 30 && p.Progress <= 80 &&
                p.Message != null && p.Message.Contains("Compiling script assemblies"));

            // Phase 4: Compilation finished, waiting for Editor to settle...
            Assert.Contains(receivedProgress, p =>
                p.Progress == 90 &&
                p.Message != null && p.Message.Contains("waiting for Editor to settle"));

            // Phase 5: Clean script recompilation completed.
            Assert.Contains(receivedProgress, p =>
                p.Progress == 100 &&
                p.Message != null && p.Message.Contains("Clean script recompilation completed"));
        }
    }

    [Fact]
    public async Task McpServer_EmitsProgressNotifications_ForRefreshAndEval()
    {
        bool refreshTriggered = false;

        await using var server = await MockUnityServer.StartAsync((srv, line) =>
        {
            if (line.StartsWith("POLL_REFRESH"))
            {
                if (refreshTriggered && TestProcessProvider.TryGetRefreshOperationId(line, out string operationId))
                {
                    srv.WriteRefreshResult(operationId);
                }
                return "READY";
            }
            if (line.StartsWith("REFRESH"))
            {
                refreshTriggered = true;
                return "REFRESHING";
            }
            if (line.StartsWith("RECOMPILE"))
            {
                return "RECOMPILING";
            }
            if (line.StartsWith("EVAL"))
            {
                return "RUNNING";
            }
            if (line.StartsWith("POLL_EVAL"))
            {
                string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 1)
                {
                    srv.WriteEvalResult(parts[1], "Hello from eval");
                }
                return "SUCCESS Hello from eval";
            }
            return null;
        });

        await using var client = new McpTestClient(server.ProjectRoot, typeof(UnityClient).Assembly.Location);

        // Test 1: unity_refresh
        var refreshResult = await client.CallToolAsync(
            "unity_refresh",
            null,
            timeout: TimeSpan.FromSeconds(20),
            progressToken: "progress-refresh-token");

        Assert.False(refreshResult.IsError, refreshResult.Text);
        bool hasRefreshProgress = client.ReceivedNotifications.Exists(n =>
            n.TryGetProperty("method", out var m) && m.GetString() == "notifications/progress" &&
            n.TryGetProperty("params", out var p) && p.TryGetProperty("progressToken", out var tok) &&
            tok.GetString() == "progress-refresh-token");
        Assert.True(hasRefreshProgress, "Expected progress notification for unity_refresh");

        // Test 2: unity_refresh with clean = true
        var recompileResult = await client.CallToolAsync(
            "unity_refresh",
            new { clean = true },
            timeout: TimeSpan.FromSeconds(20),
            progressToken: "progress-recompile-token");

        Assert.False(recompileResult.IsError, recompileResult.Text);
        bool hasRecompileProgress = client.ReceivedNotifications.Exists(n =>
            n.TryGetProperty("method", out var m) && m.GetString() == "notifications/progress" &&
            n.TryGetProperty("params", out var p) && p.TryGetProperty("progressToken", out var tok) &&
            tok.GetString() == "progress-recompile-token");
        Assert.True(hasRecompileProgress, "Expected progress notification for unity_refresh with clean = true");

        // Test 3: unity_eval
        var evalResult = await client.CallToolAsync(
            "unity_eval",
            new { code = "1 + 1" },
            timeout: TimeSpan.FromSeconds(20),
            progressToken: "progress-eval-token");

        Assert.False(evalResult.IsError, evalResult.Text);
        bool hasEvalProgress = client.ReceivedNotifications.Exists(n =>
            n.TryGetProperty("method", out var m) && m.GetString() == "notifications/progress" &&
            n.TryGetProperty("params", out var p) && p.TryGetProperty("progressToken", out var tok) &&
            tok.GetString() == "progress-eval-token");
        Assert.True(hasEvalProgress, "Expected progress notification for unity_eval");
    }

    [Fact]
    public async Task UnityTools_UnityCoverageAsync_ForwardsProgressNotifications()
    {
        var receivedProgress = new List<ProgressNotificationValue>();
        var progress = new SynchronousProgress<ProgressNotificationValue>(p =>
        {
            lock (receivedProgress)
            {
                receivedProgress.Add(p);
            }
        });

        int coverageAttempts = 0;
        await using var server = await MockUnityServer.StartAsync((srv, line) =>
        {
            if (line.StartsWith("POLL_REFRESH"))
            {
                return null;
            }
            if (line.StartsWith("GET_COVERAGE"))
            {
                coverageAttempts++;
                if (coverageAttempts == 1)
                {
                    return "BUSY test op_wait_test";
                }
                return "SUCCESS {\"files\":[]}";
            }
            return null;
        });

        var tools = new UnityTools(server.Client, server.ProcessManager.PathResolver);
        var result = await tools.UnityCoverageAsync(new[] { "Assets/Scripts/Foo.cs" }, progress: progress, cancellationToken: CancellationToken.None);

        Assert.False(result.IsError);
        lock (receivedProgress)
        {
            Assert.Contains(receivedProgress, p => p.Message != null && p.Message.Contains("Waiting for active 'test'"));
        }
    }

    [Fact]
    public async Task UnityClient_RefreshAsync_ForwardsProgressToEnsureUnityRunningAsync()
    {
        var receivedProgress = new List<ProgressNotificationValue>();
        var progress = new SynchronousProgress<ProgressNotificationValue>(p =>
        {
            lock (receivedProgress)
            {
                receivedProgress.Add(p);
            }
        });

        string tempDir = Path.Combine(Path.GetTempPath(), "client_startup_progress_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var pathResolver = new UnityPathResolver(tempDir);
            var forwardingProcessManager = new ProgressEmittingProcessManager(pathResolver);
            var transport = new RefreshResultTransport(pathResolver);
            var poller = new OperationPoller(forwardingProcessManager, pathResolver, transport, NullLogger.Instance);
            var client = new UnityClient(
                forwardingProcessManager,
                pathResolver,
                NullLogger<UnityClient>.Instance,
                transport,
                poller);

            var result = await client.RefreshAsync(isRecompile: false, progress, CancellationToken.None);

            Assert.True(result.Success);
            lock (receivedProgress)
            {
                Assert.Contains(receivedProgress, p =>
                    p.Progress == 2 &&
                    p.Total == 100 &&
                    p.Message == "Starting Unity Editor (batchmode, PID 4242)...");
                Assert.Contains(receivedProgress, p =>
                    p.Progress == 3 &&
                    p.Total == 100 &&
                    p.Message == "Starting Unity Editor: Resolving packages...");
                Assert.All(receivedProgress, p => Assert.InRange(p.Progress, 0, 100));
            }
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    private sealed class ProgressEmittingProcessManager : IUnityProcessManager
    {
        public ProgressEmittingProcessManager(IUnityPathResolver pathResolver)
        {
            PathResolver = pathResolver;
        }

        public IUnityPathResolver PathResolver { get; }
        public IUnityExecutableLocator ExecutableLocator => throw new NotSupportedException();
        public bool IsUnityRunning(out int? processId) { processId = 4242; return true; }
        public string GetUnityMode(int? pid = null) => "Batchmode";
        public int ReadPortFile() => 12345;
        public Task<bool> StopUnityAsync(bool force = false, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public void PurgeOperationState() { }

        public Task EnsureUnityRunningAsync(IProgress<ProgressNotificationValue>? progress = null, CancellationToken cancellationToken = default)
        {
            progress?.Report(new ProgressNotificationValue
            {
                Progress = 2,
                Total = 100,
                Message = "Starting Unity Editor (batchmode, PID 4242)..."
            });
            progress?.Report(new ProgressNotificationValue
            {
                Progress = 3,
                Total = 100,
                Message = "Starting Unity Editor: Resolving packages..."
            });
            return Task.CompletedTask;
        }
    }

    private sealed class RefreshResultTransport : IUnitySocketTransport
    {
        private readonly IUnityPathResolver _pathResolver;

        public RefreshResultTransport(IUnityPathResolver pathResolver)
        {
            _pathResolver = pathResolver;
        }

        public Task<bool> IsSocketReadyAsync(int port, string projectRoot, int timeoutSeconds, CancellationToken cancellationToken) =>
            Task.FromResult(true);

        public Task<string?> SendCommandAsync(int port, string projectRoot, string command, int timeoutSeconds, CancellationToken cancellationToken)
        {
            if (command.StartsWith("REFRESH ", StringComparison.Ordinal))
            {
                string opId = command["REFRESH ".Length..];
                string resultFile = _pathResolver.GetResultFilePath(UnityOperationKind.Refresh, opId);
                Directory.CreateDirectory(Path.GetDirectoryName(resultFile)!);
                File.WriteAllText(resultFile, JsonSerializer.Serialize(new UnityRefreshResult
                {
                    OperationId = opId,
                    Success = true,
                    Message = "Refreshed"
                }));
                return Task.FromResult<string?>("REFRESHING");
            }

            if (command.StartsWith("POLL_REFRESH", StringComparison.Ordinal))
            {
                return Task.FromResult<string?>("READY");
            }

            return Task.FromResult<string?>(null);
        }
    }
}
