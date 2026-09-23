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
    [Theory]
    [InlineData("smoketest")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task UnityClient_RunTestsAsync_WhenModeIsInvalid_ReturnsBeforeUnityExecution(string? mode)
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "unity_test_invalid_mode_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(tempDir, "Temp"));

        try
        {
            var processManager = new UnityProcessManager(tempDir, NullLogger<UnityProcessManager>.Instance)
                .WithTrustedTestProcessProvider();
            var client = new UnityClient(processManager, NullLogger<UnityClient>.Instance);

            var result = await client.RunTestsAsync(
                testNames: null,
                groupNames: null,
                categoryNames: null,
                assemblyNames: null,
                mode: mode,
                failedOnly: false,
                progress: null,
                cancellationToken: CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal("InvalidInput", result.ResultState);
            Assert.Equal(TestModeParser.InvalidModeMessage, result.Message);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task UnityClient_RunTestsAsync_WhenModeIsAll_ReturnsRemovedAllModeError()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "unity_test_all_mode_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(tempDir, "Temp"));

        try
        {
            var processManager = new UnityProcessManager(tempDir, NullLogger<UnityProcessManager>.Instance)
                .WithTrustedTestProcessProvider();
            var client = new UnityClient(processManager, NullLogger<UnityClient>.Instance);

            var result = await client.RunTestsAsync(
                testNames: null,
                groupNames: null,
                categoryNames: null,
                assemblyNames: null,
                mode: "all",
                failedOnly: false,
                progress: null,
                cancellationToken: CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal("InvalidInput", result.ResultState);
            Assert.Equal(TestModeParser.RemovedAllModeMessage, result.Message);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Theory]
    [InlineData("testNames")]
    [InlineData("groupNames")]
    [InlineData("categoryNames")]
    [InlineData("assemblyNames")]
    public async Task UnityClient_RunTestsAsync_WhenFilterContainsBlankValue_ReturnsBeforeUnityExecution(string filterName)
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "unity_test_invalid_filter_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(tempDir, "Temp"));

        try
        {
            var processManager = new UnityProcessManager(tempDir, NullLogger<UnityProcessManager>.Instance)
                .WithTrustedTestProcessProvider();
            var client = new UnityClient(processManager, NullLogger<UnityClient>.Instance);

            var result = await client.RunTestsAsync(
                testNames: filterName == "testNames" ? ["Valid", " "] : null,
                groupNames: filterName == "groupNames" ? ["Valid.*", " "] : null,
                categoryNames: filterName == "categoryNames" ? ["Valid", " "] : null,
                assemblyNames: filterName == "assemblyNames" ? ["Valid", " "] : null,
                mode: "editmode",
                failedOnly: false,
                progress: null,
                cancellationToken: CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal("InvalidInput", result.ResultState);
            Assert.Contains($"Invalid test filter '{filterName}[1]'", result.Message);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task UnityClient_RunTestsAsync_WhenInitialResponseIsError_ReturnsImmediatelyWithoutHanging()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var server = await MockUnityServer.StartAsync(cmd =>
        {
            if (cmd.StartsWith("RUN_TESTS"))
            {
                return "ERROR: Missing or invalid operation id";
            }
            return null;
        });

        var result = await server.Client.RunTestsAsync(null, null, null, null, "editmode", false, null, cts.Token);

        Assert.False(result.Success);
        Assert.DoesNotContain("ERROR", result.Message);
        Assert.Equal("Missing or invalid operation id", result.Message);
    }

    [Fact]
    public async Task UnityClient_RunTestsAsync_WhenInitialResponseIsFailure_ReturnsImmediatelyWithoutHanging()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var server = await MockUnityServer.StartAsync(cmd =>
        {
            if (cmd.StartsWith("RUN_TESTS"))
            {
                return "FAILURE: Runner failed to start";
            }
            return null;
        });

        var result = await server.Client.RunTestsAsync(null, null, null, null, "editmode", false, null, cts.Token);

        Assert.False(result.Success);
        Assert.DoesNotContain("FAILURE", result.Message);
        Assert.Equal("Runner failed to start", result.Message);
    }

    [Fact]
    public async Task UnityClient_RunTestsAsync_WhenPollResponseIsIdle_TerminatesImmediatelyWithFailure()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var server = await MockUnityServer.StartAsync(cmd =>
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
        });

        var result = await server.Client.RunTestsAsync(null, null, null, null, "editmode", false, null, cts.Token);

        Assert.False(result.Success);
        Assert.Contains("no longer recognized by the Editor (Editor is idle)", result.Message);
    }

    [Fact]
    public async Task UnityClient_RunTestsAsync_WhenPollResponseIsError_TerminatesImmediatelyWithFailure()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var server = await MockUnityServer.StartAsync(cmd =>
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
        });

        var result = await server.Client.RunTestsAsync(null, null, null, null, "editmode", false, null, cts.Token);

        Assert.False(result.Success);
        Assert.DoesNotContain("ERROR", result.Message);
        Assert.Equal("Something went wrong", result.Message);
    }

    [Fact]
    public async Task UnityClient_RunTestsAsync_WhenPollResponseIsErrorWithoutColon_StripsPrefixAndUnescapes()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var server = await MockUnityServer.StartAsync(cmd =>
        {
            if (cmd.StartsWith("RUN_TESTS"))
            {
                return "RUNNING";
            }
            if (cmd.StartsWith("POLL_TESTS"))
            {
                return "ERROR Test runner crashed\\nAt frame 42";
            }
            return null;
        });

        var result = await server.Client.RunTestsAsync(null, null, null, null, "editmode", false, null, cts.Token);

        Assert.False(result.Success);
        Assert.DoesNotContain("ERROR", result.Message);
        Assert.Equal("Test runner crashed\nAt frame 42", result.Message);
    }

    [Fact]
    public async Task UnityClient_RunTestsAsync_WhenInitialResponseIsEscapedErrorOrFailure_StripsPrefixAndUnescapes()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var server = await MockUnityServer.StartAsync(cmd =>
        {
            if (cmd.StartsWith("RUN_TESTS"))
            {
                return "FAILURE: Test execution aborted\\nReason: C:\\\\Temp\\\\failure.log\\t(Code 1)";
            }
            return null;
        });

        var result = await server.Client.RunTestsAsync(null, null, null, null, "editmode", false, null, cts.Token);

        Assert.False(result.Success);
        Assert.DoesNotContain("FAILURE", result.Message);
        Assert.Equal("Test execution aborted\nReason: C:\\Temp\\failure.log\t(Code 1)", result.Message);
    }

    [Fact]
    public async Task UnityClient_RunTestsAsync_WhenInitialResponseIsSuccessWithDurableFile_UsesDurableResult()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var server = await MockUnityServer.StartAsync((srv, cmd) =>
        {
            if (cmd.StartsWith("RUN_TESTS"))
            {
                string[] parts = cmd.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                string operationId = parts[1];
                var durableResult = new UnityTestRunResult
                {
                    RunId = operationId,
                    Success = true,
                    ResultState = "Passed",
                    PassCount = 10,
                    Message = "Durable result"
                };
                srv.WriteTestResult(operationId, durableResult);

                return "SUCCESS All tests passed\\nTotal: 10";
            }
            return null;
        });

        var result = await server.Client.RunTestsAsync(null, null, null, null, "editmode", false, null, cts.Token);

        Assert.True(result.Success);
        Assert.Equal(10, result.PassCount);
        Assert.Equal("Durable result", result.Message);
    }

    [Fact]
    public async Task UnityClient_RunTestsAsync_WhenPollResponseIsBusy_TerminatesImmediatelyWithFailure()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var server = await MockUnityServer.StartAsync(cmd =>
        {
            if (cmd.StartsWith("RUN_TESTS"))
            {
                return "RUNNING";
            }
            if (cmd.StartsWith("POLL_TESTS"))
            {
                return "BUSY eval foreign-op";
            }
            return null;
        });

        var result = await server.Client.RunTestsAsync(null, null, null, null, "editmode", false, null, cts.Token);

        Assert.False(result.Success);
        Assert.Contains("Lost ownership of test run: BUSY eval foreign-op", result.Message);
    }

    [Fact]
    public async Task UnityClient_EvalAsync_WhenPollResponseIsIdle_TerminatesWithFailure()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var server = await MockUnityServer.StartAsync(cmd =>
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
        });

        var result = await server.Client.EvalAsync("return 1 + 1;", cts.Token);

        Assert.False(result.Success);
        Assert.Contains("no longer recognized by the Editor (Editor is idle)", result.Message);
    }

    [Fact]
    public async Task UnityClient_EvalAsync_WhenPollResponseIsIdleButResultFileExists_ReturnsResultSuccessfully()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        string capturedOpId = "";
        await using var server = await MockUnityServer.StartAsync((srv, cmd) =>
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
                srv.WriteEvalResult(capturedOpId, "42");
                return "IDLE";
            }
            return null;
        });

        var result = await server.Client.EvalAsync("return 1 + 1;", cts.Token);

        Assert.True(result.Success);
        Assert.Equal("42", result.Payload);
    }

    [Fact]
    public async Task UnityClient_EvalAsync_WhenRefreshFails_AbortsAndReturnsCompilationError()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        bool evalInvoked = false;
        await using var server = await MockUnityServer.StartAsync(cmd =>
        {
            if (cmd.StartsWith("POLL_REFRESH"))
            {
                return "FAILURE Script compilation failed";
            }
            if (cmd.StartsWith("EVAL"))
            {
                evalInvoked = true;
                return "RUNNING";
            }
            return null;
        });

        var result = await server.Client.EvalAsync("return 1 + 1;", cts.Token);

        Assert.False(result.Success);
        Assert.False(evalInvoked, "EVAL should not be invoked when pre-refresh compilation fails.");
        Assert.Contains("compilation", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnityClient_EvalAsync_WhenInitialResponseIsFailure_StripsPrefixAndParsesDiagnosticsCorrectly()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var server = await MockUnityServer.StartAsync(cmd =>
        {
            if (cmd.StartsWith("EVAL"))
            {
                return "FAILURE eval(1,5): error CS0103: The name 'x' does not exist in the current context";
            }
            return null;
        });

        var result = await server.Client.EvalAsync("return x;", cts.Token);

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

    [Fact]
    public async Task UnityClient_EvalAsync_WhenInitialResponseIsError_StripsPrefixCorrectly()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var server = await MockUnityServer.StartAsync(cmd =>
        {
            if (cmd.StartsWith("EVAL"))
            {
                return "ERROR: Missing operation id or code snippet";
            }
            return null;
        });

        var result = await server.Client.EvalAsync("return 1;", cts.Token);

        Assert.False(result.Success);
        Assert.DoesNotContain("ERROR:", result.Message);
        Assert.Equal("Missing operation id or code snippet", result.Message);
    }

    [Fact]
    public async Task UnityClient_RunTestsAsync_SendsStructuredJsonCommandOverSocket()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        string? receivedCommand = null;
        await using var server = await MockUnityServer.StartAsync(cmd =>
        {
            if (cmd.StartsWith("RUN_TESTS"))
            {
                receivedCommand = cmd;
                return "ERROR: Intended mock stop";
            }
            return null;
        });

        var result = await server.Client.RunTestsAsync(
            testNames: ["TestA", "TestB"],
            groupNames: ["Grp1.*"],
            categoryNames: ["Fast"],
            assemblyNames: ["MyAsm"],
            mode: "editmode",
            failedOnly: false,
            progress: null,
            cancellationToken: cts.Token);

        Assert.NotNull(receivedCommand);
        Assert.StartsWith("RUN_TESTS ", receivedCommand);
        string[] parts = receivedCommand.Split(' ', 3);
        Assert.Equal(3, parts.Length);
        string json = ProtocolCodec.UnescapeLine(parts[2]);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("editmode", root.GetProperty("mode").GetString());
        Assert.Equal(2, root.GetProperty("testNames").GetArrayLength());
        Assert.Equal("TestA", root.GetProperty("testNames")[0].GetString());
        Assert.Equal("Grp1.*", root.GetProperty("groupNames")[0].GetString());
        Assert.Equal("Fast", root.GetProperty("categoryNames")[0].GetString());
        Assert.Equal("MyAsm", root.GetProperty("assemblyNames")[0].GetString());
        Assert.False(root.GetProperty("failedOnly").GetBoolean());
    }
}
