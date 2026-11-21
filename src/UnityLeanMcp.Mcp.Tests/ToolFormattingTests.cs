using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using Xunit;

namespace UnityLeanMcp.Mcp.Tests;

[Trait("Category", "Unit")]
public sealed class ToolFormattingTests
{
    [Fact]
    public void UnityProcessManager_GetUnityMode_DistinguishesBatchmodeAndGui()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "unity_mode_test_" + Guid.NewGuid().ToString("N"));
        string tempSubDir = Path.Combine(tempDir, "Temp");
        Directory.CreateDirectory(tempSubDir);
        try
        {
            var realPm = new UnityProcessManager(tempDir, NullLogger<UnityProcessManager>.Instance)
            {
                ProcessProvider = () => new[] { Process.GetCurrentProcess() }
            };
            int testPid = Environment.ProcessId;

            string identityFile = realPm.PathResolver.PidFile + ".identity.json";
            File.WriteAllText(identityFile, JsonSerializer.Serialize(new
            {
                ProcessId = testPid,
                StartTimeUtcTicks = DateTime.UtcNow.Ticks,
                ExecutablePath = "Unity",
                ProjectRoot = tempDir,
                Mode = "Batchmode"
            }));
            Assert.Equal("Batchmode", realPm.GetUnityMode(testPid));

            File.WriteAllText(identityFile, JsonSerializer.Serialize(new
            {
                ProcessId = testPid,
                StartTimeUtcTicks = DateTime.UtcNow.Ticks,
                ExecutablePath = "Unity",
                ProjectRoot = tempDir,
                Mode = "GUI"
            }));
            Assert.Equal("GUI", realPm.GetUnityMode(testPid));

            File.Delete(identityFile);
            Assert.Equal("GUI", realPm.GetUnityMode(testPid));
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task UnityRefresh_WhenClean_ReturnsZeroErrorsMessage()
    {
        using var ctx = new ToolFormattingTestContext();
        ctx.Client.RefreshResultToReturn = new UnityRefreshResult
        {
            Success = true,
            Message = ""
        };

        var result = await ctx.Tools.UnityRefreshAsync();

        Assert.False(result.IsError);
        Assert.Equal("AssetDatabase refresh completed with 0 errors.", ToolFormattingTestContext.GetResultText(result));
    }

    [Fact]
    public async Task UnityRefresh_WhenCleanFlag_ReturnsZeroErrorsMessage()
    {
        using var ctx = new ToolFormattingTestContext();
        ctx.Client.RefreshResultToReturn = new UnityRefreshResult
        {
            Success = true,
            Message = ""
        };

        var result = await ctx.Tools.UnityRefreshAsync(clean: true);

        Assert.False(result.IsError);
        Assert.Equal("Clean script recompilation completed with 0 errors.", ToolFormattingTestContext.GetResultText(result));
    }

    [Fact]
    public async Task UnityRefresh_WhenBusy_ReportsBusyWithoutClaimingCompilationFailed()
    {
        using var ctx = new ToolFormattingTestContext();
        ctx.Client.RefreshResultToReturn = new UnityRefreshResult
        {
            Success = false,
            Message = "Unity is busy with another operation: BUSY test"
        };

        var result = await ctx.Tools.UnityRefreshAsync();

        Assert.True(result.IsError);
        string text = ToolFormattingTestContext.GetResultText(result);
        Assert.Equal("Unity is busy with another operation: BUSY test", text);
        Assert.DoesNotContain("Error: Unity compilation failed.", text);
    }

    [Fact]
    public async Task UnityRefresh_WhenCleanAndBusy_ReportsBusyWithoutClaimingRecompilationFailed()
    {
        using var ctx = new ToolFormattingTestContext();
        ctx.Client.RefreshResultToReturn = new UnityRefreshResult
        {
            Success = false,
            Message = "Unity is busy with another operation: BUSY test"
        };

        var result = await ctx.Tools.UnityRefreshAsync(clean: true);

        Assert.True(result.IsError);
        string text = ToolFormattingTestContext.GetResultText(result);
        Assert.Equal("Unity is busy with another operation: BUSY test", text);
        Assert.DoesNotContain("Error: Unity recompilation failed.", text);
    }

    [Fact]
    public async Task UnityRefresh_WhenInterrupted_ReportsInterruptionCleanly()
    {
        using var ctx = new ToolFormattingTestContext();
        ctx.Client.RefreshResultToReturn = new UnityRefreshResult
        {
            Success = false,
            Interrupted = true,
            Message = "Unity operation was interrupted by domain reload or editor restart."
        };

        var result = await ctx.Tools.UnityRefreshAsync();

        Assert.True(result.IsError);
        string text = ToolFormattingTestContext.GetResultText(result);
        Assert.Equal("Unity operation was interrupted by domain reload or editor restart.", text);
        Assert.DoesNotContain("Error: Unity compilation failed.", text);
    }

    [Fact]
    public async Task UnityRefresh_WhenCompilationFails_ReportsDiagnosticsAndError()
    {
        using var ctx = new ToolFormattingTestContext();
        ctx.Client.RefreshResultToReturn = new UnityRefreshResult
        {
            Success = false,
            Interrupted = false,
            Message = "Assets/Scripts/Foo.cs(10,5): error CS0103: The name 'bar' does not exist in the current context"
        };

        var result = await ctx.Tools.UnityRefreshAsync();

        Assert.True(result.IsError);
        string text = ToolFormattingTestContext.GetResultText(result);
        Assert.Contains("error CS0103", text);
        Assert.Contains("Error: Unity compilation failed.", text);
    }

    [Fact]
    public async Task UnityRefresh_WhenCleanAndCompilationFails_ReportsDiagnosticsAndRecompilationError()
    {
        using var ctx = new ToolFormattingTestContext();
        ctx.Client.RefreshResultToReturn = new UnityRefreshResult
        {
            Success = false,
            Interrupted = false,
            Message = "Assets/Scripts/Foo.cs(10,5): error CS0103: The name 'bar' does not exist in the current context"
        };

        var result = await ctx.Tools.UnityRefreshAsync(clean: true);

        Assert.True(result.IsError);
        string text = ToolFormattingTestContext.GetResultText(result);
        Assert.Contains("error CS0103", text);
        Assert.Contains("Error: Unity recompilation failed.", text);
    }

    [Fact]
    public async Task UnityRefresh_ReturnsStructuredDiagnostics()
    {
        using var ctx = new ToolFormattingTestContext();
        ctx.Client.RefreshResultToReturn = new UnityRefreshResult
        {
            Success = false,
            Interrupted = false,
            Message = "Assets/Scripts/Game.cs(12,4): error CS0103: The name 'player' does not exist in the current context\n" +
                      "Assets/Scripts/Util.cs(88,16): warning CS0219: The variable 'temp' is assigned but its value is never used"
        };

        var result = await ctx.Tools.UnityRefreshAsync();

        Assert.True(result.IsError);
        Assert.Single(result.Content);

        string text = ToolFormattingTestContext.GetResultText(result);
        Assert.Contains("Warnings:", text);
        Assert.Contains("Assets/Scripts/Util.cs#L88", text);
        Assert.Contains("warning CS0219: The variable 'temp' is assigned but its value is never used", text);
        Assert.Contains("Errors:", text);
        Assert.Contains("Assets/Scripts/Game.cs#L12", text);
        Assert.Contains("error CS0103: The name 'player' does not exist in the current context", text);
        Assert.EndsWith("Error: Unity compilation failed.", text);

        int warningsIndex = text.IndexOf("Warnings:");
        int errorsIndex = text.IndexOf("Errors:");
        Assert.True(errorsIndex < warningsIndex, "Errors must precede warnings");
    }

    [Fact]
    public async Task UnityRefresh_WhenClean_ReturnsStructuredDiagnostics()
    {
        using var ctx = new ToolFormattingTestContext();
        ctx.Client.RefreshResultToReturn = new UnityRefreshResult
        {
            Success = false,
            Interrupted = false,
            Message = "Assets/Scripts/RecompileTest.cs(5,10): error CS1002: ; expected"
        };

        var result = await ctx.Tools.UnityRefreshAsync(clean: true);

        Assert.True(result.IsError);
        Assert.Single(result.Content);

        string text = ToolFormattingTestContext.GetResultText(result);
        Assert.Contains("Assets/Scripts/RecompileTest.cs#L5", text);
        Assert.Contains("error CS1002: ; expected", text);
        Assert.EndsWith("Error: Unity recompilation failed.", text);
        Assert.DoesNotContain("Errors:", text);
        Assert.DoesNotContain("Warnings:", text);
    }

    [Fact]
    public async Task UnityRefresh_WhenSuccessWithWarningsUnderCap_ReportsFormattedWarningsWithoutTruncation()
    {
        using var ctx = new ToolFormattingTestContext();
        ctx.Client.RefreshResultToReturn = new UnityRefreshResult
        {
            Success = true,
            Message = "Assets/Scripts/A.cs(10,5): warning CS0219: Variable 'x' is unused\n" +
                      "Assets/Scripts/B.cs(20,5): warning CS0219: Variable 'y' is unused\n" +
                      "Assets/Scripts/C.cs(30,5): warning CS0219: Variable 'z' is unused"
        };

        var result = await ctx.Tools.UnityRefreshAsync();

        Assert.False(result.IsError);
        string text = ToolFormattingTestContext.GetResultText(result);
        Assert.Contains("Assets/Scripts/A.cs#L10", text);
        Assert.Contains("Assets/Scripts/B.cs#L20", text);
        Assert.Contains("Assets/Scripts/C.cs#L30", text);
        Assert.DoesNotContain("omitted", text);
        Assert.DoesNotContain("Warnings:", text);
        Assert.DoesNotContain("Errors:", text);
        Assert.EndsWith("AssetDatabase refresh completed with 0 errors (3 warnings).", text);
    }

    [Fact]
    public async Task UnityRefresh_WhenSuccessWithSingleWarning_ReportsSingularSummary()
    {
        using var ctx = new ToolFormattingTestContext();
        ctx.Client.RefreshResultToReturn = new UnityRefreshResult
        {
            Success = true,
            Message = "Assets/Scripts/A.cs(10,5): warning CS0219: Variable 'x' is unused"
        };

        var result = await ctx.Tools.UnityRefreshAsync();

        Assert.False(result.IsError);
        string text = ToolFormattingTestContext.GetResultText(result);
        Assert.Contains("Assets/Scripts/A.cs#L10", text);
        Assert.DoesNotContain("omitted", text);
        Assert.EndsWith("AssetDatabase refresh completed with 0 errors (1 warning).", text);
    }

    [Fact]
    public async Task UnityRefresh_WhenSuccessWithWarningsOverCap_ReportsCappedWarningsAndTruncationNotice()
    {
        using var ctx = new ToolFormattingTestContext();
        var lines = new List<string>();
        for (int i = 1; i <= 15; i++)
        {
            lines.Add($"Assets/Scripts/File{i}.cs({i},1): warning CS0168: The variable 'v{i}' is declared but never used");
        }

        ctx.Client.RefreshResultToReturn = new UnityRefreshResult
        {
            Success = true,
            Message = string.Join("\n", lines)
        };

        var result = await ctx.Tools.UnityRefreshAsync();

        Assert.False(result.IsError);
        string text = ToolFormattingTestContext.GetResultText(result);
        Assert.Contains("Assets/Scripts/File1.cs#L1", text);
        Assert.Contains("Assets/Scripts/File10.cs#L10", text);
        Assert.DoesNotContain("Assets/Scripts/File11.cs#L11", text);
        Assert.Contains("... and 5 more warning(s) omitted to preserve context window.", text);
        Assert.EndsWith("AssetDatabase refresh completed with 0 errors (15 warnings).", text);
    }

    [Fact]
    public async Task UnityRefresh_WhenErrorsAndWarnings_DisplaysWarningsFirstAndErrorsAtEndWithHeaders()
    {
        using var ctx = new ToolFormattingTestContext();
        ctx.Client.RefreshResultToReturn = new UnityRefreshResult
        {
            Success = false,
            Message = "Assets/Scripts/Error1.cs(10,1): error CS0103: The name 'foo' does not exist\n" +
                      "Assets/Scripts/Warn1.cs(20,1): warning CS0219: Variable 'w1' is unused\n" +
                      "Assets/Scripts/Error2.cs(30,1): error CS0103: The name 'bar' does not exist\n" +
                      "Assets/Scripts/Warn2.cs(40,1): warning CS0219: Variable 'w2' is unused"
        };

        var result = await ctx.Tools.UnityRefreshAsync();

        Assert.True(result.IsError);
        string text = ToolFormattingTestContext.GetResultText(result);
        Assert.Contains("Warnings:", text);
        Assert.Contains("Errors:", text);

        int warnHeaderIdx = text.IndexOf("Warnings:");
        int errHeaderIdx = text.IndexOf("Errors:");
        Assert.True(errHeaderIdx < warnHeaderIdx, "Errors header must precede Warnings header");

        int warn1Idx = text.IndexOf("Warn1.cs#L20");
        int err1Idx = text.IndexOf("Error1.cs#L10");
        Assert.True(err1Idx < warn1Idx, "Errors must appear before Warnings");

        Assert.EndsWith("Error: Unity compilation failed.", text);
    }

    [Fact]
    public async Task UnityRefresh_MessageContainingBusyIdentifier_DoesNotBypassCompilerDiagnosticFormatting()
    {
        using var ctx = new ToolFormattingTestContext();
        ctx.Client.RefreshResultToReturn = new UnityRefreshResult
        {
            Success = false,
            Message = "Assets/Scripts/Worker.cs(10,5): error CS0103: The name 'busy' does not exist in the current context"
        };

        var result = await ctx.Tools.UnityRefreshAsync();

        Assert.True(result.IsError);
        string text = ToolFormattingTestContext.GetResultText(result);
        Assert.Contains("Assets/Scripts/Worker.cs#L10: error CS0103", text);
        Assert.EndsWith("Error: Unity compilation failed.", text);
    }

    [Fact]
    public async Task UnityRefresh_WhenErrorsOnly_DisplaysErrorsWithoutHeaders()
    {
        using var ctx = new ToolFormattingTestContext();
        ctx.Client.RefreshResultToReturn = new UnityRefreshResult
        {
            Success = false,
            Message = "Assets/Scripts/Error1.cs(10,1): error CS0103: The name 'foo' does not exist\n" +
                      "Assets/Scripts/Error2.cs(20,1): error CS0103: The name 'bar' does not exist"
        };

        var result = await ctx.Tools.UnityRefreshAsync();

        Assert.True(result.IsError);
        string text = ToolFormattingTestContext.GetResultText(result);
        Assert.DoesNotContain("Warnings:", text);
        Assert.DoesNotContain("Errors:", text);
        Assert.Contains("Assets/Scripts/Error1.cs#L10", text);
        Assert.Contains("Assets/Scripts/Error2.cs#L20", text);
        Assert.EndsWith("Error: Unity compilation failed.", text);
    }

    [Fact]
    public async Task UnityRefresh_WhenUnparseableError_FallsBackToRawMessage()
    {
        using var ctx = new ToolFormattingTestContext();
        ctx.Client.RefreshResultToReturn = new UnityRefreshResult
        {
            Success = false,
            Message = "Internal compiler crash occurred during Roslyn emit."
        };

        var result = await ctx.Tools.UnityRefreshAsync();

        Assert.True(result.IsError);
        string text = ToolFormattingTestContext.GetResultText(result);
        Assert.Contains("Internal compiler crash occurred during Roslyn emit.", text);
        Assert.EndsWith("Error: Unity compilation failed.", text);
    }

    [Fact]
    public async Task UnityEval_VoidWithNoLogs_ReturnsPlaceholderMessage()
    {
        using var ctx = new ToolFormattingTestContext();
        ctx.Client.EvalResultToReturn = new UnityEvalResult
        {
            Success = true,
            Payload = null,
            Logs = []
        };

        var result = await ctx.Tools.UnityEvalAsync("Time.timeScale = 1.0f;");

        Assert.False(result.IsError);
        Assert.Equal("(Evaluation completed without a return statement. Use 'return <expr>;' to return a value.)", ToolFormattingTestContext.GetResultText(result));
    }

    [Fact]
    public async Task UnityEval_WhenLogsAreNull_ReturnsInterruptedMessageWithoutThrowing()
    {
        using var ctx = new ToolFormattingTestContext();
        ctx.Client.EvalResultToReturn = new UnityEvalResult
        {
            Success = false,
            Interrupted = true,
            Message = "Command interrupted by Unity recompilation.",
            Logs = null!
        };

        var result = await ctx.Tools.UnityEvalAsync("return 42;");

        Assert.True(result.IsError);
        Assert.Equal("Command interrupted by Unity recompilation.", ToolFormattingTestContext.GetResultText(result));
    }

    [Fact]
    public async Task UnityEval_WithNullReturnPayload_ReturnsNullLiteral()
    {
        using var ctx = new ToolFormattingTestContext();
        ctx.Client.EvalResultToReturn = new UnityEvalResult
        {
            Success = true,
            Payload = "null",
            Logs = []
        };

        var result = await ctx.Tools.UnityEvalAsync("return null;");

        Assert.False(result.IsError);
        Assert.Equal("null", ToolFormattingTestContext.GetResultText(result));
        Assert.DoesNotContain("Logs:", ToolFormattingTestContext.GetResultText(result));
        Assert.DoesNotContain("Result:", ToolFormattingTestContext.GetResultText(result));
    }

    [Fact]
    public async Task UnityEval_WithPayload_ReturnsPayload()
    {
        using var ctx = new ToolFormattingTestContext();
        ctx.Client.EvalResultToReturn = new UnityEvalResult
        {
            Success = true,
            Payload = "100",
            Logs = []
        };

        var result = await ctx.Tools.UnityEvalAsync("return 50 * 2;");

        Assert.False(result.IsError);
        Assert.Equal("100", ToolFormattingTestContext.GetResultText(result));
        Assert.DoesNotContain("Logs:", ToolFormattingTestContext.GetResultText(result));
        Assert.DoesNotContain("Result:", ToolFormattingTestContext.GetResultText(result));
    }

    [Fact]
    public async Task UnityEval_WithLogs_ReturnsLogs()
    {
        using var ctx = new ToolFormattingTestContext();
        ctx.Client.EvalResultToReturn = new UnityEvalResult
        {
            Success = true,
            Payload = null,
            Logs = [new ConsoleLogEntry { LogType = "Log", Message = "Logged message from snippet" }]
        };

        var result = await ctx.Tools.UnityEvalAsync("Debug.Log(\"Logged message from snippet\");");

        Assert.False(result.IsError);
        Assert.Equal("Logged message from snippet", ToolFormattingTestContext.GetResultText(result));
    }

    [Fact]
    public async Task UnityEval_WithLogsAndPayload_SeparatesLogsAndResult()
    {
        using var ctx = new ToolFormattingTestContext();
        ctx.Client.EvalResultToReturn = new UnityEvalResult
        {
            Success = true,
            Payload = "42",
            Logs =
            [
                new ConsoleLogEntry { LogType = "Log", Message = "Calculating value..." },
                new ConsoleLogEntry { LogType = "Warning", Message = "Calculation took longer than expected" }
            ],
            Duration = 0.05
        };

        var result = await ctx.Tools.UnityEvalAsync("Debug.Log(\"Calculating value...\"); return 42;");

        Assert.False(result.IsError);
        string text = ToolFormattingTestContext.GetResultText(result);
        Assert.StartsWith("Result:", text);
        Assert.Contains("42", text);
        Assert.Contains("Logs:", text);
        Assert.Contains("Calculating value...", text);
        Assert.Contains("[Warning] Calculation took longer than expected", text);
        Assert.True(text.IndexOf("Result:") < text.IndexOf("Logs:"), "Result must precede Logs");
    }

    [Fact]
    public async Task UnityEval_StructuredJsonInContentBlock1()
    {
        using var ctx = new ToolFormattingTestContext();
        ctx.Client.EvalResultToReturn = new UnityEvalResult
        {
            Success = true,
            Payload = "42",
            Duration = 0.05,
            Logs = [new ConsoleLogEntry { LogType = "Log", Message = "step 1" }]
        };

        var result = await ctx.Tools.UnityEvalAsync("return 42;");

        Assert.False(result.IsError);
        Assert.Single(result.Content);
        Assert.True(result.Content[0] is TextContentBlock);

        string text = ToolFormattingTestContext.GetResultText(result);
        Assert.Contains("step 1", text);
        Assert.Contains("42", text);
    }

    [Fact]
    public async Task UnityEval_FailureWithLogs_SeparatesLogsAndError()
    {
        using var ctx = new ToolFormattingTestContext();
        ctx.Client.EvalResultToReturn = new UnityEvalResult
        {
            Success = false,
            Message = "NullReferenceException: Object reference not set to an instance of an object",
            Logs = [new ConsoleLogEntry { LogType = "Error", Message = "Failed to locate target" }]
        };

        var result = await ctx.Tools.UnityEvalAsync("return GameObject.Find(\"Missing\").name;");

        Assert.True(result.IsError);
        Assert.Single(result.Content);
        string text = ToolFormattingTestContext.GetResultText(result);
        Assert.StartsWith("Error:", text);
        Assert.Contains("NullReferenceException:", text);
        Assert.Contains("Logs:", text);
        Assert.Contains("[Error] Failed to locate target", text);
        Assert.True(text.IndexOf("Error:") < text.IndexOf("Logs:"), "Error must precede Logs");
    }

    [Fact]
    public async Task UnityEval_WhenPreRefreshHasCompilerDiagnostics_FormatsStructuredDiagnostics()
    {
        using var ctx = new ToolFormattingTestContext();
        ctx.Client.EvalResultToReturn = new UnityEvalResult
        {
            Success = false,
            Message = "Assets/Scripts/Player.cs(42,15): error CS0103: The name 'speed' does not exist in the current context"
        };

        var result = await ctx.Tools.UnityEvalAsync("return 42;");

        Assert.True(result.IsError);
        string text = ToolFormattingTestContext.GetResultText(result);
        Assert.Contains("Assets/Scripts/Player.cs#L42", text);
        Assert.Contains("error CS0103: The name 'speed' does not exist in the current context", text);
        Assert.EndsWith("Evaluation aborted: Project script compilation failed.", text);
    }

    [Fact]
    public async Task UnityEval_WhenSnippetCompilationFails_FormatsSyntheticLocationAndSnippetTrailer()
    {
        using var ctx = new ToolFormattingTestContext();
        ctx.Client.EvalResultToReturn = new UnityEvalResult
        {
            Success = false,
            Message = "eval(1,5): error CS0103: The name 'xyz' does not exist in the current context"
        };

        var result = await ctx.Tools.UnityEvalAsync("xyz;");

        Assert.True(result.IsError);
        string text = ToolFormattingTestContext.GetResultText(result);
        Assert.Contains("• snippet line 1, col 5: error CS0103: The name 'xyz' does not exist in the current context", text);
        Assert.DoesNotContain("file:///", text);
        Assert.DoesNotContain("eval#", text);
        Assert.EndsWith("Evaluation aborted: Dynamic snippet compilation failed.", text);
    }

    [Fact]
    public async Task UnityEval_WhenSnippetCompilationFailsWithMultipleErrorsAndWarnings_FormatsAllDiagnostics()
    {
        using var ctx = new ToolFormattingTestContext();
        ctx.Client.EvalResultToReturn = new UnityEvalResult
        {
            Success = false,
            Message = "eval(1,4): error CS1002: ; expected | eval(1,1): error CS0103: The name 'xyz' does not exist in the current context | eval(2,10): warning CS0219: Variable 'foo' unused"
        };

        var result = await ctx.Tools.UnityEvalAsync("xyz");

        Assert.True(result.IsError);
        string text = ToolFormattingTestContext.GetResultText(result);
        Assert.Contains("Warnings:", text);
        Assert.Contains("• snippet line 2, col 10: warning CS0219: Variable 'foo' unused", text);
        Assert.Contains("Errors:", text);
        Assert.Contains("• snippet line 1, col 4: error CS1002: ; expected", text);
        Assert.Contains("• snippet line 1, col 1: error CS0103: The name 'xyz' does not exist in the current context", text);
        Assert.DoesNotContain("file:///", text);
        Assert.EndsWith("Evaluation aborted: Dynamic snippet compilation failed.", text);
    }

    [Fact]
    public async Task UnityEval_WhenSnippetCompilationFailsWithCS0104_AppendsAmbiguousSymbolHint()
    {
        using var ctx = new ToolFormattingTestContext();
        ctx.Client.EvalResultToReturn = new UnityEvalResult
        {
            Success = false,
            Message = "eval(1,8): error CS0104: 'Random' is an ambiguous reference between 'UnityEngine.Random' and 'System.Random'"
        };

        var result = await ctx.Tools.UnityEvalAsync("return Random.Range(0, 1);");

        Assert.True(result.IsError);
        string text = ToolFormattingTestContext.GetResultText(result);
        Assert.Contains("• snippet line 1, col 8: error CS0104: 'Random' is an ambiguous reference between 'UnityEngine.Random' and 'System.Random'", text);
        Assert.Contains("Hint: Ambiguous symbol across pre-imported namespaces", text);
        Assert.EndsWith("Evaluation aborted: Dynamic snippet compilation failed.\nHint: Ambiguous symbol across pre-imported namespaces (System, System.Collections.Generic, System.Linq, UnityEngine, UnityEditor, UnityLeanMcp). Qualify the type (e.g., UnityEngine.Object, UnityEngine.Random, UnityEngine.Debug) or add a 'using' alias.", text);
    }

    [Theory]
    [InlineData("eval(1,4): error CS1002: ; expected", "int x = 1")]
    [InlineData("eval(1,8): error CS0103: The name 'Path' does not exist in the current context", "return Path.Combine(\"a\", \"b\");")]
    public async Task UnityEval_WhenSnippetCompilationFailsWithoutCS0104_DoesNotAppendAmbiguousSymbolHint(string errorMessage, string code)
    {
        using var ctx = new ToolFormattingTestContext();
        ctx.Client.EvalResultToReturn = new UnityEvalResult
        {
            Success = false,
            Message = errorMessage
        };

        var result = await ctx.Tools.UnityEvalAsync(code);

        Assert.True(result.IsError);
        string text = ToolFormattingTestContext.GetResultText(result);
        Assert.DoesNotContain("Hint: Ambiguous symbol", text);
        Assert.EndsWith("Evaluation aborted: Dynamic snippet compilation failed.", text);
    }

    [Fact]
    public async Task UnityEval_WhenProjectCompilationFailsWithCS0104_DoesNotAppendAmbiguousSymbolHint()
    {
        using var ctx = new ToolFormattingTestContext();
        ctx.Client.EvalResultToReturn = new UnityEvalResult
        {
            Success = false,
            Message = "Assets/Scripts/Player.cs(10,5): error CS0104: 'Object' is an ambiguous reference between 'UnityEngine.Object' and 'System.Object'"
        };

        var result = await ctx.Tools.UnityEvalAsync("return 42;");

        Assert.True(result.IsError);
        string text = ToolFormattingTestContext.GetResultText(result);
        Assert.Contains("Assets/Scripts/Player.cs", text);
        Assert.DoesNotContain("Hint: Ambiguous symbol", text);
        Assert.EndsWith("Evaluation aborted: Project script compilation failed.", text);
    }

    [Fact]
    public async Task UnityTools_UsesInjectedPathResolver_ForRefreshEvalAndTestRuns()
    {
        string pmDir = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "pm_root_" + Guid.NewGuid().ToString("N")));
        string customDir = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "custom_root_" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(Path.Combine(pmDir, "Temp"));
        Directory.CreateDirectory(Path.Combine(customDir, "Temp"));

        try
        {
            var pmPathResolver = new UnityPathResolver(pmDir);
            var customPathResolver = new UnityPathResolver(customDir);
            var pm = new FakeUnityProcessManager(pmPathResolver);
            var client = new FakeUnityClient(pm, pmPathResolver);
            var tools = new UnityTools(client, customPathResolver);

            string customRootNormalized = customPathResolver.ProjectRoot.Replace('\\', '/');
            string pmRootNormalized = pmPathResolver.ProjectRoot.Replace('\\', '/');

            client.RefreshResultToReturn = new UnityRefreshResult
            {
                Success = false,
                Message = "Assets/Scripts/Player.cs(10,5): error CS0103: The name 'speed' does not exist in the current context"
            };
            var refreshResult = await tools.UnityRefreshAsync();
            string refreshText = ToolFormattingTestContext.GetResultText(refreshResult);
            Assert.Contains(customRootNormalized, refreshText);
            Assert.DoesNotContain(pmRootNormalized, refreshText);

            client.EvalResultToReturn = new UnityEvalResult
            {
                Success = false,
                Message = "Assets/Scripts/Player.cs(10,5): error CS0103: The name 'speed' does not exist in the current context"
            };
            var evalResult = await tools.UnityEvalAsync("var x = 1;");
            string evalText = ToolFormattingTestContext.GetResultText(evalResult);
            Assert.Contains(customRootNormalized, evalText);
            Assert.DoesNotContain(pmRootNormalized, evalText);

            client.TestRunResultToReturn = new UnityTestRunResult
            {
                Success = false,
                ResultState = "CompileError",
                Message = "Assets/Scripts/Player.cs(10,5): error CS0103: The name 'speed' does not exist in the current context"
            };
            var testCompileResult = await tools.UnityTestAsync(mode: UnityTestMode.EditMode);
            string testCompileText = ToolFormattingTestContext.GetResultText(testCompileResult);
            Assert.Contains(customRootNormalized, testCompileText);
            Assert.DoesNotContain(pmRootNormalized, testCompileText);

            client.TestRunResultToReturn = new UnityTestRunResult
            {
                Success = false,
                FailCount = 1,
                ResultState = "Failed",
                FailedTests =
                [
                    new FailedTestInfo
                    {
                        Name = "PlayerTest",
                        FullName = "Tests.PlayerTest",
                        Message = "Expected 10 but was 0",
                        StackTrace = "at Tests.PlayerTest.Run() in Assets/Tests/PlayerTest.cs:line 42",
                        Duration = 0.01
                    }
                ]
            };
            var testRunResult = await tools.UnityTestAsync(mode: UnityTestMode.EditMode);
            string testRunText = ToolFormattingTestContext.GetResultText(testRunResult);
            Assert.Contains(customRootNormalized, testRunText);
            Assert.DoesNotContain(pmRootNormalized, testRunText);
        }
        finally
        {
            try { Directory.Delete(pmDir, true); } catch { }
            try { Directory.Delete(customDir, true); } catch { }
        }
    }

    [Fact]
    public async Task UnityEval_WhenCompilationFails_ParsesBoundedDiagnosticsOnce()
    {
        using var ctx = new ToolFormattingTestContext();
        var spyFormatter = new CountingDiagnosticFormatter();
        var tools = new UnityTools(ctx.Client, ctx.PathResolver, spyFormatter);
        string hugePrefix = "eval(1,5): error CS0104: 'Object' is an ambiguous reference between 'UnityEngine.Object' and 'System.Object'\n";
        string oversizedError = hugePrefix + new string('X', McpOutputLimits.MaxFormattedOutputCharacters * 2);

        ctx.Client.EvalResultToReturn = new UnityEvalResult
        {
            Success = false,
            Message = oversizedError
        };

        var result = await tools.UnityEvalAsync("return typeof(Object);");

        Assert.True(result.IsError);
        Assert.Equal(1, spyFormatter.ParseCallCount);
        Assert.NotNull(spyFormatter.LastParsedInput);
        Assert.True(spyFormatter.LastParsedInput!.Length <= McpOutputLimits.MaxFormattedOutputCharacters);
        string text = ToolFormattingTestContext.GetResultText(result);
        Assert.Contains("Hint: Ambiguous symbol", text);
    }

    [Fact]
    public async Task UnityEval_PlayModeBypass_WithPayload_PrependsNotice()
    {
        using var ctx = new ToolFormattingTestContext();
        ctx.Client.EvalResultToReturn = new UnityEvalResult
        {
            Success = true,
            Payload = "100",
            Logs = [],
            RefreshSkippedDueToPlayMode = true
        };

        var result = await ctx.Tools.UnityEvalAsync("return 50 * 2;");

        Assert.False(result.IsError);
        string text = ToolFormattingTestContext.GetResultText(result);
        Assert.StartsWith(UnityToolOutputFormatter.PlayModeRefreshBypassNotice, text);
        Assert.Contains("100", text);
    }

    [Fact]
    public async Task UnityEval_PlayModeBypass_WithPayloadAndLogs_PrependsNoticeBeforeResultAndLogs()
    {
        using var ctx = new ToolFormattingTestContext();
        ctx.Client.EvalResultToReturn = new UnityEvalResult
        {
            Success = true,
            Payload = "42",
            Logs = [new ConsoleLogEntry { LogType = "Log", Message = "step 1" }],
            RefreshSkippedDueToPlayMode = true
        };

        var result = await ctx.Tools.UnityEvalAsync("Debug.Log(\"step 1\"); return 42;");

        Assert.False(result.IsError);
        string text = ToolFormattingTestContext.GetResultText(result);
        Assert.StartsWith(UnityToolOutputFormatter.PlayModeRefreshBypassNotice, text);
        Assert.Contains("Result:", text);
        Assert.Contains("42", text);
        Assert.Contains("Logs:", text);
        Assert.Contains("step 1", text);
        Assert.True(text.IndexOf(UnityToolOutputFormatter.PlayModeRefreshBypassNotice, StringComparison.Ordinal)
            < text.IndexOf("Result:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnityEval_PlayModeBypass_WithLogsOnly_PrependsNotice()
    {
        using var ctx = new ToolFormattingTestContext();
        ctx.Client.EvalResultToReturn = new UnityEvalResult
        {
            Success = true,
            Payload = null,
            Logs = [new ConsoleLogEntry { LogType = "Log", Message = "Logged message from PlayMode" }],
            RefreshSkippedDueToPlayMode = true
        };

        var result = await ctx.Tools.UnityEvalAsync("Debug.Log(\"Logged message from PlayMode\");");

        Assert.False(result.IsError);
        string text = ToolFormattingTestContext.GetResultText(result);
        Assert.StartsWith(UnityToolOutputFormatter.PlayModeRefreshBypassNotice, text);
        Assert.Contains("Logged message from PlayMode", text);
    }

    [Fact]
    public async Task UnityEval_PlayModeBypass_WithNoReturn_PrependsNotice()
    {
        using var ctx = new ToolFormattingTestContext();
        ctx.Client.EvalResultToReturn = new UnityEvalResult
        {
            Success = true,
            Payload = null,
            Logs = [],
            RefreshSkippedDueToPlayMode = true
        };

        var result = await ctx.Tools.UnityEvalAsync("var x = 1;");

        Assert.False(result.IsError);
        string text = ToolFormattingTestContext.GetResultText(result);
        Assert.StartsWith(UnityToolOutputFormatter.PlayModeRefreshBypassNotice, text);
        Assert.Contains("(Evaluation completed without a return statement. Use 'return <expr>;' to return a value.)", text);
    }

    [Fact]
    public async Task UnityEval_PlayModeBypass_OnError_PrependsNotice()
    {
        using var ctx = new ToolFormattingTestContext();
        ctx.Client.EvalResultToReturn = new UnityEvalResult
        {
            Success = false,
            Message = "NullReferenceException: Object reference not set",
            Logs = [new ConsoleLogEntry { LogType = "Error", Message = "Failed in PlayMode" }],
            RefreshSkippedDueToPlayMode = true
        };

        var result = await ctx.Tools.UnityEvalAsync("return GameObject.Find(\"Missing\").name;");

        Assert.True(result.IsError);
        string text = ToolFormattingTestContext.GetResultText(result);
        Assert.StartsWith(UnityToolOutputFormatter.PlayModeRefreshBypassNotice, text);
        Assert.Contains("Error:", text);
        Assert.Contains("NullReferenceException:", text);
        Assert.Contains("Logs:", text);
        Assert.Contains("Failed in PlayMode", text);
    }

    [Fact]
    public async Task UnityEval_PlayModeBypass_OversizedPayload_PreservesNoticeWithinOutputBudget()
    {
        using var ctx = new ToolFormattingTestContext();
        string oversizedPayload = new string('A', McpOutputLimits.MaxFormattedOutputCharacters * 2);
        ctx.Client.EvalResultToReturn = new UnityEvalResult
        {
            Success = true,
            Payload = oversizedPayload,
            Logs = [],
            RefreshSkippedDueToPlayMode = true
        };

        var result = await ctx.Tools.UnityEvalAsync("return new string('A', 131072);");

        Assert.False(result.IsError);
        string text = ToolFormattingTestContext.GetResultText(result);
        Assert.StartsWith(UnityToolOutputFormatter.PlayModeRefreshBypassNotice, text);
        Assert.True(text.Length <= McpOutputLimits.MaxFormattedOutputCharacters);
        Assert.Contains(McpOutputLimits.AggregateOutputTruncationMarker, text);
    }

    [Fact]
    public async Task UnityEval_RuntimeException_CompactsTailAndFormatsSnippetLine()
    {
        using var ctx = new ToolFormattingTestContext();
        ctx.Client.EvalResultToReturn = new UnityEvalResult
        {
            Success = false,
            Message = string.Join("\n", new[]
            {
                "System.InvalidOperationException: Bad state",
                "  at GameService.Process () [0x00010] in Assets/Scripts/GameService.cs:42",
                "  at __UnityLeanMcpEvalRunner.Run () [0x00005] in eval:line 3",
                "  at System.Reflection.RuntimeMethodInfo.Invoke (System.Object obj) [0x0006a] in <000>:0",
                "  at UnityLeanMcp.EvalHandler.Execute (System.String code) [0x00050] in Packages/com.pereviader.unityleanmcp/Editor/EvalHandler.cs:120"
            })
        };

        var result = await ctx.Tools.UnityEvalAsync("GameService.Process();");

        Assert.True(result.IsError);
        string text = ToolFormattingTestContext.GetResultText(result);
        Assert.Contains("System.InvalidOperationException: Bad state", text);
        Assert.Contains("at GameService.Process () [0x00010] in Assets/Scripts/GameService.cs:42", text);
        Assert.Contains("at __UnityLeanMcpEvalRunner.Run () [0x00005] in snippet line 3", text);
        Assert.DoesNotContain("RuntimeMethodInfo.Invoke", text);
        Assert.DoesNotContain("UnityLeanMcp.EvalHandler", text);
    }

    [Fact]
    public async Task UnityEval_NonExceptionErrorMessage_RemainsUntouched()
    {
        using var ctx = new ToolFormattingTestContext();
        const string message = "Unity is busy with another operation: BUSY test";
        ctx.Client.EvalResultToReturn = new UnityEvalResult
        {
            Success = false,
            Message = message
        };

        var result = await ctx.Tools.UnityEvalAsync("return 1;");

        Assert.True(result.IsError);
        string text = ToolFormattingTestContext.GetResultText(result);
        Assert.Equal(message, text);
    }
}
