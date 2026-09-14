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
        string tempDir = Path.Combine(Path.GetTempPath(), "unity_refresh_prog_" + Guid.NewGuid().ToString("N"));
        string unityTemp = Path.Combine(tempDir, "Temp");
        Directory.CreateDirectory(unityTemp);

        try
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;

            File.WriteAllText(Path.Combine(unityTemp, "unity_lean_mcp_port.txt"), port.ToString());
            TestProcessProvider.WriteTrustedPidFile(tempDir);

            var procManager = new UnityProcessManager(tempDir, NullLogger<UnityProcessManager>.Instance)
                .WithTrustedTestProcessProvider();
            var client = new UnityClient(procManager, NullLogger<UnityClient>.Instance);

            var receivedProgress = new List<ProgressNotificationValue>();
            var progress = new SynchronousProgress<ProgressNotificationValue>(p =>
            {
                lock (receivedProgress)
                {
                    receivedProgress.Add(p);
                }
            });

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            int pollCount = 0;
            bool refreshTriggered = false;

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
                        else if (line.StartsWith("REFRESH"))
                        {
                            refreshTriggered = true;
                            await writer.WriteLineAsync("REFRESHING");
                        }
                        else if (line.StartsWith("POLL_REFRESH"))
                        {
                            int count = Interlocked.Increment(ref pollCount);
                            if (count <= 1)
                            {
                                // Initial pre-check or first poll
                                await writer.WriteLineAsync("READY");
                            }
                            else if (count <= 3)
                            {
                                // Simulate compilation in progress
                                await writer.WriteLineAsync("COMPILING");
                            }
                            else
                            {
                                if (refreshTriggered && TestProcessProvider.TryGetRefreshOperationId(line, out string operationId))
                                {
                                    TestProcessProvider.WriteRefreshResult(procManager.PathResolver, operationId);
                                }

                                await writer.WriteLineAsync("READY");
                            }
                        }
                    }
                }
            }, cts.Token);

            var result = await client.RefreshAsync(isRecompile: false, progress, cts.Token);

            listener.Stop();
            cts.Cancel();

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
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task UnityClient_RecompileAsync_ReportsAllProgressPhases()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "unity_recompile_prog_" + Guid.NewGuid().ToString("N"));
        string unityTemp = Path.Combine(tempDir, "Temp");
        Directory.CreateDirectory(unityTemp);

        try
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;

            File.WriteAllText(Path.Combine(unityTemp, "unity_lean_mcp_port.txt"), port.ToString());
            TestProcessProvider.WriteTrustedPidFile(tempDir);

            var procManager = new UnityProcessManager(tempDir, NullLogger<UnityProcessManager>.Instance)
                .WithTrustedTestProcessProvider();
            var client = new UnityClient(procManager, NullLogger<UnityClient>.Instance);

            var receivedProgress = new List<ProgressNotificationValue>();
            var progress = new SynchronousProgress<ProgressNotificationValue>(p =>
            {
                lock (receivedProgress)
                {
                    receivedProgress.Add(p);
                }
            });

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            int pollCount = 0;
            bool refreshTriggered = false;

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
                        else if (line.StartsWith("RECOMPILE"))
                        {
                            refreshTriggered = true;
                            await writer.WriteLineAsync("RECOMPILING");
                        }
                        else if (line.StartsWith("POLL_REFRESH"))
                        {
                            int count = Interlocked.Increment(ref pollCount);
                            if (count <= 1)
                            {
                                await writer.WriteLineAsync("READY");
                            }
                            else if (count <= 3)
                            {
                                await writer.WriteLineAsync("COMPILING");
                            }
                            else
                            {
                                if (refreshTriggered && TestProcessProvider.TryGetRefreshOperationId(line, out string operationId))
                                {
                                    TestProcessProvider.WriteRefreshResult(procManager.PathResolver, operationId);
                                }

                                await writer.WriteLineAsync("READY");
                            }
                        }
                    }
                }
            }, cts.Token);

            var result = await client.RefreshAsync(isRecompile: true, progress, cts.Token);

            listener.Stop();
            cts.Cancel();

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
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task UnityClient_ExecuteMethodAsync_ReportsAllProgressPhases()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "unity_exec_prog_" + Guid.NewGuid().ToString("N"));
        string unityTemp = Path.Combine(tempDir, "Temp");
        Directory.CreateDirectory(unityTemp);

        try
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;

            File.WriteAllText(Path.Combine(unityTemp, "unity_lean_mcp_port.txt"), port.ToString());
            TestProcessProvider.WriteTrustedPidFile(tempDir);

            var procManager = new UnityProcessManager(tempDir, NullLogger<UnityProcessManager>.Instance)
                .WithTrustedTestProcessProvider();
            var client = new UnityClient(procManager, NullLogger<UnityClient>.Instance);

            var receivedProgress = new List<ProgressNotificationValue>();
            var progress = new SynchronousProgress<ProgressNotificationValue>(p =>
            {
                lock (receivedProgress)
                {
                    receivedProgress.Add(p);
                }
            });

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            bool refreshTriggered = false;

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
                            if (refreshTriggered && TestProcessProvider.TryGetRefreshOperationId(line, out string operationId))
                            {
                                TestProcessProvider.WriteRefreshResult(procManager.PathResolver, operationId);
                            }

                            await writer.WriteLineAsync("READY");
                        }
                        else if (line.StartsWith("REFRESH"))
                        {
                            refreshTriggered = true;
                            await writer.WriteLineAsync("REFRESHING");
                        }
                        else if (line.StartsWith("EXECUTE_METHOD"))
                        {
                            await writer.WriteLineAsync("RUNNING");
                        }
                        else if (line.StartsWith("POLL_EXECUTE"))
                        {
                            string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                            if (parts.Length > 1)
                            {
                                var result = new UnityExecuteResult
                                {
                                    OperationId = parts[1],
                                    Success = true,
                                    Message = "Method executed successfully"
                                };
                                File.WriteAllText(
                                    procManager.PathResolver.GetResultFilePath(UnityOperationKind.Execute, parts[1]),
                                    JsonSerializer.Serialize(result));
                            }

                            await writer.WriteLineAsync("SUCCESS Method executed successfully");
                        }
                    }
                }
            }, cts.Token);

#pragma warning disable CS0618
            var result = await client.ExecuteMethodAsync("Namespace.Type.Method", null, progress, cts.Token);
#pragma warning restore CS0618

            listener.Stop();
            cts.Cancel();

            Assert.True(result.Success);

            lock (receivedProgress)
            {
                Assert.NotEmpty(receivedProgress);

                // Phase 1 (0-40): Refreshing AssetDatabase prior to execution...
                Assert.Contains(receivedProgress, p =>
                    p.Progress >= 0 && p.Progress <= 40 &&
                    p.Message != null && p.Message.Contains("Refreshing AssetDatabase prior to execution"));

                // Phase 2 (50): Executing static method Namespace.Type.Method...
                Assert.Contains(receivedProgress, p =>
                    p.Progress == 50 &&
                    p.Message != null && p.Message.Contains("Executing static method Namespace.Type.Method"));

                // Phase 3 (100): Method execution completed.
                Assert.Contains(receivedProgress, p =>
                    p.Progress == 100 &&
                    p.Message != null && p.Message.Contains("Method execution completed"));
            }
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task McpServer_EmitsProgressNotifications_ForRefreshAndEval()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "unity_mcp_prog_all_" + Guid.NewGuid().ToString("N"));
        string unityTemp = Path.Combine(tempDir, "Temp");
        Directory.CreateDirectory(unityTemp);

        try
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;

            File.WriteAllText(Path.Combine(unityTemp, "unity_lean_mcp_port.txt"), port.ToString());
            TestProcessProvider.WriteTrustedPidFile(tempDir);

            Directory.CreateDirectory(Path.Combine(tempDir, "Assets"));
            Directory.CreateDirectory(Path.Combine(tempDir, "ProjectSettings"));
            var pathResolver = new UnityPathResolver(tempDir);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            bool refreshTriggered = false;

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
                            if (refreshTriggered && TestProcessProvider.TryGetRefreshOperationId(line, out string operationId))
                            {
                                TestProcessProvider.WriteRefreshResult(pathResolver, operationId);
                            }

                            await writer.WriteLineAsync("READY");
                        }
                        else if (line.StartsWith("REFRESH"))
                        {
                            refreshTriggered = true;
                            await writer.WriteLineAsync("REFRESHING");
                        }
                        else if (line.StartsWith("RECOMPILE"))
                        {
                            await writer.WriteLineAsync("RECOMPILING");
                        }
                        else if (line.StartsWith("EVAL"))
                        {
                            await writer.WriteLineAsync("RUNNING");
                        }
                        else if (line.StartsWith("POLL_EVAL"))
                        {
                            string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                            if (parts.Length > 1)
                            {
                                var result = new UnityEvalResult
                                {
                                    OperationId = parts[1],
                                    Success = true,
                                    Message = "Evaluation completed",
                                    Payload = "Hello from eval"
                                };
                                File.WriteAllText(
                                    pathResolver.GetResultFilePath(UnityOperationKind.Eval, parts[1]),
                                    JsonSerializer.Serialize(result));
                            }

                            await writer.WriteLineAsync("SUCCESS Hello from eval");
                        }
                    }
                }
            }, cts.Token);

            await using var client = new McpTestClient(tempDir);

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

            listener.Stop();
            cts.Cancel();
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }
}
