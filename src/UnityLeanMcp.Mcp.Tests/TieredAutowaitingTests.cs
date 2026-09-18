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
        var receivedProgress = new List<ProgressNotificationValue>();
        var progress = new Progress<ProgressNotificationValue>(p =>
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
    public async Task UnityClient_EvalAsync_WhenBusyForeignOperation_GracePeriodExpires_ReturnsFailFastDiagnostic()
    {
        var receivedProgress = new List<ProgressNotificationValue>();
        var progress = new Progress<ProgressNotificationValue>(p =>
        {
            lock (receivedProgress)
            {
                receivedProgress.Add(p);
            }
        });

        int evalAttempts = 0;

        await using var server = await MockUnityServer.StartAsync((srv, line) =>
        {
            if (line.StartsWith("POLL_REFRESH"))
            {
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
        }, new UnityClientOptions(PollIntervalMs: 20, BusyGracePeriod: TimeSpan.FromMilliseconds(100)));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var result = await server.Client.EvalAsync("return 1;", progress, cts.Token);

        Assert.False(result.Success);
        Assert.Contains("Unity is busy executing 'test' (id: op_foreign_999). If this operation is hung, call unity_stop to recover.", result.Message);

        lock (receivedProgress)
        {
            Assert.Contains(receivedProgress, p => p.Message != null && p.Message.Contains("Waiting for active 'test'"));
        }
    }

    [Fact]
    public async Task UnityClient_EvalAsync_WhenBusyForeignOperation_ClearsDuringGracePeriod_Succeeds()
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
        var progress = new Progress<ProgressNotificationValue>(p =>
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
        var result = await server.Client.RunTestsAsync(null, null, null, null, "all", false, progress, cts.Token);

        Assert.True(result.Success, result.Message);
        Assert.Equal(5, result.PassCount);
        Assert.Equal(2, runTestsAttempts);

        lock (receivedProgress)
        {
            Assert.Contains(receivedProgress, p => p.Message != null && p.Message.Contains("Unity is compiling script assemblies"));
        }
    }

    [Fact]
    public async Task UnityClient_RunTestsAsync_WhenBusyForeignOperation_GracePeriodExpires_ReturnsFailFastDiagnostic()
    {
        int runTestsAttempts = 0;

        await using var server = await MockUnityServer.StartAsync((srv, line) =>
        {
            if (line.StartsWith("POLL_REFRESH"))
            {
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
        }, new UnityClientOptions(PollIntervalMs: 20, BusyGracePeriod: TimeSpan.FromMilliseconds(100)));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var result = await server.Client.RunTestsAsync(null, null, null, null, "all", false, null, cts.Token);

        Assert.False(result.Success);
        Assert.Contains("Unity is busy executing 'eval' (id: op_eval_777). If this operation is hung, call unity_stop to recover.", result.Message);
    }
}
