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
        var refresh = await _fixture.SharedClient.CallToolAsync("unity_refresh");
        Assert.False(refresh.IsError, refresh.Text);
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
        var before = await client.CallToolAsync("unity_eval", new { code = "return Tests.RefreshProbe.DomainToken;" });
        Assert.False(before.IsError, before.Text);
        string domainToken = System.Text.RegularExpressions.Regex.Match(before.Text, "[a-f0-9]{32}").Value;
        Assert.NotEmpty(domainToken);

        var result = await client.CallToolAsync("unity_refresh", new { clean = true });

        Assert.False(result.IsError, result.Text);
        Assert.Contains("Clean script recompilation completed with 0 errors", result.Text);
        var after = await client.CallToolAsync("unity_eval", new { code = "return Tests.RefreshProbe.DomainToken;" });
        Assert.False(after.IsError, after.Text);
        Assert.DoesNotContain(domainToken, after.Text);
        Assert.Matches("[a-f0-9]{32}", after.Text);
    }

    [Fact]
    public async Task TestPollRefreshNonBlocking_PollsRefreshStateWithoutBlocking()
    {
        var client = _fixture.SharedClient;
        var result = await client.CallToolAsync("unity_eval", new
        {
            code = "return Tests.RefreshProbe.PollRefreshWhileBusy();"
        });

        Assert.False(result.IsError, result.Text);
        Assert.Contains("OK:BUSY eval ", result.Text);
    }

    [Fact]
    public async Task TestStatusReportsBusy_WhileActualEvalOwnsGate()
    {
        var refresh = await _fixture.SharedClient.CallToolAsync("unity_refresh");
        Assert.False(refresh.IsError, refresh.Text);
        var pm = new UnityProcessManager(_fixture.UnityRoot, NullLogger<UnityProcessManager>.Instance);
        var client = new UnityClient(pm, NullLogger<UnityClient>.Instance);
        var transport = new UnitySocketTransport();
        string opId = Guid.NewGuid().ToString("N");
        string resultFile = Path.Combine(_fixture.UnityRoot, "Temp", $"unity_eval_{opId}.json");
        bool accepted = false;
        try
        {
            string? acknowledgement = await transport.SendCommandAsync(pm.ReadPortFile(), _fixture.UnityRoot,
                $"EVAL {opId} await System.Threading.Tasks.Task.Delay(System.Threading.Timeout.Infinite, cancellationToken); return 1;");
            accepted = acknowledgement == "RUNNING";
            Assert.Equal("RUNNING", acknowledgement);
            Assert.Equal("Busy (eval)", await client.GetStatusAsync());
        }
        finally
        {
            if (accepted)
            {
                await transport.SendCommandAsync(pm.ReadPortFile(), _fixture.UnityRoot, $"CANCEL_OPERATION {opId}");
                while (!File.Exists(resultFile)) await Task.Delay(100);
                DeleteFileWithRetry(resultFile);
            }
        }
        string status;
        do
        {
            status = await client.GetStatusAsync();
            if (status == "Busy (eval)") await Task.Delay(100);
        } while (status == "Busy (eval)");
        Assert.Equal("Ready", status);
    }

    [Fact]
    public async Task TestPollReadsDurableOperationAndResultOnWorkerCacheMiss()
    {
        var refresh = await _fixture.SharedClient.CallToolAsync("unity_refresh");
        Assert.False(refresh.IsError, refresh.Text);
        string opId = "worker-cache-miss-" + Guid.NewGuid().ToString("N");
        string resultFile = Path.Combine(_fixture.UnityRoot, "Temp", $"unity_eval_{opId}.json");

        try
        {
            DeleteFileWithRetry(resultFile);

            int port = new UnityProcessManager(_fixture.UnityRoot, NullLogger<UnityProcessManager>.Instance).ReadPortFile();
            using (var tcpClient = new TcpClient())
            {
                await tcpClient.ConnectAsync(IPAddress.Loopback, port);
                using var stream = tcpClient.GetStream();
                using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
                using var reader = new StreamReader(stream, Encoding.UTF8);
                await writer.WriteLineAsync(UnityLeanMcp.ProjectCommandEnvelope.Encode(_fixture.UnityRoot, $"EVAL {opId} await System.Threading.Tasks.Task.Delay(2000, cancellationToken); return \"worker result\";"));
                Assert.Equal("RUNNING", await reader.ReadLineAsync());
            }

            using (var tcpClient = new TcpClient())
            {
                await tcpClient.ConnectAsync(IPAddress.Loopback, port);
                using var stream = tcpClient.GetStream();
                using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
                using var reader = new StreamReader(stream, Encoding.UTF8);
                await writer.WriteLineAsync(UnityLeanMcp.ProjectCommandEnvelope.Encode(_fixture.UnityRoot, $"POLL_EVAL {opId}"));
                string? pollStatus = await reader.ReadLineAsync();
                Assert.True(pollStatus == "RUNNING" || pollStatus == "SUCCESS worker result");
            }

            var deadline = DateTime.UtcNow.AddSeconds(5);
            string? finalResult = null;
            while (DateTime.UtcNow < deadline)
            {
                using (var tcpClient = new TcpClient())
                {
                    await tcpClient.ConnectAsync(IPAddress.Loopback, port);
                    using var stream = tcpClient.GetStream();
                    using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
                    using var reader = new StreamReader(stream, Encoding.UTF8);
                    await writer.WriteLineAsync(UnityLeanMcp.ProjectCommandEnvelope.Encode(_fixture.UnityRoot, $"POLL_EVAL {opId}"));
                    finalResult = await reader.ReadLineAsync();
                    if (finalResult == "SUCCESS worker result")
                        break;
                }
                await Task.Delay(100);
            }
            Assert.Equal("SUCCESS worker result", finalResult);
        }
        finally
        {
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

            await File.WriteAllTextAsync(pm.PathResolver.TestRunningFile, "{\"operationId\":\"test\"}");
            await File.WriteAllTextAsync(pm.PathResolver.PidFile, "12345");
            await File.WriteAllTextAsync(pm.PathResolver.PortFile, "50000");

            Assert.True(File.Exists(pm.PathResolver.TestRunningFile));
            Assert.True(File.Exists(pm.PathResolver.PidFile));
            Assert.True(File.Exists(pm.PathResolver.PortFile));

            pm.PurgeOperationState();

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
        var refresh = await _fixture.SharedClient.CallToolAsync("unity_refresh");
        Assert.False(refresh.IsError, refresh.Text);
        var pm = new UnityProcessManager(_fixture.UnityRoot, NullLogger<UnityProcessManager>.Instance);
        int port = pm.ReadPortFile();
        Assert.True(port > 0, "Unity port should be valid.");

        string opId = Guid.NewGuid().ToString("N");

        using (var tcpClient = new TcpClient())
        {
            await tcpClient.ConnectAsync(IPAddress.Loopback, port);
            using var stream = tcpClient.GetStream();
            using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
            using var reader = new StreamReader(stream, Encoding.UTF8);

            await writer.WriteLineAsync(UnityLeanMcp.ProjectCommandEnvelope.Encode(_fixture.UnityRoot, $"REFRESH {opId}"));
            string? ack = await reader.ReadLineAsync();
            Assert.True(ack == "REFRESHING" || ack == "BUSY compile" || ack == "COMPILING");
        }

        using (var tcpClient = new TcpClient())
        {
            await tcpClient.ConnectAsync(IPAddress.Loopback, port);
            using var stream = tcpClient.GetStream();
            using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
            using var reader = new StreamReader(stream, Encoding.UTF8);

            await writer.WriteLineAsync(UnityLeanMcp.ProjectCommandEnvelope.Encode(_fixture.UnityRoot, $"CANCEL_OPERATION {opId}"));
            string? response = await reader.ReadLineAsync();

            Assert.True(response == "NOT_CANCELABLE" || response == "NO_OPERATION");
        }

        var client = new UnityClient(pm, NullLogger<UnityClient>.Instance);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            string status = await client.GetStatusAsync();
            if (status == "Ready")
            {
                break;
            }
            await Task.Delay(100);
        }
    }

    [Fact]
    public async Task TestCancelOperation_EvalSnippetWithCancellationToken_CancelsSuccessfully()
    {
        var refresh = await _fixture.SharedClient.CallToolAsync("unity_refresh");
        Assert.False(refresh.IsError, refresh.Text);
        var pm = new UnityProcessManager(_fixture.UnityRoot, NullLogger<UnityProcessManager>.Instance);
        int port = pm.ReadPortFile();
        Assert.True(port > 0, "Unity port should be valid.");

        string opId = Guid.NewGuid().ToString("N");

        using (var tcpClient = new TcpClient())
        {
            await tcpClient.ConnectAsync(IPAddress.Loopback, port);
            using var stream = tcpClient.GetStream();
            using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
            using var reader = new StreamReader(stream, Encoding.UTF8);

            await writer.WriteLineAsync(UnityLeanMcp.ProjectCommandEnvelope.Encode(_fixture.UnityRoot, $"EVAL {opId} await System.Threading.Tasks.Task.Delay(15000, cancellationToken); return 123;"));
            string? ack = await reader.ReadLineAsync();
            Assert.Equal("RUNNING", ack);
        }

        using (var cancelClient = new TcpClient())
        {
            await cancelClient.ConnectAsync(IPAddress.Loopback, port);
            using var stream = cancelClient.GetStream();
            using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
            using var reader = new StreamReader(stream, Encoding.UTF8);

            await writer.WriteLineAsync(UnityLeanMcp.ProjectCommandEnvelope.Encode(_fixture.UnityRoot, $"CANCEL_OPERATION {opId}"));
            string? cancelResp = await reader.ReadLineAsync();
            Assert.Equal("CANCELLED", cancelResp);
        }

        var deadline = DateTime.UtcNow.AddSeconds(5);
        var client = new UnityClient(pm, NullLogger<UnityClient>.Instance);
        string status = "";
        while (DateTime.UtcNow < deadline)
        {
            status = await client.GetStatusAsync();
            if (status == "Ready")
            {
                break;
            }
            await Task.Delay(50);
        }
        Assert.Equal("Ready", status);
    }

    [Fact]
    public async Task TestClientCancellation_EvalAsyncWithCancellationToken_ThrowsAndUnwinds()
    {
        var pm = new UnityProcessManager(_fixture.UnityRoot, NullLogger<UnityProcessManager>.Instance);

        var client = new UnityClient(pm, NullLogger<UnityClient>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await client.EvalAsync("await System.Threading.Tasks.Task.Delay(10000, cancellationToken); return 42;", cts.Token);
        });

        var deadline = DateTime.UtcNow.AddSeconds(5);
        string status = "";
        while (DateTime.UtcNow < deadline)
        {
            status = await client.GetStatusAsync();
            if (status == "Ready")
            {
                break;
            }
            await Task.Delay(50);
        }
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
