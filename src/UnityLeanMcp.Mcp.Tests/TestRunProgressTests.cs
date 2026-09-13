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
public class TestRunProgressTests
{
    [Fact]
    public async Task UnityClient_RunTestsAsync_ReportsProgressAccurately()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "unity_test_progress_" + Guid.NewGuid().ToString("N"));
        string unityTemp = Path.Combine(tempDir, "Temp");
        Directory.CreateDirectory(unityTemp);

        try
        {
            // 1. Start local mock TCP server for Unity Editor loopback
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;

            File.WriteAllText(Path.Combine(unityTemp, "unity_lean_mcp_port.txt"), port.ToString());
            File.WriteAllText(Path.Combine(unityTemp, "unity_lean_mcp_process.pid"), Environment.ProcessId.ToString());

            var logger = NullLogger<UnityProcessManager>.Instance;
            var procManager = new UnityProcessManager(tempDir, logger);
            var clientLogger = NullLogger<UnityClient>.Instance;
            var client = new UnityClient(procManager, clientLogger)
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
            int pollCount = 0;

            // Run mock server handling socket commands in background
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
                            await writer.WriteLineAsync("READY");
                        }
                        else if (line.StartsWith("REFRESH"))
                        {
                            await writer.WriteLineAsync("REFRESHING");
                        }
                        else if (line.StartsWith("RUN_TESTS"))
                        {
                            string[] parts = line.Split(' ');
                            string opId = parts.Length > 1 ? parts[1] : "test-op";

                            var state1 = new UnityTestRunState
                            {
                                RunId = opId,
                                Status = "Running",
                                TotalTests = 10,
                                CompletedTests = 2,
                                PassCount = 2,
                                FailCount = 0,
                                CurrentTestName = "Suite.TestAlpha"
                            };
                            File.WriteAllText(procManager.PathResolver.TestRunningFile, JsonSerializer.Serialize(state1));

                            await writer.WriteLineAsync("RUNNING");
                        }
                        else if (line.StartsWith("POLL_TESTS"))
                        {
                            string[] parts = line.Split(' ');
                            string opId = parts.Length > 1 ? parts[1] : "test-op";
                            pollCount++;

                            if (pollCount == 1)
                            {
                                var state2 = new UnityTestRunState
                                {
                                    RunId = opId,
                                    Status = "Running",
                                    TotalTests = 10,
                                    CompletedTests = 6,
                                    PassCount = 5,
                                    FailCount = 1,
                                    CurrentTestName = "Suite.TestBeta"
                                };
                                File.WriteAllText(procManager.PathResolver.TestRunningFile, JsonSerializer.Serialize(state2));
                                await writer.WriteLineAsync("RUNNING");
                            }
                            else
                            {
                                var result = new UnityTestRunResult
                                {
                                    RunId = opId,
                                    Success = true,
                                    PassCount = 9,
                                    FailCount = 1,
                                    SkipCount = 0,
                                    ResultState = "Passed"
                                };
                                File.WriteAllText(procManager.PathResolver.GetResultFilePath(UnityOperationKind.Test, opId), JsonSerializer.Serialize(result));
                                await writer.WriteLineAsync("SUCCESS 9 passed");
                            }
                        }
                    }
                }
            }, cts.Token);

            var runResult = await client.RunTestsAsync(null, null, "editmode", progress, cts.Token);

            listener.Stop();
            cts.Cancel();

            Assert.True(runResult.Success);
            Assert.Equal(9, runResult.PassCount);
            Assert.Equal(1, runResult.FailCount);

            // Verify progress reports
            lock (receivedProgress)
            {
                Assert.NotEmpty(receivedProgress);
                Assert.Contains(receivedProgress, p => p.Message != null && p.Message.Contains("Checking compilation"));
                Assert.Contains(receivedProgress, p => p.Message != null && p.Message.Contains("Initializing editmode test run"));
                Assert.Contains(receivedProgress, p => p.Message != null && p.Message.Contains("Suite.TestAlpha"));
                Assert.Contains(receivedProgress, p => p.Message != null && p.Message.Contains("Suite.TestBeta"));
                Assert.Contains(receivedProgress, p => p.Message != null && p.Message.Contains("Tests finished:"));
            }
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task McpServer_EmitsProgressNotifications_WhenProgressTokenProvided()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "unity_mcp_progress_" + Guid.NewGuid().ToString("N"));
        string unityTemp = Path.Combine(tempDir, "Temp");
        Directory.CreateDirectory(unityTemp);

        try
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;

            File.WriteAllText(Path.Combine(unityTemp, "unity_lean_mcp_port.txt"), port.ToString());
            File.WriteAllText(Path.Combine(unityTemp, "unity_lean_mcp_process.pid"), Environment.ProcessId.ToString());

            // Fake Unity project indicators
            Directory.CreateDirectory(Path.Combine(tempDir, "Assets"));
            Directory.CreateDirectory(Path.Combine(tempDir, "ProjectSettings"));

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));

            int pollCount = 0;

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
                            await writer.WriteLineAsync("READY");
                        }
                        else if (line.StartsWith("RUN_TESTS"))
                        {
                            string[] parts = line.Split(' ');
                            string opId = parts.Length > 1 ? parts[1] : "test-op";

                            var state = new UnityTestRunState
                            {
                                RunId = opId,
                                Status = "Running",
                                TotalTests = 5,
                                CompletedTests = 3,
                                PassCount = 3,
                                FailCount = 0,
                                CurrentTestName = "SampleTest.TestMethod"
                            };
                            File.WriteAllText(Path.Combine(unityTemp, "unity_test_running.txt"), JsonSerializer.Serialize(state));

                            await writer.WriteLineAsync("RUNNING");
                        }
                        else if (line.StartsWith("POLL_TESTS"))
                        {
                            string[] parts = line.Split(' ');
                            string opId = parts.Length > 1 ? parts[1] : "test-op";
                            pollCount++;

                            if (pollCount == 1)
                            {
                                var result = new UnityTestRunResult
                                {
                                    RunId = opId,
                                    Success = true,
                                    PassCount = 5,
                                    FailCount = 0,
                                    SkipCount = 0,
                                    ResultState = "Passed"
                                };
                                File.WriteAllText(Path.Combine(unityTemp, $"unity_test_{opId}.json"), JsonSerializer.Serialize(result));
                                await writer.WriteLineAsync("RUNNING");
                            }
                            else
                            {
                                await writer.WriteLineAsync("SUCCESS 5 passed");
                            }
                        }
                    }
                }
            }, cts.Token);

            await using var client = new McpTestClient(tempDir);
            var toolResult = await client.CallToolAsync(
                "unity_run_tests",
                new { mode = "editmode" },
                timeout: TimeSpan.FromSeconds(20),
                progressToken: "progress-token-test-1");

            listener.Stop();
            cts.Cancel();

            Assert.False(toolResult.IsError, toolResult.Text);
            Assert.Contains("Tests Passed: 5 passed", toolResult.Text);

            // Check that notifications/progress was received
            Assert.NotEmpty(client.ReceivedNotifications);
            bool hasProgressNotification = false;
            foreach (var notif in client.ReceivedNotifications)
            {
                if (notif.TryGetProperty("method", out var m) && m.GetString() == "notifications/progress")
                {
                    if (notif.TryGetProperty("params", out var p) &&
                        p.TryGetProperty("progressToken", out var tok) &&
                        tok.GetString() == "progress-token-test-1")
                    {
                        hasProgressNotification = true;
                        break;
                    }
                }
            }

            Assert.True(hasProgressNotification, "Expected at least one notifications/progress with progressToken 'progress-token-test-1'");
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }
}
