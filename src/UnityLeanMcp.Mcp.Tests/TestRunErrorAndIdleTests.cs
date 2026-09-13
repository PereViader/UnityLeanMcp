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

[Trait("Category", "Subsystem")]
public class TestRunErrorAndIdleTests
{
    private static (UnityClient client, UnityProcessManager procManager, TcpListener listener, string tempDir, Task serverTask) StartMockServer(
        Func<string, string?> handleCommand,
        CancellationToken cancellationToken)
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "unity_test_err_" + Guid.NewGuid().ToString("N"));
        string unityTemp = Path.Combine(tempDir, "Temp");
        Directory.CreateDirectory(unityTemp);

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        File.WriteAllText(Path.Combine(unityTemp, "unity_lean_mcp_port.txt"), port.ToString());
        File.WriteAllText(Path.Combine(unityTemp, "unity_lean_mcp_process.pid"), Environment.ProcessId.ToString());

        var procManager = new UnityProcessManager(tempDir, NullLogger<UnityProcessManager>.Instance);
        var client = new UnityClient(procManager, NullLogger<UnityClient>.Instance);

        var serverTask = Task.Run(async () =>
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient tcp;
                try { tcp = await listener.AcceptTcpClientAsync(cancellationToken); }
                catch { break; }

                using (tcp)
                using (var stream = tcp.GetStream())
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                using (var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true })
                {
                    string? line = await reader.ReadLineAsync(cancellationToken);
                    if (line == null) continue;

                    string? customResp = handleCommand(line);
                    if (customResp != null)
                    {
                        await writer.WriteLineAsync(customResp);
                    }
                    else if (line == "PING")
                    {
                        await writer.WriteLineAsync("PONG");
                    }
                    else if (line.StartsWith("POLL_REFRESH"))
                    {
                        await writer.WriteLineAsync("READY");
                    }
                    else if (line.StartsWith("REFRESH"))
                    {
                        await writer.WriteLineAsync("REFRESHING");
                    }
                }
            }
        }, cancellationToken);

        return (client, procManager, listener, tempDir, serverTask);
    }

    [Fact]
    public async Task UnityClient_RunTestsAsync_WhenInitialResponseIsError_ReturnsImmediatelyWithoutHanging()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (client, _, listener, tempDir, _) = StartMockServer(cmd =>
        {
            if (cmd.StartsWith("RUN_TESTS"))
            {
                return "ERROR: Missing or invalid operation id";
            }
            return null;
        }, cts.Token);

        try
        {
            var result = await client.RunTestsAsync(null, null, "editmode", null, cts.Token);

            Assert.False(result.Success);
            Assert.Contains("ERROR: Missing or invalid operation id", result.Message);
        }
        finally
        {
            listener.Stop();
            cts.Cancel();
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task UnityClient_RunTestsAsync_WhenInitialResponseIsFailure_ReturnsImmediatelyWithoutHanging()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (client, _, listener, tempDir, _) = StartMockServer(cmd =>
        {
            if (cmd.StartsWith("RUN_TESTS"))
            {
                return "FAILURE: Runner failed to start";
            }
            return null;
        }, cts.Token);

        try
        {
            var result = await client.RunTestsAsync(null, null, "editmode", null, cts.Token);

            Assert.False(result.Success);
            Assert.Contains("FAILURE: Runner failed to start", result.Message);
        }
        finally
        {
            listener.Stop();
            cts.Cancel();
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task UnityClient_RunTestsAsync_WhenPollResponseIsIdle_TerminatesImmediatelyWithFailure()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (client, _, listener, tempDir, _) = StartMockServer(cmd =>
        {
            if (cmd.StartsWith("RUN_TESTS"))
            {
                return "RUNNING";
            }
            if (cmd.StartsWith("POLL_TESTS"))
            {
                return "IDLE";
            }
            return null;
        }, cts.Token);

        try
        {
            var result = await client.RunTestsAsync(null, null, "editmode", null, cts.Token);

            Assert.False(result.Success);
            Assert.Contains("no longer recognized by the Editor (Editor is idle)", result.Message);
        }
        finally
        {
            listener.Stop();
            cts.Cancel();
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task UnityClient_RunTestsAsync_WhenPollResponseIsError_TerminatesImmediatelyWithFailure()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (client, _, listener, tempDir, _) = StartMockServer(cmd =>
        {
            if (cmd.StartsWith("RUN_TESTS"))
            {
                return "RUNNING";
            }
            if (cmd.StartsWith("POLL_TESTS"))
            {
                return "ERROR: Something went wrong";
            }
            return null;
        }, cts.Token);

        try
        {
            var result = await client.RunTestsAsync(null, null, "editmode", null, cts.Token);

            Assert.False(result.Success);
            Assert.Contains("ERROR: Something went wrong", result.Message);
        }
        finally
        {
            listener.Stop();
            cts.Cancel();
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task UnityClient_RunTestsAsync_WhenPollResponseIsBusy_TerminatesImmediatelyWithFailure()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (client, _, listener, tempDir, _) = StartMockServer(cmd =>
        {
            if (cmd.StartsWith("RUN_TESTS"))
            {
                return "RUNNING";
            }
            if (cmd.StartsWith("POLL_TESTS"))
            {
                return "BUSY execute foreign-op";
            }
            return null;
        }, cts.Token);

        try
        {
            var result = await client.RunTestsAsync(null, null, "editmode", null, cts.Token);

            Assert.False(result.Success);
            Assert.Contains("Lost ownership of test run: BUSY execute foreign-op", result.Message);
        }
        finally
        {
            listener.Stop();
            cts.Cancel();
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task UnityClient_EvalAsync_WhenPollResponseIsIdle_TerminatesWithFailure()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (client, _, listener, tempDir, _) = StartMockServer(cmd =>
        {
            if (cmd.StartsWith("EVAL"))
            {
                return "RUNNING";
            }
            if (cmd.StartsWith("POLL_EVAL"))
            {
                return "IDLE";
            }
            return null;
        }, cts.Token);

        try
        {
            var result = await client.EvalAsync("return 1 + 1;", cts.Token);

            Assert.False(result.Success);
            Assert.Contains("no longer recognized by the Editor (Editor is idle)", result.Message);
        }
        finally
        {
            listener.Stop();
            cts.Cancel();
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task UnityClient_ExecuteMethodAsync_WhenPollResponseIsIdle_TerminatesWithFailure()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (client, _, listener, tempDir, _) = StartMockServer(cmd =>
        {
            if (cmd.StartsWith("EXECUTE_METHOD"))
            {
                return "RUNNING";
            }
            if (cmd.StartsWith("POLL_EXECUTE"))
            {
                return "IDLE";
            }
            return null;
        }, cts.Token);

        try
        {
#pragma warning disable CS0618
            var result = await client.ExecuteMethodAsync("Namespace.Class.Method", null, cts.Token);
#pragma warning restore CS0618

            Assert.False(result.Success);
            Assert.Contains("no longer recognized by the Editor (Editor is idle)", result.Message);
        }
        finally
        {
            listener.Stop();
            cts.Cancel();
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task UnityClient_EvalAsync_WhenPollResponseIsIdleButResultFileExists_ReturnsResultSuccessfully()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        string capturedOpId = "";
        UnityProcessManager procManager = null!;
        var server = StartMockServer(cmd =>
        {
            if (cmd.StartsWith("EVAL"))
            {
                var parts = cmd.Split(' ', 3);
                if (parts.Length > 1)
                {
                    capturedOpId = parts[1];
                }
                return "RUNNING";
            }
            if (cmd.StartsWith("POLL_EVAL"))
            {
                // Simulate race condition: Editor finished evaluation, wrote result file, and returned IDLE
                var result = new UnityEvalResult
                {
                    OperationId = capturedOpId,
                    Success = true,
                    Payload = "42"
                };
                File.WriteAllText(procManager.GetEvalResultFile(capturedOpId), System.Text.Json.JsonSerializer.Serialize(result));
                return "IDLE";
            }
            return null;
        }, cts.Token);
        procManager = server.procManager;
        var client = server.client;
        var listener = server.listener;
        var tempDir = server.tempDir;

        try
        {
            var result = await client.EvalAsync("return 1 + 1;", cts.Token);

            Assert.True(result.Success);
            Assert.Equal("42", result.Payload);
        }
        finally
        {
            listener.Stop();
            cts.Cancel();
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task UnityClient_EvalAsync_WhenRefreshFails_AbortsAndReturnsCompilationError()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        bool evalInvoked = false;
        var (client, _, listener, tempDir, _) = StartMockServer(cmd =>
        {
            if (cmd.StartsWith("POLL_REFRESH"))
            {
                return "COMPILATION_ERROR";
            }
            if (cmd.StartsWith("EVAL"))
            {
                evalInvoked = true;
                return "RUNNING";
            }
            return null;
        }, cts.Token);

        try
        {
            var result = await client.EvalAsync("return 1 + 1;", cts.Token);

            Assert.False(result.Success);
            Assert.False(evalInvoked, "EVAL should not be invoked when pre-refresh compilation fails.");
            Assert.Contains("compilation", result.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            listener.Stop();
            cts.Cancel();
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task UnityClient_EvalAsync_WhenInitialResponseIsFailure_StripsPrefixAndParsesDiagnosticsCorrectly()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (client, _, listener, tempDir, _) = StartMockServer(cmd =>
        {
            if (cmd.StartsWith("EVAL"))
            {
                return "FAILURE eval(1,5): error CS0103: The name 'x' does not exist in the current context";
            }
            return null;
        }, cts.Token);

        try
        {
            var result = await client.EvalAsync("return x;", cts.Token);

            Assert.False(result.Success);
            Assert.DoesNotContain("FAILURE", result.Message);
            Assert.StartsWith("eval(1,5): error CS0103:", result.Message);

            var diags = DiagnosticFormatter.Default.ParseCompilerDiagnostics(result.Message);
            Assert.Single(diags);
            Assert.Equal("eval", diags[0].File);
            Assert.Equal(1, diags[0].Line);
            Assert.Equal(5, diags[0].Column);
            Assert.Equal("error", diags[0].Severity);
            Assert.Equal("CS0103", diags[0].Code);
            Assert.Equal("The name 'x' does not exist in the current context", diags[0].Message);
        }
        finally
        {
            listener.Stop();
            cts.Cancel();
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task UnityClient_EvalAsync_WhenInitialResponseIsError_StripsPrefixCorrectly()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (client, _, listener, tempDir, _) = StartMockServer(cmd =>
        {
            if (cmd.StartsWith("EVAL"))
            {
                return "ERROR: Missing operation id or code snippet";
            }
            return null;
        }, cts.Token);

        try
        {
            var result = await client.EvalAsync("return 1;", cts.Token);

            Assert.False(result.Success);
            Assert.DoesNotContain("ERROR:", result.Message);
            Assert.Equal("Missing operation id or code snippet", result.Message);
        }
        finally
        {
            listener.Stop();
            cts.Cancel();
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

#pragma warning disable CS0618
    [Fact]
    public async Task UnityClient_ExecuteMethodAsync_WhenInitialResponseIsFailureOrError_StripsPrefixCorrectly()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (client, _, listener, tempDir, _) = StartMockServer(cmd =>
        {
            if (cmd.StartsWith("EXECUTE_METHOD"))
            {
                return "FAILURE Compilation failed";
            }
            return null;
        }, cts.Token);

        try
        {
            var result = await client.ExecuteMethodAsync("TestClass.TestMethod", null, cts.Token);

            Assert.False(result.Success);
            Assert.DoesNotContain("FAILURE", result.Message);
            Assert.Equal("Compilation failed", result.Message);
        }
        finally
        {
            listener.Stop();
            cts.Cancel();
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }
#pragma warning restore CS0618
}
