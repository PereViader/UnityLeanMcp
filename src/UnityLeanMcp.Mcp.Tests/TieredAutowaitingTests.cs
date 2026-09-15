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
public class TieredAutowaitingTests
{
    [Theory]
    [InlineData("COMPILING", true, true, "compile", null)]
    [InlineData("UPDATING", true, true, "compile", null)]
    [InlineData("BUSY compile", true, true, "compile", null)]
    [InlineData("BUSY refresh op1", true, true, "refresh", "op1")]
    [InlineData("BUSY recompile op2", true, true, "recompile", "op2")]
    [InlineData("BUSY reloading op3", true, true, "reloading", "op3")]
    [InlineData("BUSY updating op4", true, true, "updating", "op4")]
    [InlineData("BUSY test op5", true, false, "test", "op5")]
    [InlineData("BUSY eval op6", true, false, "eval", "op6")]
    [InlineData("BUSY test op7", true, false, "test", "op7")]
    [InlineData("BUSY unknown_op", true, false, "unknown_op", null)]
    [InlineData("BUSY", true, false, "unknown", null)]
    [InlineData("SUCCESS 42", false, false, null, null)]
    [InlineData("FAILURE something wrong", false, false, null, null)]
    [InlineData("", false, false, null, null)]
    [InlineData(null, false, false, null, null)]
    public void ParseBusyResponse_ParsesCorrectly(
        string? input,
        bool expectedIsBusy,
        bool expectedIsCompilation,
        string? expectedKind,
        string? expectedOpId)
    {
        var (isBusy, isCompilation, kind, opId) = UnityClient.ParseBusyResponse(input);
        Assert.Equal(expectedIsBusy, isBusy);
        Assert.Equal(expectedIsCompilation, isCompilation);
        Assert.Equal(expectedKind, kind);
        Assert.Equal(expectedOpId, opId);
    }

    [Fact]
    public void FormatBusyExecutingMessage_MatchesExactSpecification()
    {
        string msg = UnityClient.FormatBusyExecutingMessage("test", "abc12345");
        Assert.Equal("Unity is busy executing 'test' (id: abc12345). If this operation is hung, call unity_stop to recover.", msg);
    }

    [Fact]
    public async Task UnityClient_EvalAsync_WhenBusyCompile_AutowaitsAndSucceeds()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "unity_busy_compile_eval_" + Guid.NewGuid().ToString("N"));
        string unityTemp = Path.Combine(tempDir, "Temp");
        Directory.CreateDirectory(unityTemp);

        try
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;

            File.WriteAllText(Path.Combine(unityTemp, "unity_lean_mcp_port.txt"), port.ToString());
            File.WriteAllText(Path.Combine(unityTemp, "unity_lean_mcp_process.pid"), Environment.ProcessId.ToString());

            var procManager = new UnityProcessManager(tempDir, NullLogger<UnityProcessManager>.Instance)
                .WithTrustedTestProcessProvider();
            var client = new UnityClient(procManager, NullLogger<UnityClient>.Instance)
            {
                PollIntervalMs = 50
            };

            var receivedProgress = new List<ProgressNotificationValue>();
            var progress = new Progress<ProgressNotificationValue>(p =>
            {
                lock (receivedProgress)
                {
                    receivedProgress.Add(p);
                }
            });

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            int evalAttempts = 0;
            int compilePolls = 0;

            var serverTask = Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    TcpClient tcp;
                    try
                    {
                        tcp = await listener.AcceptTcpClientAsync(cts.Token);
                    }
                    catch
                    {
                        break;
                    }

                    using (tcp)
                    using (var stream = tcp.GetStream())
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                    using (var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true })
                    {
                        string? line = await reader.ReadLineAsync(cts.Token);
                        if (line == null) continue;

                        if (line == "PING")
                        {
                            await writer.WriteLineAsync("PONG");
                        }
                        else if (line.StartsWith("POLL_REFRESH"))
                        {
                            if (evalAttempts == 1)
                            {
                                if (compilePolls++ == 0)
                                {
                                    await writer.WriteLineAsync("COMPILING");
                                }
                                else
                                {
                                    if (TestProcessProvider.TryGetRefreshOperationId(line, out string operationId))
                                    {
                                        TestProcessProvider.WriteRefreshResult(procManager.PathResolver, operationId);
                                    }

                                    await writer.WriteLineAsync("READY");
                                }
                            }
                            else
                            {
                                await writer.WriteLineAsync("READY");
                            }
                        }
                        else if (line.StartsWith("REFRESH"))
                        {
                            string[] parts = line.Split(' ');
                            string operationId = parts.Length > 1 ? parts[1] : "refresh-op";
                            TestProcessProvider.WriteRefreshResult(procManager.PathResolver, operationId);
                            await writer.WriteLineAsync("REFRESHING");
                        }
                        else if (line.StartsWith("POLL_EVAL"))
                        {
                            await writer.WriteLineAsync("READY");
                        }
                        else if (line.StartsWith("EVAL"))
                        {
                            evalAttempts++;
                            if (evalAttempts == 1)
                            {
                                await writer.WriteLineAsync("BUSY compile");
                            }
                            else
                            {
                                string[] parts = line.Split(' ', 3);
                                string opId = parts.Length > 1 ? parts[1] : "eval-op";
                                var evalResult = new UnityEvalResult
                                {
                                    OperationId = opId,
                                    Success = true,
                                    Payload = "100"
                                };
                                string json = JsonSerializer.Serialize(evalResult);
                                await File.WriteAllTextAsync(Path.Combine(unityTemp, $"unity_eval_{opId}.json"), json, cts.Token);
                                await writer.WriteLineAsync($"SUCCESS 100");
                            }
                        }
                    }
                }
            });

            var result = await client.EvalAsync("return 50 + 50;", progress, cts.Token);

            Assert.True(result.Success, result.Message);
            Assert.Equal("100", result.Payload);
            Assert.Equal(2, evalAttempts);

            lock (receivedProgress)
            {
                Assert.Contains(receivedProgress, p => p.Message != null && p.Message.Contains("Unity is compiling script assemblies"));
            }

            cts.Cancel();
            listener.Stop();
            await Task.WhenAny(serverTask, Task.Delay(500));
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task UnityClient_EvalAsync_WhenBusyForeignOperation_GracePeriodExpires_ReturnsFailFastDiagnostic()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "unity_busy_foreign_eval_" + Guid.NewGuid().ToString("N"));
        string unityTemp = Path.Combine(tempDir, "Temp");
        Directory.CreateDirectory(unityTemp);

        try
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;

            File.WriteAllText(Path.Combine(unityTemp, "unity_lean_mcp_port.txt"), port.ToString());
            File.WriteAllText(Path.Combine(unityTemp, "unity_lean_mcp_process.pid"), Environment.ProcessId.ToString());

            var procManager = new UnityProcessManager(tempDir, NullLogger<UnityProcessManager>.Instance)
                .WithTrustedTestProcessProvider();
            var client = new UnityClient(procManager, NullLogger<UnityClient>.Instance)
            {
                PollIntervalMs = 20,
                BusyGracePeriod = TimeSpan.FromMilliseconds(100)
            };

            var receivedProgress = new List<ProgressNotificationValue>();
            var progress = new Progress<ProgressNotificationValue>(p =>
            {
                lock (receivedProgress)
                {
                    receivedProgress.Add(p);
                }
            });

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            int evalAttempts = 0;

            var serverTask = Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    TcpClient tcp;
                    try
                    {
                        tcp = await listener.AcceptTcpClientAsync(cts.Token);
                    }
                    catch
                    {
                        break;
                    }

                    using (tcp)
                    using (var stream = tcp.GetStream())
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                    using (var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true })
                    {
                        string? line = await reader.ReadLineAsync(cts.Token);
                        if (line == null) continue;

                        if (line == "PING")
                        {
                            await writer.WriteLineAsync("PONG");
                        }
                        else if (line.StartsWith("POLL_REFRESH"))
                        {
                            if (evalAttempts >= 1)
                            {
                                await writer.WriteLineAsync("BUSY test op_foreign_999");
                            }
                            else
                            {
                                await writer.WriteLineAsync("READY");
                            }
                        }
                        else if (line.StartsWith("REFRESH"))
                        {
                            string[] parts = line.Split(' ');
                            string operationId = parts.Length > 1 ? parts[1] : "refresh-op";
                            TestProcessProvider.WriteRefreshResult(procManager.PathResolver, operationId);
                            await writer.WriteLineAsync("REFRESHING");
                        }
                        else if (line.StartsWith("EVAL"))
                        {
                            evalAttempts++;
                            await writer.WriteLineAsync("BUSY test op_foreign_999");
                        }
                    }
                }
            });

            var result = await client.EvalAsync("return 1;", progress, cts.Token);

            Assert.False(result.Success);
            Assert.Contains("Unity is busy executing 'test' (id: op_foreign_999). If this operation is hung, call unity_stop to recover.", result.Message);

            lock (receivedProgress)
            {
                Assert.Contains(receivedProgress, p => p.Message != null && p.Message.Contains("Waiting for active 'test'"));
            }

            cts.Cancel();
            listener.Stop();
            await Task.WhenAny(serverTask, Task.Delay(500));
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task UnityClient_EvalAsync_WhenBusyForeignOperation_ClearsDuringGracePeriod_Succeeds()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "unity_busy_clears_eval_" + Guid.NewGuid().ToString("N"));
        string unityTemp = Path.Combine(tempDir, "Temp");
        Directory.CreateDirectory(unityTemp);

        try
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;

            File.WriteAllText(Path.Combine(unityTemp, "unity_lean_mcp_port.txt"), port.ToString());
            File.WriteAllText(Path.Combine(unityTemp, "unity_lean_mcp_process.pid"), Environment.ProcessId.ToString());

            var procManager = new UnityProcessManager(tempDir, NullLogger<UnityProcessManager>.Instance)
                .WithTrustedTestProcessProvider();
            var client = new UnityClient(procManager, NullLogger<UnityClient>.Instance)
            {
                PollIntervalMs = 50
            };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            int evalAttempts = 0;
            int gracePolls = 0;

            var serverTask = Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    TcpClient tcp;
                    try
                    {
                        tcp = await listener.AcceptTcpClientAsync(cts.Token);
                    }
                    catch
                    {
                        break;
                    }

                    using (tcp)
                    using (var stream = tcp.GetStream())
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                    using (var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true })
                    {
                        string? line = await reader.ReadLineAsync(cts.Token);
                        if (line == null) continue;

                        if (line == "PING")
                        {
                            await writer.WriteLineAsync("PONG");
                        }
                        else if (line.StartsWith("POLL_REFRESH"))
                        {
                            if (evalAttempts == 1)
                            {
                                if (gracePolls++ < 1)
                                {
                                    await writer.WriteLineAsync("BUSY test op_foreign");
                                }
                                else
                                {
                                    // Cleared during grace period
                                    await writer.WriteLineAsync("READY");
                                }
                            }
                            else
                            {
                                await writer.WriteLineAsync("READY");
                            }
                        }
                        else if (line.StartsWith("REFRESH"))
                        {
                            string[] parts = line.Split(' ');
                            string operationId = parts.Length > 1 ? parts[1] : "refresh-op";
                            TestProcessProvider.WriteRefreshResult(procManager.PathResolver, operationId);
                            await writer.WriteLineAsync("REFRESHING");
                        }
                        else if (line.StartsWith("POLL_EVAL"))
                        {
                            await writer.WriteLineAsync("READY");
                        }
                        else if (line.StartsWith("EVAL"))
                        {
                            evalAttempts++;
                            if (evalAttempts == 1)
                            {
                                await writer.WriteLineAsync("BUSY test op_foreign");
                            }
                            else
                            {
                                string[] parts = line.Split(' ', 3);
                                string opId = parts.Length > 1 ? parts[1] : "eval-op";
                                var evalResult = new UnityEvalResult
                                {
                                    OperationId = opId,
                                    Success = true,
                                    Payload = "cleared_ok"
                                };
                                string json = JsonSerializer.Serialize(evalResult);
                                await File.WriteAllTextAsync(Path.Combine(unityTemp, $"unity_eval_{opId}.json"), json, cts.Token);
                                await writer.WriteLineAsync("SUCCESS cleared_ok");
                            }
                        }
                    }
                }
            });

            var result = await client.EvalAsync("return \"cleared_ok\";", null, cts.Token);

            Assert.True(result.Success, result.Message);
            Assert.Equal("cleared_ok", result.Payload);
            Assert.Equal(2, evalAttempts);

            cts.Cancel();
            listener.Stop();
            await Task.WhenAny(serverTask, Task.Delay(500));
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task UnityClient_RunTestsAsync_WhenBusyCompile_AutowaitsAndSucceeds()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "unity_busy_compile_test_" + Guid.NewGuid().ToString("N"));
        string unityTemp = Path.Combine(tempDir, "Temp");
        Directory.CreateDirectory(unityTemp);

        try
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;

            File.WriteAllText(Path.Combine(unityTemp, "unity_lean_mcp_port.txt"), port.ToString());
            File.WriteAllText(Path.Combine(unityTemp, "unity_lean_mcp_process.pid"), Environment.ProcessId.ToString());

            var procManager = new UnityProcessManager(tempDir, NullLogger<UnityProcessManager>.Instance)
                .WithTrustedTestProcessProvider();
            var client = new UnityClient(procManager, NullLogger<UnityClient>.Instance)
            {
                PollIntervalMs = 50
            };

            var receivedProgress = new List<ProgressNotificationValue>();
            var progress = new Progress<ProgressNotificationValue>(p =>
            {
                lock (receivedProgress)
                {
                    receivedProgress.Add(p);
                }
            });

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            int runTestsAttempts = 0;
            int compilePolls = 0;

            var serverTask = Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    TcpClient tcp;
                    try
                    {
                        tcp = await listener.AcceptTcpClientAsync(cts.Token);
                    }
                    catch
                    {
                        break;
                    }

                    using (tcp)
                    using (var stream = tcp.GetStream())
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                    using (var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true })
                    {
                        string? line = await reader.ReadLineAsync(cts.Token);
                        if (line == null) continue;

                        if (line == "PING")
                        {
                            await writer.WriteLineAsync("PONG");
                        }
                        else if (line.StartsWith("POLL_REFRESH"))
                        {
                            if (runTestsAttempts == 1)
                            {
                                if (compilePolls++ == 0)
                                {
                                    await writer.WriteLineAsync("COMPILING");
                                }
                                else
                                {
                                    if (TestProcessProvider.TryGetRefreshOperationId(line, out string operationId))
                                    {
                                        TestProcessProvider.WriteRefreshResult(procManager.PathResolver, operationId);
                                    }

                                    await writer.WriteLineAsync("READY");
                                }
                            }
                            else
                            {
                                await writer.WriteLineAsync("READY");
                            }
                        }
                        else if (line.StartsWith("REFRESH"))
                        {
                            string[] parts = line.Split(' ');
                            string operationId = parts.Length > 1 ? parts[1] : "refresh-op";
                            TestProcessProvider.WriteRefreshResult(procManager.PathResolver, operationId);
                            await writer.WriteLineAsync("REFRESHING");
                        }
                        else if (line.StartsWith("POLL_TESTS"))
                        {
                            await writer.WriteLineAsync("READY");
                        }
                        else if (line.StartsWith("RUN_TESTS"))
                        {
                            runTestsAttempts++;
                            if (runTestsAttempts == 1)
                            {
                                await writer.WriteLineAsync("BUSY compile");
                            }
                            else
                            {
                                string[] parts = line.Split(' ');
                                string opId = parts.Length > 1 ? parts[1] : "test-op";
                                var testResult = new UnityTestRunResult
                                {
                                    RunId = opId,
                                    Success = true,
                                    PassCount = 5,
                                    FailCount = 0
                                };
                                string json = JsonSerializer.Serialize(testResult);
                                await File.WriteAllTextAsync(Path.Combine(unityTemp, $"unity_test_{opId}.json"), json, cts.Token);
                                await writer.WriteLineAsync($"SUCCESS {opId}");
                            }
                        }
                    }
                }
            });

            var result = await client.RunTestsAsync(null, null, null, null, "all", false, progress, cts.Token);

            Assert.True(result.Success, result.Message);
            Assert.Equal(5, result.PassCount);
            Assert.Equal(2, runTestsAttempts);

            lock (receivedProgress)
            {
                Assert.Contains(receivedProgress, p => p.Message != null && p.Message.Contains("Unity is compiling script assemblies"));
            }

            cts.Cancel();
            listener.Stop();
            await Task.WhenAny(serverTask, Task.Delay(500));
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task UnityClient_RunTestsAsync_WhenBusyForeignOperation_GracePeriodExpires_ReturnsFailFastDiagnostic()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "unity_busy_foreign_test_" + Guid.NewGuid().ToString("N"));
        string unityTemp = Path.Combine(tempDir, "Temp");
        Directory.CreateDirectory(unityTemp);

        try
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;

            File.WriteAllText(Path.Combine(unityTemp, "unity_lean_mcp_port.txt"), port.ToString());
            File.WriteAllText(Path.Combine(unityTemp, "unity_lean_mcp_process.pid"), Environment.ProcessId.ToString());

            var procManager = new UnityProcessManager(tempDir, NullLogger<UnityProcessManager>.Instance)
                .WithTrustedTestProcessProvider();
            var client = new UnityClient(procManager, NullLogger<UnityClient>.Instance)
            {
                PollIntervalMs = 20,
                BusyGracePeriod = TimeSpan.FromMilliseconds(100)
            };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            int runTestsAttempts = 0;

            var serverTask = Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    TcpClient tcp;
                    try
                    {
                        tcp = await listener.AcceptTcpClientAsync(cts.Token);
                    }
                    catch
                    {
                        break;
                    }

                    using (tcp)
                    using (var stream = tcp.GetStream())
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                    using (var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true })
                    {
                        string? line = await reader.ReadLineAsync(cts.Token);
                        if (line == null) continue;

                        if (line == "PING")
                        {
                            await writer.WriteLineAsync("PONG");
                        }
                        else if (line.StartsWith("POLL_REFRESH"))
                        {
                            if (runTestsAttempts >= 1)
                            {
                                await writer.WriteLineAsync("BUSY eval op_eval_777");
                            }
                            else
                            {
                                await writer.WriteLineAsync("READY");
                            }
                        }
                        else if (line.StartsWith("REFRESH"))
                        {
                            string[] parts = line.Split(' ');
                            string operationId = parts.Length > 1 ? parts[1] : "refresh-op";
                            TestProcessProvider.WriteRefreshResult(procManager.PathResolver, operationId);
                            await writer.WriteLineAsync("REFRESHING");
                        }
                        else if (line.StartsWith("RUN_TESTS"))
                        {
                            runTestsAttempts++;
                            await writer.WriteLineAsync("BUSY eval op_eval_777");
                        }
                    }
                }
            });

            var result = await client.RunTestsAsync(null, null, null, null, "all", false, null, cts.Token);

            Assert.False(result.Success);
            Assert.Contains("Unity is busy executing 'eval' (id: op_eval_777). If this operation is hung, call unity_stop to recover.", result.Message);

            cts.Cancel();
            listener.Stop();
            await Task.WhenAny(serverTask, Task.Delay(500));
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }
}
