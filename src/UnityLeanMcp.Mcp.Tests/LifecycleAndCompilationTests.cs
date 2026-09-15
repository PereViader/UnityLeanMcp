using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace UnityLeanMcp.Mcp.Tests;

[Collection("UnityIntegration")]
[Trait("Category", "UnityIntegration")]
public class LifecycleAndCompilationTests
{
    private readonly UnityIntegrationFixture _fixture;

    public LifecycleAndCompilationTests(UnityIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task TestBackgroundStatusOnline_ReturnsReadyWhenEditorIsRunning()
    {
        var pm = new UnityProcessManager(_fixture.UnityRoot, NullLogger<UnityProcessManager>.Instance);
        var client = new UnityClient(pm, NullLogger<UnityClient>.Instance);
        var result = await client.GetStatusAsync();

        Assert.Equal("Ready", result);
    }

    [Fact]
    public async Task TestRefresh_TriggersAssetDatabaseRefreshSuccessfully()
    {
        var client = _fixture.SharedClient;
        var result = await client.CallToolAsync("unity_refresh");

        Assert.False(result.IsError, result.Text);
        Assert.Contains("AssetDatabase refresh completed with 0 errors", result.Text);
    }

    [Fact]
    public async Task TestRecompile_TriggersCleanScriptRecompilationSuccessfully()
    {
        var client = _fixture.SharedClient;
        var result = await client.CallToolAsync("unity_refresh", new { clean = true });

        Assert.False(result.IsError, result.Text);
        Assert.Contains("Clean script recompilation completed with 0 errors", result.Text);
    }

    [Fact]
    public async Task TestPollRefreshNonBlocking_PollsRefreshStateWithoutBlocking()
    {
        var client = _fixture.SharedClient;
        var result = await client.CallToolAsync("unity_eval", new
        {
            code = "Tests.RefreshProbe.PollRefreshWhileBusy();"
        });

        Assert.False(result.IsError, result.Text);
    }

    [Fact]
    public async Task TestStatusReportsBusy_WhenOperationIsRecorded()
    {
        string operationFile = Path.Combine(_fixture.UnityRoot, "Temp", "unity_lean_mcp_operation.json");
        string opId = Guid.NewGuid().ToString("N");
        string testOperationJson = $"{{\"operationId\":\"{opId}\",\"kind\":\"eval\",\"status\":\"Running\",\"editorSessionId\":\"test\",\"startedUtc\":\"2026-09-08T12:00:00.0000000Z\",\"updatedUtc\":\"2026-09-08T12:00:00.0000000Z\"}}";

        try
        {
            WriteAtomic(operationFile, testOperationJson);

            var pm = new UnityProcessManager(_fixture.UnityRoot, NullLogger<UnityProcessManager>.Instance);
            var client = new UnityClient(pm, NullLogger<UnityClient>.Instance);
            var result = await client.GetStatusAsync();

            Assert.Contains("Busy (eval)", result);
        }
        finally
        {
            DeleteFileWithRetry(operationFile);
        }

        {
            var pm = new UnityProcessManager(_fixture.UnityRoot, NullLogger<UnityProcessManager>.Instance);
            var client = new UnityClient(pm, NullLogger<UnityClient>.Instance);
            var result = await client.GetStatusAsync();
            Assert.Contains("Ready", result);
        }
    }

    [Fact]
    public async Task TestPollReadsDurableOperationAndResultOnWorkerCacheMiss()
    {
        string operationFile = Path.Combine(_fixture.UnityRoot, "Temp", "unity_lean_mcp_operation.json");
        string opId = "worker-cache-miss-" + Guid.NewGuid().ToString("N");
        string resultFile = Path.Combine(_fixture.UnityRoot, "Temp", $"unity_eval_{opId}.json");
        string operationJson = $"{{\"operationId\":\"{opId}\",\"kind\":\"eval\",\"status\":\"Executing\",\"editorSessionId\":\"test\",\"startedUtc\":\"2026-09-08T12:00:00.0000000Z\",\"updatedUtc\":\"2026-09-08T12:00:00.0000000Z\"}}";
        string resultJson = $"{{\"operationId\":\"{opId}\",\"success\":true,\"payload\":\"worker result\"}}";

        try
        {
            DeleteFileWithRetry(operationFile);
            DeleteFileWithRetry(resultFile);
            WriteAtomic(operationFile, operationJson);

            int port = new UnityProcessManager(_fixture.UnityRoot, NullLogger<UnityProcessManager>.Instance).ReadPortFile();
            using (var tcpClient = new TcpClient())
            {
                await tcpClient.ConnectAsync(IPAddress.Loopback, port);
                using var stream = tcpClient.GetStream();
                using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
                using var reader = new StreamReader(stream, Encoding.UTF8);
                await writer.WriteLineAsync($"POLL_EVAL {opId}");
                Assert.Equal("RUNNING", await reader.ReadLineAsync());
            }

            WriteAtomic(resultFile, resultJson);
            using (var tcpClient = new TcpClient())
            {
                await tcpClient.ConnectAsync(IPAddress.Loopback, port);
                using var stream = tcpClient.GetStream();
                using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
                using var reader = new StreamReader(stream, Encoding.UTF8);
                await writer.WriteLineAsync($"POLL_EVAL {opId}");
                Assert.Equal("SUCCESS worker result", await reader.ReadLineAsync());
            }
        }
        finally
        {
            DeleteFileWithRetry(operationFile);
            DeleteFileWithRetry(resultFile);
        }
    }

    [Fact]
    public async Task TestStopUnity_PurgesOperationAndMarkerFiles()
    {
        string tempProject = Path.Combine(Path.GetTempPath(), "UnityLeanMcpTestPurge_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(tempProject, "Temp"));

        try
        {
            var pm = new UnityProcessManager(tempProject, NullLogger<UnityProcessManager>.Instance);

            await File.WriteAllTextAsync(pm.PathResolver.OperationFile, "{\"operationId\":\"test\"}");
            await File.WriteAllTextAsync(pm.PathResolver.TestRunningFile, "{\"operationId\":\"test\"}");
            await File.WriteAllTextAsync(pm.PathResolver.PidFile, "12345");
            await File.WriteAllTextAsync(pm.PathResolver.PortFile, "50000");

            Assert.True(File.Exists(pm.PathResolver.OperationFile));
            Assert.True(File.Exists(pm.PathResolver.TestRunningFile));
            Assert.True(File.Exists(pm.PathResolver.PidFile));
            Assert.True(File.Exists(pm.PathResolver.PortFile));

            pm.PurgeOperationState();

            Assert.False(File.Exists(pm.PathResolver.OperationFile), "OperationFile was not purged");
            Assert.False(File.Exists(pm.PathResolver.TestRunningFile), "TestRunningFile was not purged");
            Assert.False(File.Exists(pm.PathResolver.PidFile), "PidFile was not purged");
            Assert.False(File.Exists(pm.PathResolver.PortFile), "PortFile was not purged");
        }
        finally
        {
            try { Directory.Delete(tempProject, true); } catch { }
        }
    }

    [Fact]
    public async Task TestCancelOperation_NonCancelableOperation_ReturnsNotCancelableAndRemainsBusy()
    {
        var pm = new UnityProcessManager(_fixture.UnityRoot, NullLogger<UnityProcessManager>.Instance);
        int port = pm.ReadPortFile();
        Assert.True(port > 0, "Unity port should be valid.");

        string operationFile = pm.PathResolver.OperationFile;
        string opId = Guid.NewGuid().ToString("N");
        string testOperationJson = $"{{\"operationId\":\"{opId}\",\"kind\":\"recompile\",\"status\":\"Compiling\",\"editorSessionId\":\"test\",\"startedUtc\":\"{DateTime.UtcNow:o}\",\"updatedUtc\":\"{DateTime.UtcNow:o}\"}}";

        try
        {
            WriteAtomic(operationFile, testOperationJson);
            Assert.True(File.Exists(operationFile));

            using (var tcpClient = new TcpClient())
            {
                await tcpClient.ConnectAsync(IPAddress.Loopback, port);
                using var stream = tcpClient.GetStream();
                using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
                using var reader = new StreamReader(stream, Encoding.UTF8);

                await writer.WriteLineAsync($"CANCEL_OPERATION {opId}");
                string? response = await reader.ReadLineAsync();

                Assert.Equal("NOT_CANCELABLE", response);
            }

            Assert.True(File.Exists(operationFile), "Non-cancelable operation file must remain busy in the store.");
        }
        finally
        {
            DeleteFileWithRetry(operationFile);
        }
    }

    [Fact]
    public async Task TestCancelOperation_EvalSnippetWithCancellationToken_CancelsSuccessfully()
    {
        var pm = new UnityProcessManager(_fixture.UnityRoot, NullLogger<UnityProcessManager>.Instance);
        DeleteFileWithRetry(pm.PathResolver.OperationFile);
        int port = pm.ReadPortFile();
        Assert.True(port > 0, "Unity port should be valid.");

        string opId = Guid.NewGuid().ToString("N");

        using (var tcpClient = new TcpClient())
        {
            await tcpClient.ConnectAsync(IPAddress.Loopback, port);
            using var stream = tcpClient.GetStream();
            using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
            using var reader = new StreamReader(stream, Encoding.UTF8);

            await writer.WriteLineAsync($"EVAL {opId} await System.Threading.Tasks.Task.Delay(15000, cancellationToken); return 123;");
            string? ack = await reader.ReadLineAsync();
            Assert.Equal("RUNNING", ack);
        }

        using (var cancelClient = new TcpClient())
        {
            await cancelClient.ConnectAsync(IPAddress.Loopback, port);
            using var stream = cancelClient.GetStream();
            using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
            using var reader = new StreamReader(stream, Encoding.UTF8);

            await writer.WriteLineAsync($"CANCEL_OPERATION {opId}");
            string? cancelResp = await reader.ReadLineAsync();
            Assert.Equal("CANCELLED", cancelResp);
        }

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (File.Exists(pm.PathResolver.OperationFile) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }
        Assert.False(File.Exists(pm.PathResolver.OperationFile), "Operation file should be cleared when eval cancels.");

        var client = new UnityClient(pm, NullLogger<UnityClient>.Instance);
        string status = await client.GetStatusAsync();
        Assert.Equal("Ready", status);
    }

    [Fact]
    public async Task TestClientCancellation_EvalAsyncWithCancellationToken_ThrowsAndUnwinds()
    {
        var pm = new UnityProcessManager(_fixture.UnityRoot, NullLogger<UnityProcessManager>.Instance);
        DeleteFileWithRetry(pm.PathResolver.OperationFile);

        var client = new UnityClient(pm, NullLogger<UnityClient>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await client.EvalAsync("await System.Threading.Tasks.Task.Delay(10000, cancellationToken); return 42;", cts.Token);
        });

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (File.Exists(pm.PathResolver.OperationFile) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }
        Assert.False(File.Exists(pm.PathResolver.OperationFile), "Operation file should not remain after cancellation.");

        string status = await client.GetStatusAsync();
        Assert.Equal("Ready", status);
    }

    private static void WriteAtomic(string path, string content)
    {
        string dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        string tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(tempPath, content, new UTF8Encoding(false));
            for (int i = 0; i < 5; i++)
            {
                try
                {
                    if (File.Exists(path))
                    {
                        File.Replace(tempPath, path, null);
                    }
                    else
                    {
                        File.Move(tempPath, path);
                    }
                    return;
                }
                catch (IOException) when (i < 4)
                {
                    Thread.Sleep(10);
                }
                catch (UnauthorizedAccessException) when (i < 4)
                {
                    Thread.Sleep(10);
                }
            }
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { }
            }
        }
    }

    private static void DeleteFileWithRetry(string path, int maxRetries = 10, int delayMs = 50)
    {
        for (int i = 0; i < maxRetries; i++)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
                return;
            }
            catch (IOException) when (i < maxRetries - 1)
            {
                Thread.Sleep(delayMs);
            }
            catch (UnauthorizedAccessException) when (i < maxRetries - 1)
            {
                Thread.Sleep(delayMs);
            }
        }
        if (File.Exists(path))
        {
            try { File.Delete(path); } catch { }
        }
    }
}
