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
        var receivedProgress = new List<ProgressNotificationValue>();
        var progress = new Progress<ProgressNotificationValue>(p =>
        {
            lock (receivedProgress)
            {
                receivedProgress.Add(p);
            }
        });

        int pollCount = 0;

        await using var server = await MockUnityServer.StartAsync((srv, line) =>
        {
            if (line.StartsWith("RUN_TESTS"))
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
                srv.WriteTestRunning(state1);

                return "RUNNING";
            }
            if (line.StartsWith("POLL_TESTS"))
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
                    srv.WriteTestRunning(state2);
                    return "RUNNING";
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
                    srv.WriteTestResult(opId, result);
                    return "SUCCESS 9 passed";
                }
            }
            return null;
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var runResult = await server.Client.RunTestsAsync(null, null, null, null, "editmode", false, progress, cts.Token);

        Assert.True(runResult.Success);
        Assert.Equal(9, runResult.PassCount);
        Assert.Equal(1, runResult.FailCount);

        // Verify progress reports
        lock (receivedProgress)
        {
            Assert.NotEmpty(receivedProgress);
            Assert.Contains(receivedProgress, p => p.Message != null && p.Message.Contains("AssetDatabase"));
            Assert.Contains(receivedProgress, p => p.Message != null && p.Message.Contains("Initializing editmode test run"));
            Assert.Contains(receivedProgress, p => p.Message != null && p.Message.Contains("Suite.TestAlpha"));
            Assert.Contains(receivedProgress, p => p.Message != null && p.Message.Contains("Suite.TestBeta"));
            Assert.Contains(receivedProgress, p => p.Message != null && p.Message.Contains("Tests finished:"));
            Assert.Contains(receivedProgress, p => p.Message == "Tests finished: 9 passed.");
            Assert.DoesNotContain(receivedProgress, p => p.Message != null && p.Message.Contains("0 skipped"));
        }
    }

    [Fact]
    public async Task UnityClient_RunTestsAsync_WhenTestsSkipped_ReportsProgressWithSkippedCount()
    {
        var receivedProgress = new List<ProgressNotificationValue>();
        var progress = new Progress<ProgressNotificationValue>(p =>
        {
            lock (receivedProgress)
            {
                receivedProgress.Add(p);
            }
        });

        string? opId = null;

        await using var server = await MockUnityServer.StartAsync((srv, line) =>
        {
            if (line.StartsWith("RUN_TESTS"))
            {
                string[] parts = line.Split(' ');
                opId = parts.Length > 1 ? parts[1] : "test-op";
                return "RUNNING";
            }
            if (line.StartsWith("POLL_TESTS"))
            {
                var result = new UnityTestRunResult
                {
                    RunId = opId ?? "test-op",
                    Success = true,
                    PassCount = 27,
                    FailCount = 0,
                    SkipCount = 1,
                    ResultState = "Passed"
                };
                srv.WriteTestResult(opId ?? "test-op", result);
                return "SUCCESS 27 passed, 1 skipped";
            }
            return null;
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var runResult = await server.Client.RunTestsAsync(null, null, null, null, "editmode", false, progress, cts.Token);

        Assert.True(runResult.Success);
        Assert.Equal(27, runResult.PassCount);
        Assert.Equal(1, runResult.SkipCount);

        lock (receivedProgress)
        {
            Assert.NotEmpty(receivedProgress);
            Assert.Contains(receivedProgress, p => p.Message == "Tests finished: 27 passed, 1 skipped.");
        }
    }

    [Fact]
    public async Task McpServer_EmitsProgressNotifications_WhenProgressTokenProvided()
    {
        int pollCount = 0;

        await using var server = await MockUnityServer.StartAsync((srv, line) =>
        {
            if (line.StartsWith("RUN_TESTS"))
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
                srv.WriteTestRunning(state);
                return "RUNNING";
            }
            if (line.StartsWith("POLL_TESTS"))
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
                    srv.WriteTestResult(opId, result);
                    return "RUNNING";
                }
                return "SUCCESS 5 passed";
            }
            return null;
        });

        await using var client = new McpTestClient(server.ProjectRoot);
        var toolResult = await client.CallToolAsync(
            "unity_run_tests",
            new { mode = "editmode" },
            timeout: TimeSpan.FromSeconds(20),
            progressToken: "progress-token-test-1");

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
}
