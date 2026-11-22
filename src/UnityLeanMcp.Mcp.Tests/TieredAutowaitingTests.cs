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

    [Theory]
    [InlineData("EVAL")]
    [InlineData("RUN_TESTS")]
    public async Task LostInitialDispatch_RetriesSameOperationId(string verb)
    {
        string? operationId = null;
        int attempts = 0;
        await using var server = await MockUnityServer.StartAsync((srv, line) =>
        {
            if (line.StartsWith(verb + " ", StringComparison.Ordinal))
            {
                var id = line.Split(' ')[1];
                if (++attempts == 1)
                {
                    operationId = id;
                    throw new System.IO.IOException("Simulated reload before admission");
                }
                Assert.Equal(operationId, id);
                string path = srv.PathResolver.GetResultFilePath(verb == "EVAL" ? UnityOperationKind.Eval : UnityOperationKind.Test, id);
                System.IO.File.WriteAllText(path, "{\"operationId\":\"" + id + "\",\"runId\":\"" + id + "\",\"success\":true}");
                return "RUNNING";
            }
            return (string?)null;
        });
        if (verb == "EVAL") Assert.True((await server.Client.EvalAsync("return 1;")).Success);
        else Assert.True((await server.Client.RunTestsAsync(null, null, null, null, "editmode")).Success);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task UnityClient_EvalAsync_WhenBusyCompile_AutowaitsAndSucceeds()
    {
        var receivedProgress = new List<ProgressNotificationValue>();
        var progress = new SyncProgress(p =>
        {
            lock (receivedProgress)
            {
                receivedProgress.Add(p);
            }
        });

        int evalAttempts = 0;
        int compilePolls = 0;

        await using var server = await MockUnityServer.StartAsync((srv, line) =>
        {
            if (line.StartsWith("POLL_REFRESH"))
            {
                if (evalAttempts == 1)
                {
                    if (compilePolls++ == 0)
                    {
                        return "COMPILING";
                    }
                }
                return null;
            }
            if (line.StartsWith("EVAL"))
            {
                evalAttempts++;
                if (evalAttempts == 1)
                {
                    return "BUSY compile";
                }
                else
                {
                    string[] parts = line.Split(' ', 3);
                    string opId = parts.Length > 1 ? parts[1] : "eval-op";
                    srv.WriteEvalResult(opId, "100");
                    return "SUCCESS 100";
                }
            }
            return null;
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var result = await server.Client.EvalAsync("return 50 + 50;", progress, cts.Token);

        Assert.True(result.Success, result.Message);
        Assert.Equal("100", result.Payload);
        Assert.Equal(2, evalAttempts);

        lock (receivedProgress)
        {
            Assert.Contains(receivedProgress, p => p.Message != null && p.Message.Contains("Unity is compiling script assemblies"));
        }
    }

    [Fact]
    public async Task UnityClient_EvalAsync_WhenBusyForeignOperation_WaitsUntilCallerCancels()
    {
        var receivedProgress = new List<ProgressNotificationValue>();
        var progress = new SyncProgress(p =>
        {
            lock (receivedProgress)
            {
                receivedProgress.Add(p);
            }
        });

        int evalAttempts = 0;

        using var cts = new CancellationTokenSource();
        int busyPolls = 0;
        await using var server = await MockUnityServer.StartAsync((srv, line) =>
        {
            if (line.StartsWith("POLL_REFRESH"))
            {
                if (++busyPolls == 5) cts.Cancel();
                if (evalAttempts >= 1)
                {
                    return "BUSY test op_foreign_999";
                }
                return null;
            }
            if (line.StartsWith("EVAL"))
            {
                evalAttempts++;
                return "BUSY test op_foreign_999";
            }
            return null;
        }, new UnityClientOptions(PollIntervalMs: 20));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => server.Client.EvalAsync("return 1;", progress, cts.Token));
        Assert.Equal(5, busyPolls);

        lock (receivedProgress)
        {
            Assert.Contains(receivedProgress, p => p.Message != null && p.Message.Contains("Waiting for active 'test'"));
        }
    }

    [Fact]
    public async Task UnityClient_EvalAsync_WhenBusyForeignOperation_Completes_Succeeds()
    {
        int evalAttempts = 0;
        int gracePolls = 0;

        await using var server = await MockUnityServer.StartAsync((srv, line) =>
        {
            if (line.StartsWith("POLL_REFRESH"))
            {
                if (evalAttempts == 1)
                {
                    if (gracePolls++ < 1)
                    {
                        return "BUSY test op_foreign";
                    }
                }
                return null;
            }
            if (line.StartsWith("EVAL"))
            {
                evalAttempts++;
                if (evalAttempts == 1)
                {
                    return "BUSY test op_foreign";
                }
                else
                {
                    string[] parts = line.Split(' ', 3);
                    string opId = parts.Length > 1 ? parts[1] : "eval-op";
                    srv.WriteEvalResult(opId, "cleared_ok");
                    return "SUCCESS cleared_ok";
                }
            }
            return null;
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var result = await server.Client.EvalAsync("return \"cleared_ok\";", null, cts.Token);

        Assert.True(result.Success, result.Message);
        Assert.Equal("cleared_ok", result.Payload);
        Assert.Equal(2, evalAttempts);
    }

    [Fact]
    public async Task UnityClient_RunTestsAsync_WhenBusyCompile_AutowaitsAndSucceeds()
    {
        var receivedProgress = new List<ProgressNotificationValue>();
        var progress = new SyncProgress(p =>
        {
            lock (receivedProgress)
            {
                receivedProgress.Add(p);
            }
        });

        int runTestsAttempts = 0;
        int compilePolls = 0;

        await using var server = await MockUnityServer.StartAsync((srv, line) =>
        {
            if (line.StartsWith("POLL_REFRESH"))
            {
                if (runTestsAttempts == 1)
                {
                    if (compilePolls++ == 0)
                    {
                        return "COMPILING";
                    }
                }
                return null;
            }
            if (line.StartsWith("POLL_TESTS"))
            {
                return "READY";
            }
            if (line.StartsWith("RUN_TESTS"))
            {
                runTestsAttempts++;
                if (runTestsAttempts == 1)
                {
                    return "BUSY compile";
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
                    srv.WriteTestResult(opId, testResult);
                    return $"SUCCESS {opId}";
                }
            }
            return null;
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var result = await server.Client.RunTestsAsync(null, null, null, null, "editmode", false, false, progress, cts.Token);

        Assert.True(result.Success, result.Message);
        Assert.Equal(5, result.PassCount);
        Assert.Equal(2, runTestsAttempts);

        lock (receivedProgress)
        {
            Assert.Contains(receivedProgress, p => p.Message != null && p.Message.Contains("Unity is compiling script assemblies"));
        }
    }

    [Fact]
    public async Task UnityClient_RunTestsAsync_WhenBusyForeignOperation_WaitsUntilCallerCancels()
    {
        int runTestsAttempts = 0;

        using var cts = new CancellationTokenSource();
        int busyPolls = 0;
        await using var server = await MockUnityServer.StartAsync((srv, line) =>
        {
            if (line.StartsWith("POLL_REFRESH"))
            {
                if (++busyPolls == 5) cts.Cancel();
                if (runTestsAttempts >= 1)
                {
                    return "BUSY eval op_eval_777";
                }
                return null;
            }
            if (line.StartsWith("RUN_TESTS"))
            {
                runTestsAttempts++;
                return "BUSY eval op_eval_777";
            }
            return null;
        }, new UnityClientOptions(PollIntervalMs: 20));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => server.Client.RunTestsAsync(null, null, null, null, "editmode", false, false, null, cts.Token));
        Assert.Equal(5, busyPolls);
    }

    [Fact]
    public async Task UnityClient_GetCoverageAsync_WhenBusyCompile_WaitsForCapturedReportWithoutRefreshing()
    {
        int attempts = 0;
        int refreshes = 0;
        await using var server = await MockUnityServer.StartAsync((srv, line) =>
        {
            if (line.StartsWith("REFRESH") || line.StartsWith("RECOMPILE")) refreshes++;
            if (line.StartsWith("GET_COVERAGE"))
            {
                return ++attempts == 1 ? "BUSY compile" : "SUCCESS {\"files\":[]}";
            }
            return null;
        }, new UnityClientOptions(PollIntervalMs: 1));

        var result = await server.Client.GetCoverageAsync(new[] { "Assets/Scripts/Foo.cs" });

        Assert.True(result.Success);
        Assert.Equal(2, attempts);
        Assert.Equal(0, refreshes);
    }

    [Fact]
    public async Task UnityClient_GetCoverageAsync_WhenReloadClosesSocket_RetriesCapturedReport()
    {
        int attempts = 0;
        await using var server = await MockUnityServer.StartAsync((srv, line) =>
        {
            if (line.StartsWith("GET_COVERAGE"))
            {
                if (++attempts == 1) throw new IOException("Simulated reload while querying coverage");
                return "SUCCESS {\"files\":[]}";
            }
            return null;
        }, new UnityClientOptions(PollIntervalMs: 1));

        var result = await server.Client.GetCoverageAsync(new[] { "Assets/Scripts/Foo.cs" });
        Assert.True(result.Success);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task UnityClient_GetCoverageAsync_WhenBusyCompile_HonorsCancellation()
    {
        using var cts = new CancellationTokenSource();
        int polls = 0;
        await using var server = await MockUnityServer.StartAsync((srv, line) =>
        {
            if (line.StartsWith("GET_COVERAGE")) return "BUSY compile";
            if (line.StartsWith("POLL_REFRESH"))
            {
                if (++polls == 3) cts.Cancel();
                return "BUSY compile";
            }
            return null;
        }, new UnityClientOptions(PollIntervalMs: 1));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            server.Client.GetCoverageAsync(new[] { "Assets/Scripts/Foo.cs" }, null, cts.Token));
        Assert.Equal(3, polls);
    }

    [Fact]
    public async Task UnityClient_GetCoverageAsync_WhenSnapshotMissing_ReturnsActionableDiagnostic()
    {
        await using var server = await MockUnityServer.StartAsync((srv, line) =>
        {
            if (line.StartsWith("GET_COVERAGE"))
            {
                return "FAILURE No coverage data recorded. Run 'unity_test' with coverage: true first.";
            }
            return null;
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var result = await server.Client.GetCoverageAsync(new[] { "Assets/Scripts/Foo.cs" }, null, cts.Token);

        Assert.False(result.Success);
        Assert.Contains("No coverage data recorded", result.Message);
        Assert.Contains("coverage: true", result.Message);
    }


    [Fact]
    public async Task UnityClient_GetCoverageAsync_WhenBusyForeignOperation_WaitsUntilCallerCancels()
    {
        var receivedProgress = new List<ProgressNotificationValue>();
        var progress = new SyncProgress(p =>
        {
            lock (receivedProgress)
            {
                receivedProgress.Add(p);
            }
        });

        int coverageAttempts = 0;

        using var cts = new CancellationTokenSource();
        int busyPolls = 0;
        await using var server = await MockUnityServer.StartAsync((srv, line) =>
        {
            if (line.StartsWith("POLL_REFRESH"))
            {
                if (++busyPolls == 5) cts.Cancel();
                if (coverageAttempts >= 1)
                {
                    return "BUSY test op_test_123";
                }
                return null;
            }
            if (line.StartsWith("GET_COVERAGE"))
            {
                coverageAttempts++;
                return "BUSY test op_test_123";
            }
            return null;
        }, new UnityClientOptions(PollIntervalMs: 20));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => server.Client.GetCoverageAsync(new[] { "Assets/Scripts/Foo.cs" }, progress, cts.Token));
        Assert.Equal(5, busyPolls);

        lock (receivedProgress)
        {
            Assert.Contains(receivedProgress, p => p.Message != null && p.Message.Contains("Waiting for active 'test'"));
        }
    }

    [Fact]
    public async Task UnityClient_GetCoverageAsync_WhenBusyForeignOperation_Completes_Succeeds()
    {
        int coverageAttempts = 0;
        int gracePolls = 0;

        await using var server = await MockUnityServer.StartAsync((srv, line) =>
        {
            if (line.StartsWith("POLL_REFRESH"))
            {
                if (coverageAttempts == 1)
                {
                    if (gracePolls++ < 1)
                    {
                        return "BUSY test op_foreign_test";
                    }
                }
                return null;
            }
            if (line.StartsWith("GET_COVERAGE"))
            {
                coverageAttempts++;
                if (coverageAttempts == 1)
                {
                    return "BUSY test op_foreign_test";
                }
                else
                {
                    return "SUCCESS {\"files\":[{\"path\":\"Assets/Scripts/Foo.cs\",\"totalPoints\":10,\"coveredPoints\":8,\"uncoveredLines\":[1,2]}]}";
                }
            }
            return null;
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var result = await server.Client.GetCoverageAsync(new[] { "Assets/Scripts/Foo.cs" }, null, cts.Token);

        Assert.True(result.Success, result.Message);
        Assert.Single(result.Files);
        Assert.Equal("Assets/Scripts/Foo.cs", result.Files[0].Path);
        Assert.Equal(10, result.Files[0].TotalPoints);
        Assert.Equal(8, result.Files[0].CoveredPoints);
        Assert.Equal(2, coverageAttempts);
    }

    [Fact]
    public async Task UnityClient_RefreshAsync_WhenBusyCompile_AutowaitsAndSucceeds()
    {
        var receivedProgress = new List<ProgressNotificationValue>();
        var progress = new SyncProgress(p =>
        {
            lock (receivedProgress)
            {
                receivedProgress.Add(p);
            }
        });

        int refreshAttempts = 0;
        int compilePolls = 0;

        await using var server = await MockUnityServer.StartAsync((srv, line) =>
        {
            if (line.StartsWith("POLL_REFRESH"))
            {
                if (refreshAttempts == 1)
                {
                    if (compilePolls++ == 0)
                    {
                        return "COMPILING";
                    }
                    return null;
                }
                return null;
            }
            if (line.StartsWith("REFRESH"))
            {
                refreshAttempts++;
                if (refreshAttempts == 1)
                {
                    return "BUSY compile";
                }
                else
                {
                    string[] parts = line.Split(' ');
                    string opId = parts.Length > 1 ? parts[1] : "refresh-op";
                    srv.WriteRefreshResult(opId, true);
                    return "READY";
                }
            }
            return null;
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var result = await server.Client.RefreshAsync(isRecompile: false, progress, cts.Token);

        Assert.True(result.Success, result.Message);
        Assert.Equal(2, refreshAttempts);

        lock (receivedProgress)
        {
            Assert.Contains(receivedProgress, p => p.Message != null && p.Message.Contains("Waiting for active 'compile'"));
        }
    }

    [Fact]
    public async Task UnityClient_RefreshAsync_WhenBusyForeignOperation_WaitsUntilCallerCancels()
    {
        var receivedProgress = new List<ProgressNotificationValue>();
        var progress = new SyncProgress(p =>
        {
            lock (receivedProgress)
            {
                receivedProgress.Add(p);
            }
        });

        using var cts = new CancellationTokenSource();
        int busyPolls = 0;
        await using var server = await MockUnityServer.StartAsync((srv, line) =>
        {
            if (line.StartsWith("POLL_REFRESH"))
            {
                if (++busyPolls == 5) cts.Cancel();
                return "BUSY test op_test_123";
            }
            if (line.StartsWith("REFRESH"))
            {
                return "BUSY test op_test_123";
            }
            return null;
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => server.Client.RefreshAsync(isRecompile: false, progress, cts.Token));
        Assert.Equal(5, busyPolls);

        lock (receivedProgress)
        {
            Assert.Contains(receivedProgress, p => p.Message != null && p.Message.Contains("Waiting for active 'test'"));
        }
    }

    [Fact]
    public async Task UnityClient_EvalAsync_WhenRetryReceivesBusySameOpId_HandsOffToPollEvalWithoutPollRefresh()
    {
        int evalAttempts = 0;
        int pollEvalAttempts = 0;
        int pollRefreshAfterEval = 0;
        bool evalDispatched = false;

        await using var server = await MockUnityServer.StartAsync((srv, line) =>
        {
            if (line.StartsWith("POLL_REFRESH"))
            {
                if (evalDispatched)
                {
                    pollRefreshAfterEval++;
                }
                return "PLAYMODE";
            }
            if (line.StartsWith("EVAL "))
            {
                evalDispatched = true;
                evalAttempts++;
                string[] parts = line.Split(' ');
                string opId = parts[1];
                if (evalAttempts == 1)
                {
                    return null;
                }
                return $"BUSY eval {opId}";
            }
            if (line.StartsWith("POLL_EVAL "))
            {
                pollEvalAttempts++;
                string opId = line.Split(' ')[1];
                if (pollEvalAttempts == 1)
                {
                    return "RUNNING";
                }
                srv.WriteEvalResult(opId, "\"ok\"");
                return "READY";
            }
            return null;
        }, new UnityClientOptions(PollIntervalMs: 1));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var result = await server.Client.EvalAsync("\"ok\"", null, cts.Token);

        Assert.True(result.Success, result.Message);
        Assert.Equal("\"ok\"", result.Payload);
        Assert.Equal(2, evalAttempts);
        Assert.True(pollEvalAttempts >= 2, $"Expected at least 2 POLL_EVAL calls, got {pollEvalAttempts}");
        Assert.Equal(0, pollRefreshAfterEval);
    }

    [Fact]
    public async Task UnityClient_RunTestsAsync_WhenRetryReceivesBusySameOpId_HandsOffToPollTestsWithoutPollRefresh()
    {
        var receivedProgress = new List<ProgressNotificationValue>();
        var progress = new SyncProgress(p =>
        {
            lock (receivedProgress)
            {
                receivedProgress.Add(p);
            }
        });

        int runTestsAttempts = 0;
        int pollTestsAttempts = 0;
        int pollRefreshAfterRunTests = 0;
        bool runTestsDispatched = false;

        await using var server = await MockUnityServer.StartAsync((srv, line) =>
        {
            if (line.StartsWith("POLL_REFRESH"))
            {
                if (runTestsDispatched)
                {
                    pollRefreshAfterRunTests++;
                }
                return "READY";
            }
            if (line.StartsWith("REFRESH "))
            {
                string opId = line.Split(' ')[1];
                srv.WriteRefreshResult(opId, success: true);
                return $"SUCCESS {opId}";
            }
            if (line.StartsWith("RUN_TESTS "))
            {
                runTestsDispatched = true;
                runTestsAttempts++;
                string opId = line.Split(' ')[1];
                if (runTestsAttempts == 1)
                {
                    return null;
                }
                return $"BUSY test {opId}";
            }
            if (line.StartsWith("POLL_TESTS "))
            {
                pollTestsAttempts++;
                string opId = line.Split(' ')[1];
                if (pollTestsAttempts == 1)
                {
                    var runningState = new UnityTestRunState
                    {
                        RunId = opId,
                        TotalTests = 2,
                        CompletedTests = 1,
                        PassCount = 1,
                        FailCount = 0,
                        CurrentTestName = "SampleTest",
                        Status = "Running"
                    };
                    string stateJson = System.Text.Json.JsonSerializer.Serialize(runningState);
                    return "RUNNING " + ProtocolCodec.EscapeLine(stateJson);
                }
                srv.WriteTestResult(opId, new UnityTestRunResult
                {
                    RunId = opId,
                    Success = true,
                    PassCount = 2,
                    FailCount = 0
                });
                return "READY";
            }
            return null;
        }, new UnityClientOptions(PollIntervalMs: 1));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var result = await server.Client.RunTestsAsync(null, null, null, null, "editmode", false, false, progress, cts.Token);

        Assert.True(result.Success, result.Message);
        Assert.Equal(2, result.PassCount);
        Assert.Equal(2, runTestsAttempts);
        Assert.True(pollTestsAttempts >= 2, $"Expected at least 2 POLL_TESTS calls, got {pollTestsAttempts}");
        Assert.Equal(0, pollRefreshAfterRunTests);

        lock (receivedProgress)
        {
            Assert.Contains(receivedProgress, p => p.Message != null && p.Message.Contains("SampleTest"));
        }
    }

    private sealed class SyncProgress(Action<ProgressNotificationValue> onReport) : IProgress<ProgressNotificationValue>
    {
        public void Report(ProgressNotificationValue value) => onReport(value);
    }
}
