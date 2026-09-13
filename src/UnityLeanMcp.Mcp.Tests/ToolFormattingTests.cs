using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit;

namespace UnityLeanMcp.Mcp.Tests;

[Trait("Category", "Unit")]
public class ToolFormattingTests
{
    private sealed class FakeUnityProcessManager : UnityProcessManager
    {
        public bool Running { get; set; }
        public int? Pid { get; set; }
        public string Mode { get; set; } = "Batchmode";
        public bool StopSuccess { get; set; } = true;

        public FakeUnityProcessManager(IUnityPathResolver pathResolver)
            : base(pathResolver, NullLogger<UnityProcessManager>.Instance)
        {
        }

        public FakeUnityProcessManager(string projectRoot)
            : this(new UnityPathResolver(projectRoot))
        {
        }

        public override bool IsUnityRunning(out int? processId)
        {
            processId = Running ? Pid : null;
            return Running;
        }

        public override string GetUnityMode(int? pid = null) => Mode;

        public override Task<bool> StopUnityAsync(bool force = false, CancellationToken cancellationToken = default)
        {
            if (Mode == "GUI" && !force)
            {
                return Task.FromResult(false);
            }

            if (StopSuccess)
            {
                Running = false;
            }
            return Task.FromResult(StopSuccess);
        }

        public override Task<bool> StartUnityAsync(CancellationToken cancellationToken = default)
        {
            Running = true;
            return Task.FromResult(true);
        }

        public override Task<bool> WaitForHealthyAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(true);
        }
    }

    private sealed class FakeUnityClient : UnityClient
    {
        public string StatusToReturn { get; set; } = "Ready";
        public UnityRefreshResult RefreshResultToReturn { get; set; } = new();
        public UnityEvalResult EvalResultToReturn { get; set; } = new();
        public UnityExecuteResult ExecuteResultToReturn { get; set; } = new();
        public UnityTestRunResult TestRunResultToReturn { get; set; } = new();

        public FakeUnityClient(UnityProcessManager pm, IUnityPathResolver pathResolver)
            : base(pm, pathResolver, NullLogger<UnityClient>.Instance)
        {
        }

        public FakeUnityClient(UnityProcessManager pm)
            : this(pm, pm.PathResolver)
        {
        }

        public override Task<string> GetStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(StatusToReturn);

        public override Task<UnityRefreshResult> RefreshAsync(
            bool isRecompile = false,
            IProgress<ProgressNotificationValue>? progress = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(RefreshResultToReturn);

        public override Task<UnityEvalResult> EvalAsync(
            string code,
            IProgress<ProgressNotificationValue>? progress = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(EvalResultToReturn);

        [Obsolete]
        public override Task<UnityExecuteResult> ExecuteMethodAsync(
            string methodName,
            string[]? args,
            IProgress<ProgressNotificationValue>? progress = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(ExecuteResultToReturn);

        public string[]? LastTestNames { get; private set; }
        public string[]? LastGroupNames { get; private set; }
        public string[]? LastCategoryNames { get; private set; }
        public string[]? LastAssemblyNames { get; private set; }
        public string? LastMode { get; private set; }
        public bool LastFailedOnly { get; private set; }

        public override Task<UnityTestRunResult> RunTestsAsync(
            string? filter,
            string? category,
            string? mode,
            bool failedOnly = false,
            IProgress<ProgressNotificationValue>? progress = null,
            CancellationToken cancellationToken = default) =>
            RunTestsAsync(
                testNames: null,
                groupNames: !string.IsNullOrEmpty(filter) ? [filter] : null,
                categoryNames: !string.IsNullOrEmpty(category) ? [category] : null,
                assemblyNames: null,
                mode: mode,
                failedOnly: failedOnly,
                progress: progress,
                cancellationToken: cancellationToken);

        public override Task<UnityTestRunResult> RunTestsAsync(
            string[]? testNames,
            string[]? groupNames,
            string[]? categoryNames,
            string[]? assemblyNames,
            string? mode,
            bool failedOnly = false,
            IProgress<ProgressNotificationValue>? progress = null,
            CancellationToken cancellationToken = default)
        {
            LastTestNames = testNames;
            LastGroupNames = groupNames;
            LastCategoryNames = categoryNames;
            LastAssemblyNames = assemblyNames;
            LastMode = mode;
            LastFailedOnly = failedOnly;
            return Task.FromResult(TestRunResultToReturn);
        }
    }

    private static (string tempDir, FakeUnityProcessManager pm, FakeUnityClient client, UnityTools tools) CreateTestContext()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "unity_fmt_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(tempDir, "Temp"));
        Directory.CreateDirectory(Path.Combine(tempDir, "ProjectSettings"));

        var pathResolver = new UnityPathResolver(tempDir);
        var pm = new FakeUnityProcessManager(pathResolver);
        var client = new FakeUnityClient(pm, pathResolver);
        var tools = new UnityTools(client, pm, pathResolver);

        return (tempDir, pm, client, tools);
    }

    private static string GetResultText(CallToolResult result)
    {
        return result.Content.Count > 0 && result.Content[0] is TextContentBlock textBlock
            ? textBlock.Text
            : "";
    }


    [Fact]
    public void UnityProcessManager_GetUnityMode_DistinguishesBatchmodeAndGui()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "unity_mode_test_" + Guid.NewGuid().ToString("N"));
        string tempSubDir = Path.Combine(tempDir, "Temp");
        Directory.CreateDirectory(tempSubDir);
        try
        {
            var realPm = new UnityProcessManager(tempDir, NullLogger<UnityProcessManager>.Instance);
            int testPid = Environment.ProcessId;

            // When PidFile exists with matching PID -> Batchmode
            File.WriteAllText(realPm.PidFile, testPid.ToString());
            Assert.Equal("Batchmode", realPm.GetUnityMode(testPid));

            // When PidFile does not exist -> GUI
            File.Delete(realPm.PidFile);
            Assert.Equal("GUI", realPm.GetUnityMode(testPid));
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    // ==========================================
    // 2. unity_refresh tests (including clean: true)
    // ==========================================

    [Fact]
    public async Task UnityRefresh_WhenClean_ReturnsZeroErrorsMessage()
    {
        var (tempDir, pm, client, tools) = CreateTestContext();
        try
        {
            client.RefreshResultToReturn = new UnityRefreshResult
            {
                Success = true,
                Message = ""
            };

            var result = await tools.UnityRefreshAsync();

            Assert.False(result.IsError);
            Assert.Equal("AssetDatabase refresh completed with 0 errors.", GetResultText(result));
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task UnityRefresh_WhenCleanFlag_ReturnsZeroErrorsMessage()
    {
        var (tempDir, pm, client, tools) = CreateTestContext();
        try
        {
            client.RefreshResultToReturn = new UnityRefreshResult
            {
                Success = true,
                Message = ""
            };

            var result = await tools.UnityRefreshAsync(clean: true);

            Assert.False(result.IsError);
            Assert.Equal("Clean script recompilation completed with 0 errors.", GetResultText(result));
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task UnityRefresh_WhenBusy_ReportsBusyWithoutClaimingCompilationFailed()
    {
        var (tempDir, pm, client, tools) = CreateTestContext();
        try
        {
            client.RefreshResultToReturn = new UnityRefreshResult
            {
                Success = false,
                Message = "Unity is busy with another operation: BUSY test"
            };

            var result = await tools.UnityRefreshAsync();

            Assert.True(result.IsError);
            string text = GetResultText(result);
            Assert.Equal("Unity is busy with another operation: BUSY test", text);
            Assert.DoesNotContain("Error: Unity compilation failed.", text);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task UnityRefresh_WhenCleanAndBusy_ReportsBusyWithoutClaimingRecompilationFailed()
    {
        var (tempDir, pm, client, tools) = CreateTestContext();
        try
        {
            client.RefreshResultToReturn = new UnityRefreshResult
            {
                Success = false,
                Message = "Unity is busy with another operation: BUSY test"
            };

            var result = await tools.UnityRefreshAsync(clean: true);

            Assert.True(result.IsError);
            string text = GetResultText(result);
            Assert.Equal("Unity is busy with another operation: BUSY test", text);
            Assert.DoesNotContain("Error: Unity recompilation failed.", text);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task UnityRefresh_WhenInterrupted_ReportsInterruptionCleanly()
    {
        var (tempDir, pm, client, tools) = CreateTestContext();
        try
        {
            client.RefreshResultToReturn = new UnityRefreshResult
            {
                Success = false,
                Interrupted = true,
                Message = "Unity operation was interrupted by domain reload or editor restart."
            };

            var result = await tools.UnityRefreshAsync();

            Assert.True(result.IsError);
            string text = GetResultText(result);
            Assert.Equal("Unity operation was interrupted by domain reload or editor restart.", text);
            Assert.DoesNotContain("Error: Unity compilation failed.", text);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task UnityRefresh_WhenCompilationFails_ReportsDiagnosticsAndError()
    {
        var (tempDir, pm, client, tools) = CreateTestContext();
        try
        {
            client.RefreshResultToReturn = new UnityRefreshResult
            {
                Success = false,
                Interrupted = false,
                Message = "Assets/Scripts/Foo.cs(10,5): error CS0103: The name 'bar' does not exist in the current context"
            };

            var result = await tools.UnityRefreshAsync();

            Assert.True(result.IsError);
            string text = GetResultText(result);
            Assert.Contains("error CS0103", text);
            Assert.Contains("Error: Unity compilation failed.", text);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task UnityRefresh_WhenCleanAndCompilationFails_ReportsDiagnosticsAndRecompilationError()
    {
        var (tempDir, pm, client, tools) = CreateTestContext();
        try
        {
            client.RefreshResultToReturn = new UnityRefreshResult
            {
                Success = false,
                Interrupted = false,
                Message = "Assets/Scripts/Foo.cs(10,5): error CS0103: The name 'bar' does not exist in the current context"
            };

            var result = await tools.UnityRefreshAsync(clean: true);

            Assert.True(result.IsError);
            string text = GetResultText(result);
            Assert.Contains("error CS0103", text);
            Assert.Contains("Error: Unity recompilation failed.", text);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }


    // ==========================================
    // 4. unity_eval tests
    // ==========================================

    [Fact]
    public async Task UnityEval_VoidWithNoLogs_ReturnsPlaceholderMessage()
    {
        var (tempDir, pm, client, tools) = CreateTestContext();
        try
        {
            client.EvalResultToReturn = new UnityEvalResult
            {
                Success = true,
                Payload = null,
                Logs = []
            };

            var result = await tools.UnityEvalAsync("Time.timeScale = 1.0f;");

            Assert.False(result.IsError);
            Assert.Equal("(Evaluation completed without a return statement. Use 'return <expr>;' to return a value.)", GetResultText(result));
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task UnityEval_WithNullReturnPayload_ReturnsNullLiteral()
    {
        var (tempDir, pm, client, tools) = CreateTestContext();
        try
        {
            client.EvalResultToReturn = new UnityEvalResult
            {
                Success = true,
                Payload = "null",
                Logs = []
            };

            var result = await tools.UnityEvalAsync("return null;");

            Assert.False(result.IsError);
            Assert.Equal("null", GetResultText(result));
            Assert.DoesNotContain("Logs:", GetResultText(result));
            Assert.DoesNotContain("Result:", GetResultText(result));
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task UnityEval_WithPayload_ReturnsPayload()
    {
        var (tempDir, pm, client, tools) = CreateTestContext();
        try
        {
            client.EvalResultToReturn = new UnityEvalResult
            {
                Success = true,
                Payload = "100",
                Logs = []
            };

            var result = await tools.UnityEvalAsync("return 50 * 2;");

            Assert.False(result.IsError);
            Assert.Equal("100", GetResultText(result));
            Assert.DoesNotContain("Logs:", GetResultText(result));
            Assert.DoesNotContain("Result:", GetResultText(result));
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task UnityEval_WithLogs_ReturnsLogs()
    {
        var (tempDir, pm, client, tools) = CreateTestContext();
        try
        {
            client.EvalResultToReturn = new UnityEvalResult
            {
                Success = true,
                Payload = null,
                Logs = [new ConsoleLogEntry { LogType = "Log", Message = "Logged message from snippet" }]
            };

            var result = await tools.UnityEvalAsync("Debug.Log(\"Logged message from snippet\");");

            Assert.False(result.IsError);
            Assert.Equal("Logged message from snippet", GetResultText(result));
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task UnityEval_WithLogsAndPayload_SeparatesLogsAndResult()
    {
        var (tempDir, pm, client, tools) = CreateTestContext();
        try
        {
            client.EvalResultToReturn = new UnityEvalResult
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

            var result = await tools.UnityEvalAsync("Debug.Log(\"Calculating value...\"); return 42;");

            Assert.False(result.IsError);
            string text = GetResultText(result);
            Assert.StartsWith("Logs:", text);
            Assert.Contains("Calculating value...", text);
            Assert.Contains("[Warning] Calculation took longer than expected", text);
            Assert.Contains("Result:", text);
            Assert.EndsWith("42", text);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task UnityEval_StructuredJsonInContentBlock1()
    {
        var (tempDir, pm, client, tools) = CreateTestContext();
        try
        {
            client.EvalResultToReturn = new UnityEvalResult
            {
                Success = true,
                Payload = "42",
                Duration = 0.05,
                Logs = [new ConsoleLogEntry { LogType = "Log", Message = "step 1" }]
            };

            var result = await tools.UnityEvalAsync("return 42;");

            Assert.False(result.IsError);
            Assert.Single(result.Content);
            Assert.True(result.Content[0] is TextContentBlock);

            string text = GetResultText(result);
            Assert.Contains("step 1", text);
            Assert.Contains("42", text);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task UnityEval_FailureWithLogs_SeparatesLogsAndError()
    {
        var (tempDir, pm, client, tools) = CreateTestContext();
        try
        {
            client.EvalResultToReturn = new UnityEvalResult
            {
                Success = false,
                Message = "NullReferenceException: Object reference not set to an instance of an object",
                Logs = [new ConsoleLogEntry { LogType = "Error", Message = "Failed to locate target" }]
            };

            var result = await tools.UnityEvalAsync("return GameObject.Find(\"Missing\").name;");

            Assert.True(result.IsError);
            Assert.Single(result.Content);
            string text = GetResultText(result);
            Assert.StartsWith("Logs:", text);
            Assert.Contains("[Error] Failed to locate target", text);
            Assert.Contains("Error:", text);
            Assert.Contains("NullReferenceException:", text);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    // ==========================================
    // 5. unity_run_tests tests
    // ==========================================

    [Fact]
    public async Task UnityRunTests_CompileError_FormatsAsTestExecutionAborted()
    {
        var (tempDir, pm, client, tools) = CreateTestContext();
        try
        {
            client.TestRunResultToReturn = new UnityTestRunResult
            {
                Success = false,
                ResultState = "CompileError",
                FailCount = 0,
                PassCount = 0,
                SkipCount = 0,
                Message = "Assets/Scripts/Test.cs(12,8): error CS1002: ; expected"
            };

            var result = await tools.UnityRunTestsAsync();

            Assert.True(result.IsError);
            string text = GetResultText(result);
            Assert.EndsWith("Test execution aborted: Script compilation failed.", text);
            Assert.Contains("error CS1002", text);
            Assert.DoesNotContain("Tests Failed: 0 failed", text);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task UnityRunTests_MoreThan25Failures_CapsDetailedOutputAndSummarizesRemainder()
    {
        var (tempDir, pm, client, tools) = CreateTestContext();
        try
        {
            var failedTests = new List<FailedTestInfo>();
            for (int i = 0; i < 30; i++)
            {
                failedTests.Add(new FailedTestInfo
                {
                    Name = $"Test_Method_{i}",
                    FullName = $"MySuite.Test_Method_{i}",
                    Message = $"Assertion failed in test {i}",
                    StackTrace = $"at MySuite.Test_Method_{i}() line {i}",
                    Duration = 0.05
                });
            }

            client.TestRunResultToReturn = new UnityTestRunResult
            {
                Success = false,
                ResultState = "Failed",
                FailCount = 30,
                PassCount = 10,
                SkipCount = 2,
                FailedTests = failedTests
            };

            var result = await tools.UnityRunTestsAsync();

            Assert.True(result.IsError);
            string text = GetResultText(result);

            Assert.Contains("Tests Failed: 30 failed, 10 passed, 2 skipped.", text);
            Assert.Contains("• MySuite.Test_Method_0", text);
            Assert.Contains("• MySuite.Test_Method_24", text);
            Assert.DoesNotContain("• MySuite.Test_Method_25", text);
            Assert.Contains("... and 5 more failed test(s).", text);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task UnityRunTests_Under25Failures_ShowsAllWithoutSummaryLine()
    {
        var (tempDir, pm, client, tools) = CreateTestContext();
        try
        {
            var failedTests = new List<FailedTestInfo>();
            for (int i = 0; i < 3; i++)
            {
                failedTests.Add(new FailedTestInfo
                {
                    Name = $"Test_{i}",
                    FullName = $"Suite.Test_{i}",
                    Message = $"Fail {i}",
                    Duration = 0.01
                });
            }

            client.TestRunResultToReturn = new UnityTestRunResult
            {
                Success = false,
                ResultState = "Failed",
                FailCount = 3,
                PassCount = 5,
                SkipCount = 0,
                FailedTests = failedTests
            };

            var result = await tools.UnityRunTestsAsync();

            Assert.True(result.IsError);
            string text = GetResultText(result);

            Assert.Contains("• Suite.Test_0", text);
            Assert.Contains("• Suite.Test_1", text);
            Assert.Contains("• Suite.Test_2", text);
            Assert.DoesNotContain("more failed test(s).", text);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task UnityRunTests_WhenFilterSpecifiedAndZeroTestsRun_ReturnsErrorWithDescriptiveMessage()
    {
        var (tempDir, pm, client, tools) = CreateTestContext();
        try
        {
            client.TestRunResultToReturn = new UnityTestRunResult
            {
                Success = true,
                FailCount = 0,
                PassCount = 0,
                SkipCount = 0
            };

            var result = await tools.UnityRunTestsAsync(filter: "SomeFilter");

            Assert.True(result.IsError);
            string text = GetResultText(result);
            Assert.Contains("No tests found matching filter 'SomeFilter' (mode: all).", text);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task UnityRunTests_WhenCategorySpecifiedAndZeroTestsRun_ReturnsErrorWithDescriptiveMessage()
    {
        var (tempDir, pm, client, tools) = CreateTestContext();
        try
        {
            client.TestRunResultToReturn = new UnityTestRunResult
            {
                Success = true,
                FailCount = 0,
                PassCount = 0,
                SkipCount = 0
            };

            var result = await tools.UnityRunTestsAsync(category: "SomeCat");

            Assert.True(result.IsError);
            string text = GetResultText(result);
            Assert.Contains("No tests found matching category 'SomeCat' (mode: all).", text);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task UnityRunTests_WhenFilterAndCategorySpecifiedAndZeroTestsRun_ReturnsErrorWithBoth()
    {
        var (tempDir, pm, client, tools) = CreateTestContext();
        try
        {
            client.TestRunResultToReturn = new UnityTestRunResult
            {
                Success = true,
                FailCount = 0,
                PassCount = 0,
                SkipCount = 0
            };

            var result = await tools.UnityRunTestsAsync(filter: "SomeFilter", category: "SomeCat");

            Assert.True(result.IsError);
            string text = GetResultText(result);
            Assert.Contains("No tests found matching filter 'SomeFilter' and category 'SomeCat' (mode: all).", text);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task UnityRunTests_WhenUnfilteredAndZeroTestsRun_ReturnsSuccessWithEmptySuiteMessage()
    {
        var (tempDir, pm, client, tools) = CreateTestContext();
        try
        {
            client.TestRunResultToReturn = new UnityTestRunResult
            {
                Success = true,
                FailCount = 0,
                PassCount = 0,
                SkipCount = 0
            };

            var result = await tools.UnityRunTestsAsync();

            Assert.False(result.IsError);
            string text = GetResultText(result);
            Assert.Equal("Tests Passed: 0 passed, 0 skipped (no tests found in suite).", text);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task UnityRunTests_SingleStringParameters_PassThroughToClient()
    {
        var (tempDir, pm, client, tools) = CreateTestContext();
        try
        {
            client.TestRunResultToReturn = new UnityTestRunResult
            {
                Success = true,
                PassCount = 1
            };

            var result = await tools.UnityRunTestsAsync(
                testName: "MyNamespace.MyTestClass.MyMethod",
                group: "MyNamespace\\.MyTestClass",
                category: "Integration",
                assembly: "MyProject.Tests");

            Assert.False(result.IsError);
            Assert.NotNull(client.LastTestNames);
            Assert.Equal(["MyNamespace.MyTestClass.MyMethod"], client.LastTestNames);
            Assert.NotNull(client.LastGroupNames);
            Assert.Equal(["MyNamespace\\.MyTestClass"], client.LastGroupNames);
            Assert.NotNull(client.LastCategoryNames);
            Assert.Equal(["Integration"], client.LastCategoryNames);
            Assert.NotNull(client.LastAssemblyNames);
            Assert.Equal(["MyProject.Tests"], client.LastAssemblyNames);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task UnityRunTests_ArrayParameters_PassThroughToClientVerbatim()
    {
        var (tempDir, pm, client, tools) = CreateTestContext();
        try
        {
            client.TestRunResultToReturn = new UnityTestRunResult
            {
                Success = true,
                PassCount = 2
            };

            var testNames = new[] { "TestA", "TestB" };
            var groupNames = new[] { "GroupA.*", "GroupB.*" };
            var catNames = new[] { "Cat1", "Cat2" };
            var asmNames = new[] { "Asm1", "Asm2" };

            var result = await tools.UnityRunTestsAsync(
                testNames: testNames,
                groupNames: groupNames,
                categoryNames: catNames,
                assemblyNames: asmNames);

            Assert.False(result.IsError);
            Assert.Equal(testNames, client.LastTestNames);
            Assert.Equal(groupNames, client.LastGroupNames);
            Assert.Equal(catNames, client.LastCategoryNames);
            Assert.Equal(asmNames, client.LastAssemblyNames);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task UnityRunTests_SingleAndArrayCombined_DeDuplicatesAndPreservesOrder()
    {
        var (tempDir, pm, client, tools) = CreateTestContext();
        try
        {
            client.TestRunResultToReturn = new UnityTestRunResult
            {
                Success = true,
                PassCount = 2
            };

            var result = await tools.UnityRunTestsAsync(
                testName: "TestA",
                testNames: ["TestA", "TestB"]);

            Assert.False(result.IsError);
            Assert.NotNull(client.LastTestNames);
            Assert.Equal(["TestA", "TestB"], client.LastTestNames);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task UnityRunTests_LegacyFilterAndCategory_PassThroughToClient()
    {
        var (tempDir, pm, client, tools) = CreateTestContext();
        try
        {
            client.TestRunResultToReturn = new UnityTestRunResult
            {
                Success = true,
                PassCount = 1
            };

            var result = await tools.UnityRunTestsAsync(
                filter: "MyLegacyFilter",
                category: "MyLegacyCat");

            Assert.False(result.IsError);
            Assert.NotNull(client.LastGroupNames);
            Assert.Equal(["MyLegacyFilter"], client.LastGroupNames);
            Assert.NotNull(client.LastCategoryNames);
            Assert.Equal(["MyLegacyCat"], client.LastCategoryNames);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task UnityRunTests_WhenFailureOccursWithZeroTests_SurfacesFailureMessageInsteadOfNoTestsFound()
    {
        var (tempDir, pm, client, tools) = CreateTestContext();
        try
        {
            // Simulates Unity Test Runner failing with a regex or compilation error before executing any tests
            client.TestRunResultToReturn = new UnityTestRunResult
            {
                Success = false,
                FailCount = 0,
                PassCount = 0,
                SkipCount = 0,
                Message = "Regex parsing error: Quantifier * following nothing"
            };

            var result = await tools.UnityRunTestsAsync(group: "*Movement*");

            Assert.True(result.IsError);
            string text = GetResultText(result);
            Assert.Contains("Test run failed: Regex parsing error: Quantifier * following nothing", text);
            Assert.DoesNotContain("No tests found matching", text);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task UnityRunTests_WhenTestNamesSpecifiedAndZeroTestsRun_ReturnsDescriptiveErrorMessage()
    {
        var (tempDir, pm, client, tools) = CreateTestContext();
        try
        {
            client.TestRunResultToReturn = new UnityTestRunResult
            {
                Success = true,
                FailCount = 0,
                PassCount = 0,
                SkipCount = 0
            };

            var result = await tools.UnityRunTestsAsync(testNames: ["MyNamespace.MyTest"]);

            Assert.True(result.IsError);
            string text = GetResultText(result);
            Assert.Contains("No tests found matching testNames 'MyNamespace.MyTest' (mode: all).", text);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task UnityRunTests_WhenAssemblyNamesSpecifiedAndZeroTestsRun_ReturnsDescriptiveErrorMessage()
    {
        var (tempDir, pm, client, tools) = CreateTestContext();
        try
        {
            client.TestRunResultToReturn = new UnityTestRunResult
            {
                Success = true,
                FailCount = 0,
                PassCount = 0,
                SkipCount = 0
            };

            var result = await tools.UnityRunTestsAsync(assemblyNames: ["MyCompany.MyTests"]);

            Assert.True(result.IsError);
            string text = GetResultText(result);
            Assert.Contains("No tests found matching assemblyNames 'MyCompany.MyTests' (mode: all).", text);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    // ==========================================
    // 6. unity_stop tests
    // ==========================================

    [Fact]
    public async Task UnityStop_WhenNotRunning_ReturnsNotRunningMessageWithNoError()
    {
        var (tempDir, pm, client, tools) = CreateTestContext();
        try
        {
            pm.Running = false;

            var result = await tools.UnityStopAsync();

            Assert.False(result.IsError);
            Assert.Equal("Unity background instance is not running.", GetResultText(result));
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task UnityStop_WhenRunningAndStopped_ReturnsStopped()
    {
        var (tempDir, pm, client, tools) = CreateTestContext();
        try
        {
            pm.Running = true;
            pm.StopSuccess = true;

            var result = await tools.UnityStopAsync();

            Assert.False(result.IsError);
            Assert.Equal("Stopped.", GetResultText(result));
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task UnityStop_WhenRunningAndFailsToStop_ReturnsErrorMessage()
    {
        var (tempDir, pm, client, tools) = CreateTestContext();
        try
        {
            pm.Running = true;
            pm.StopSuccess = false;

            var result = await tools.UnityStopAsync();

            Assert.True(result.IsError);
            Assert.Equal("Error: Unity background instance could not be stopped.", GetResultText(result));
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task UnityStop_WhenGuiModeAndNotForced_ReturnsRefusalErrorMessage()
    {
        var (tempDir, pm, client, tools) = CreateTestContext();
        try
        {
            pm.Running = true;
            pm.Mode = "GUI";
            pm.StopSuccess = true;

            var result = await tools.UnityStopAsync(force: false);

            Assert.True(result.IsError);
            Assert.Equal("Refusing to stop Unity: The active Unity Editor is running in interactive GUI mode. Stopping it may lose unsaved user changes. Set 'force: true' to stop it anyway.", GetResultText(result));
            Assert.True(pm.Running);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task UnityStop_WhenGuiModeAndForced_StopsSuccessfully()
    {
        var (tempDir, pm, client, tools) = CreateTestContext();
        try
        {
            pm.Running = true;
            pm.Mode = "GUI";
            pm.StopSuccess = true;

            var result = await tools.UnityStopAsync(force: true);

            Assert.False(result.IsError);
            Assert.Equal("Stopped.", GetResultText(result));
            Assert.False(pm.Running);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    // ==========================================
    // 7. Structured Data & Diagnostics tests
    // ==========================================

    [Fact]
    public async Task UnityRunTests_ReturnsStructuredDataWithCountsAndFailures()
    {
        var (tempDir, pm, client, tools) = CreateTestContext();
        try
        {
            var failed = new List<FailedTestInfo>
            {
                new FailedTestInfo
                {
                    Name = "Test_AssertFailure",
                    FullName = "MySuite.Test_AssertFailure",
                    Duration = 0.25,
                    Message = "Expected 10 but got 5",
                    StackTrace = "  at MySuite.Test_AssertFailure () [0x00010] in Assets/Tests/Editor/DummyTest.cs:42\n"
                }
            };

            client.TestRunResultToReturn = new UnityTestRunResult
            {
                Success = false,
                ResultState = "Failed",
                PassCount = 7,
                FailCount = 1,
                SkipCount = 2,
                Duration = 1.85,
                FailedTests = failed
            };

            var result = await tools.UnityRunTestsAsync();

            Assert.True(result.IsError);
            Assert.Single(result.Content);

            string humanText = GetResultText(result);
            Assert.Contains("Tests Failed: 1 failed, 7 passed, 2 skipped.", humanText);
            Assert.Contains("• MySuite.Test_AssertFailure (0.250s)", humanText);
            Assert.Contains("Location: [Assets/Tests/Editor/DummyTest.cs:42](file:///", humanText);
            Assert.Contains("#L42)", humanText);
            Assert.Contains("Message: Expected 10 but got 5", humanText);
            Assert.Contains("Stack trace:", humanText);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task UnityRunTests_WhenStackTraceContainsRunnerPlumbing_SanitizesPlumbingFromOutput()
    {
        var (tempDir, pm, client, tools) = CreateTestContext();
        try
        {
            var failed = new List<FailedTestInfo>
            {
                new FailedTestInfo
                {
                    Name = "Test_AssertFailure",
                    FullName = "MySuite.Test_AssertFailure",
                    Duration = 0.25,
                    Message = "Expected 10 but got 5",
                    StackTrace = string.Join(Environment.NewLine, new[]
                    {
                        "  at MySuite.Test_AssertFailure () [0x00010] in Assets/Tests/Editor/DummyTest.cs:42",
                        "  at (wrapper managed-to-native) System.Reflection.RuntimeMethodInfo.InternalInvoke(System.Reflection.RuntimeMethodInfo,object,object[],System.Exception&)",
                        "  at System.Reflection.RuntimeMethodInfo.Invoke (System.Object obj, System.Reflection.BindingFlags invokeAttr, System.Reflection.Binder binder, System.Object[] parameters, System.Globalization.CultureInfo culture) [0x0006a] in <...>:0",
                        "  at NUnit.Framework.Internal.Commands.TestMethodCommand.Execute (NUnit.Framework.Internal.TestExecutionContext context) [0x0001c] in <...>:0",
                        "  at UnityEditor.TestTools.TestRunner.EditorEnumeratorTestWorkItem.Execute () [0x0003b] in <...>:0"
                    })
                }
            };

            client.TestRunResultToReturn = new UnityTestRunResult
            {
                Success = false,
                ResultState = "Failed",
                PassCount = 1,
                FailCount = 1,
                SkipCount = 0,
                Duration = 0.5,
                FailedTests = failed
            };

            var result = await tools.UnityRunTestsAsync();

            Assert.True(result.IsError);
            string humanText = GetResultText(result);
            Assert.Contains("MySuite.Test_AssertFailure ()", humanText);
            Assert.DoesNotContain("System.Reflection", humanText);
            Assert.DoesNotContain("TestMethodCommand", humanText);
            Assert.DoesNotContain("EditorEnumeratorTestWorkItem", humanText);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task UnityRefresh_ReturnsStructuredDiagnostics()
    {
        var (tempDir, pm, client, tools) = CreateTestContext();
        try
        {
            client.RefreshResultToReturn = new UnityRefreshResult
            {
                Success = false,
                Interrupted = false,
                Message = "Assets/Scripts/Game.cs(12,4): error CS0103: The name 'player' does not exist in the current context\n" +
                          "Assets/Scripts/Util.cs(88,16): warning CS0219: The variable 'temp' is assigned but its value is never used"
            };

            var result = await tools.UnityRefreshAsync();

            Assert.True(result.IsError);
            Assert.Single(result.Content);

            string text = GetResultText(result);
            Assert.Contains("Warnings:", text);
            Assert.Contains("Assets/Scripts/Util.cs#L88", text);
            Assert.Contains("warning CS0219: The variable 'temp' is assigned but its value is never used", text);
            Assert.Contains("Errors:", text);
            Assert.Contains("Assets/Scripts/Game.cs#L12", text);
            Assert.Contains("error CS0103: The name 'player' does not exist in the current context", text);
            Assert.EndsWith("Error: Unity compilation failed.", text);

            int warningsIndex = text.IndexOf("Warnings:");
            int errorsIndex = text.IndexOf("Errors:");
            Assert.True(warningsIndex < errorsIndex, "Warnings must precede errors");
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task UnityRefresh_WhenClean_ReturnsStructuredDiagnostics()
    {
        var (tempDir, pm, client, tools) = CreateTestContext();
        try
        {
            client.RefreshResultToReturn = new UnityRefreshResult
            {
                Success = false,
                Interrupted = false,
                Message = "Assets/Scripts/RecompileTest.cs(5,10): error CS1002: ; expected"
            };

            var result = await tools.UnityRefreshAsync(clean: true);

            Assert.True(result.IsError);
            Assert.Single(result.Content);

            string text = GetResultText(result);
            Assert.Contains("Assets/Scripts/RecompileTest.cs#L5", text);
            Assert.Contains("error CS1002: ; expected", text);
            Assert.EndsWith("Error: Unity recompilation failed.", text);
            Assert.DoesNotContain("Errors:", text);
            Assert.DoesNotContain("Warnings:", text);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }


    [Theory]
    [InlineData("  at MySuite.Test () [0x00000] in C:/Code/Assets/Tests/Test.cs:55", "C:/Code/Assets/Tests/Test.cs", 55)]
    [InlineData("   at MySuite.Test() in C:\\Code\\Assets\\Tests\\Test.cs:line 102", "C:\\Code\\Assets\\Tests\\Test.cs", 102)]
    [InlineData("MySuite.Test () (at Assets/Tests/Test.cs:23)", "Assets/Tests/Test.cs", 23)]
    [InlineData("  at NUnit.Framework.Assert.Fail() in <filename unknown>:0\n  at MySuite.Run() in Assets/Tests/Run.cs:99", "Assets/Tests/Run.cs", 99)]
    public void ExtractSourceLocation_ExtractsExpectedFileAndLine(string stackTrace, string expectedFile, int expectedLine)
    {
        var (file, line, uri) = UnityTools.ExtractSourceLocation(stackTrace, "C:/ProjectRoot");

        Assert.Equal(expectedFile, file);
        Assert.Equal(expectedLine, line);
        Assert.NotNull(uri);
        Assert.StartsWith("file:///", uri);
        Assert.EndsWith($"#L{expectedLine}", uri);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("  at MySuite.TestMethod() line 42")]
    [InlineData("  at NUnit.Framework.Assert.AreEqual() in <filename unknown>:0")]
    public void ExtractSourceLocation_WhenNoSourceLocation_ReturnsNulls(string? stackTrace)
    {
        var (file, line, uri) = UnityTools.ExtractSourceLocation(stackTrace, "C:/ProjectRoot");

        Assert.Null(file);
        Assert.Null(line);
        Assert.Null(uri);
    }

    [Fact]
    public void ParseCompilerDiagnostics_ParsesErrorsAndWarningsCorrectly()
    {
        string text = @"Assets/Scripts/Player.cs(10,15): error CS0103: The name 'foo' does not exist in the current context
Assets/Scripts/Enemy.cs(42,5): warning CS0219: The variable 'bar' is assigned but its value is never used";

        var diagnostics = UnityTools.ParseCompilerDiagnostics(text);

        Assert.Equal(2, diagnostics.Count);

        Assert.Equal("Assets/Scripts/Player.cs", diagnostics[0].File);
        Assert.Equal(10, diagnostics[0].Line);
        Assert.Equal(15, diagnostics[0].Column);
        Assert.Equal("error", diagnostics[0].Severity);
        Assert.Equal("CS0103", diagnostics[0].Code);
        Assert.Equal("The name 'foo' does not exist in the current context", diagnostics[0].Message);

        Assert.Equal("Assets/Scripts/Enemy.cs", diagnostics[1].File);
        Assert.Equal(42, diagnostics[1].Line);
        Assert.Equal(5, diagnostics[1].Column);
        Assert.Equal("warning", diagnostics[1].Severity);
        Assert.Equal("CS0219", diagnostics[1].Code);
        Assert.Equal("The variable 'bar' is assigned but its value is never used", diagnostics[1].Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Compilation succeeded with no diagnostics.")]
    public void ParseCompilerDiagnostics_WhenNoDiagnostics_ReturnsEmpty(string? text)
    {
        var diagnostics = DiagnosticFormatter.Default.ParseCompilerDiagnostics(text);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task DiagnosticFormatter_CustomImplementationCanBeInjectedIntoUnityTools()
    {
        var customFormatter = new TestCustomDiagnosticFormatter();
        var (tempDir, pm, client, _) = CreateTestContext();
        try
        {
            var customTools = new UnityTools(client, pm, pm.PathResolver, customFormatter);

            client.RefreshResultToReturn = new UnityRefreshResult
            {
                Success = true,
                Message = "Dummy message"
            };

            var refreshResult = await customTools.UnityRefreshAsync();
            Assert.True(customFormatter.FormatCompilerDiagnosticsCalled);

            client.TestRunResultToReturn = new UnityTestRunResult
            {
                Success = false,
                FailedTests =
                {
                    new FailedTestInfo { Name = "Test1", StackTrace = "dummy stack trace" }
                }
            };

            var testResult = await customTools.UnityRunTestsAsync();
            Assert.True(customFormatter.ExtractSourceLocationCalled);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    private class TestCustomDiagnosticFormatter : IDiagnosticFormatter
    {
        public bool ExtractSourceLocationCalled { get; private set; }
        public bool ParseCompilerDiagnosticsCalled { get; private set; }
        public bool FormatCompilerDiagnosticsCalled { get; private set; }
        public bool FormatDiagnosticCalled { get; private set; }

        public (string? filePath, int? lineNumber, string? fileUri) ExtractSourceLocation(string? stackTrace, string? projectRoot)
        {
            ExtractSourceLocationCalled = true;
            return ("CustomFile.cs", 1, "file:///CustomFile.cs#L1");
        }

        public string SanitizeTestStackTrace(string? stackTrace) => stackTrace ?? string.Empty;

        public List<StructuredCompilerDiagnostic> ParseCompilerDiagnostics(string? diagnosticText)
        {
            ParseCompilerDiagnosticsCalled = true;
            return new List<StructuredCompilerDiagnostic>
            {
                new StructuredCompilerDiagnostic
                {
                    File = "CustomFile.cs",
                    Line = 1,
                    Column = 1,
                    Severity = "warning",
                    Code = "CS9999",
                    Message = "Custom diagnostic"
                }
            };
        }

        public string FormatCompilerDiagnostics(
            string? diagnosticText,
            string? projectRoot,
            string? successTrailer = null,
            string? failureTrailer = null,
            bool isSuccess = false,
            int maxWarnings = DiagnosticFormatter.DefaultMaxWarnings,
            bool isEval = false)
        {
            FormatCompilerDiagnosticsCalled = true;
            return DiagnosticFormatter.Default.FormatCompilerDiagnostics(
                diagnosticText, projectRoot, successTrailer, failureTrailer, isSuccess, maxWarnings, isEval);
        }

        public string FormatDiagnostic(StructuredCompilerDiagnostic diagnostic, string? projectRoot, bool isEval = false)
        {
            FormatDiagnosticCalled = true;
            return DiagnosticFormatter.Default.FormatDiagnostic(diagnostic, projectRoot, isEval);
        }
    }

    [Fact]
    public async Task UnityRefresh_WhenSuccessWithWarningsUnderCap_ReportsFormattedWarningsWithoutTruncation()
    {
        var (tempDir, pm, client, tools) = CreateTestContext();
        try
        {
            client.RefreshResultToReturn = new UnityRefreshResult
            {
                Success = true,
                Message = "Assets/Scripts/A.cs(10,5): warning CS0219: Variable 'x' is unused\n" +
                          "Assets/Scripts/B.cs(20,5): warning CS0219: Variable 'y' is unused\n" +
                          "Assets/Scripts/C.cs(30,5): warning CS0219: Variable 'z' is unused"
            };

            var result = await tools.UnityRefreshAsync();

            Assert.False(result.IsError);
            string text = GetResultText(result);
            Assert.Contains("Assets/Scripts/A.cs#L10", text);
            Assert.Contains("Assets/Scripts/B.cs#L20", text);
            Assert.Contains("Assets/Scripts/C.cs#L30", text);
            Assert.DoesNotContain("omitted", text);
            Assert.DoesNotContain("Warnings:", text);
            Assert.DoesNotContain("Errors:", text);
            Assert.EndsWith("AssetDatabase refresh completed with 0 errors (3 warnings).", text);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task UnityRefresh_WhenSuccessWithSingleWarning_ReportsSingularSummary()
    {
        var (tempDir, pm, client, tools) = CreateTestContext();
        try
        {
            client.RefreshResultToReturn = new UnityRefreshResult
            {
                Success = true,
                Message = "Assets/Scripts/A.cs(10,5): warning CS0219: Variable 'x' is unused"
            };

            var result = await tools.UnityRefreshAsync();

            Assert.False(result.IsError);
            string text = GetResultText(result);
            Assert.Contains("Assets/Scripts/A.cs#L10", text);
            Assert.DoesNotContain("omitted", text);
            Assert.EndsWith("AssetDatabase refresh completed with 0 errors (1 warning).", text);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task UnityRefresh_WhenSuccessWithWarningsOverCap_ReportsCappedWarningsAndTruncationNotice()
    {
        var (tempDir, pm, client, tools) = CreateTestContext();
        try
        {
            var lines = new List<string>();
            for (int i = 1; i <= 15; i++)
            {
                lines.Add($"Assets/Scripts/File{i}.cs({i},1): warning CS0168: The variable 'v{i}' is declared but never used");
            }

            client.RefreshResultToReturn = new UnityRefreshResult
            {
                Success = true,
                Message = string.Join("\n", lines)
            };

            var result = await tools.UnityRefreshAsync();

            Assert.False(result.IsError);
            string text = GetResultText(result);
            Assert.Contains("Assets/Scripts/File1.cs#L1", text);
            Assert.Contains("Assets/Scripts/File10.cs#L10", text);
            Assert.DoesNotContain("Assets/Scripts/File11.cs#L11", text);
            Assert.Contains("... and 5 more warning(s) omitted to preserve context window.", text);
            Assert.EndsWith("AssetDatabase refresh completed with 0 errors (15 warnings).", text);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task UnityRefresh_WhenErrorsAndWarnings_DisplaysWarningsFirstAndErrorsAtEndWithHeaders()
    {
        var (tempDir, pm, client, tools) = CreateTestContext();
        try
        {
            client.RefreshResultToReturn = new UnityRefreshResult
            {
                Success = false,
                Message = "Assets/Scripts/Error1.cs(10,1): error CS0103: The name 'foo' does not exist\n" +
                          "Assets/Scripts/Warn1.cs(20,1): warning CS0219: Variable 'w1' is unused\n" +
                          "Assets/Scripts/Error2.cs(30,1): error CS0103: The name 'bar' does not exist\n" +
                          "Assets/Scripts/Warn2.cs(40,1): warning CS0219: Variable 'w2' is unused"
            };

            var result = await tools.UnityRefreshAsync();

            Assert.True(result.IsError);
            string text = GetResultText(result);
            Assert.Contains("Warnings:", text);
            Assert.Contains("Errors:", text);

            int warnHeaderIdx = text.IndexOf("Warnings:");
            int errHeaderIdx = text.IndexOf("Errors:");
            Assert.True(warnHeaderIdx < errHeaderIdx, "Warnings header must precede Errors header");

            int warn1Idx = text.IndexOf("Warn1.cs#L20");
            int err1Idx = text.IndexOf("Error1.cs#L10");
            Assert.True(warn1Idx < err1Idx, "Warnings must appear before Errors");

            Assert.EndsWith("Error: Unity compilation failed.", text);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task UnityRefresh_WhenErrorsOnly_DisplaysErrorsWithoutHeaders()
    {
        var (tempDir, pm, client, tools) = CreateTestContext();
        try
        {
            client.RefreshResultToReturn = new UnityRefreshResult
            {
                Success = false,
                Message = "Assets/Scripts/Error1.cs(10,1): error CS0103: The name 'foo' does not exist\n" +
                          "Assets/Scripts/Error2.cs(20,1): error CS0103: The name 'bar' does not exist"
            };

            var result = await tools.UnityRefreshAsync();

            Assert.True(result.IsError);
            string text = GetResultText(result);
            Assert.DoesNotContain("Warnings:", text);
            Assert.DoesNotContain("Errors:", text);
            Assert.Contains("Assets/Scripts/Error1.cs#L10", text);
            Assert.Contains("Assets/Scripts/Error2.cs#L20", text);
            Assert.EndsWith("Error: Unity compilation failed.", text);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task UnityRefresh_WhenUnparseableError_FallsBackToRawMessage()
    {
        var (tempDir, pm, client, tools) = CreateTestContext();
        try
        {
            client.RefreshResultToReturn = new UnityRefreshResult
            {
                Success = false,
                Message = "Internal compiler crash occurred during Roslyn emit."
            };

            var result = await tools.UnityRefreshAsync();

            Assert.True(result.IsError);
            string text = GetResultText(result);
            Assert.Contains("Internal compiler crash occurred during Roslyn emit.", text);
            Assert.EndsWith("Error: Unity compilation failed.", text);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task UnityRunTests_WhenCompileErrorWithWarningsAndErrors_FormatsStructuredDiagnostics()
    {
        var (tempDir, pm, client, tools) = CreateTestContext();
        try
        {
            client.TestRunResultToReturn = new UnityTestRunResult
            {
                Success = false,
                ResultState = "CompileError",
                Message = "Assets/Scripts/Util.cs(5,1): warning CS0219: Variable 't' unused\n" +
                          "Assets/Scripts/Test.cs(12,8): error CS1002: ; expected"
            };

            var result = await tools.UnityRunTestsAsync();

            Assert.True(result.IsError);
            string text = GetResultText(result);
            Assert.Contains("Warnings:", text);
            Assert.Contains("Errors:", text);
            Assert.Contains("Assets/Scripts/Util.cs#L5", text);
            Assert.Contains("Assets/Scripts/Test.cs#L12", text);
            Assert.EndsWith("Test execution aborted: Script compilation failed.", text);

            int warnIdx = text.IndexOf("Util.cs#L5");
            int errIdx = text.IndexOf("Test.cs#L12");
            Assert.True(warnIdx < errIdx, "Warnings must precede errors");
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task UnityEval_WhenPreRefreshHasCompilerDiagnostics_FormatsStructuredDiagnostics()
    {
        var (tempDir, pm, client, tools) = CreateTestContext();
        try
        {
            client.EvalResultToReturn = new UnityEvalResult
            {
                Success = false,
                Message = "Assets/Scripts/Player.cs(42,15): error CS0103: The name 'speed' does not exist in the current context"
            };

            var result = await tools.UnityEvalAsync("return 42;");

            Assert.True(result.IsError);
            string text = GetResultText(result);
            Assert.Contains("Assets/Scripts/Player.cs#L42", text);
            Assert.Contains("error CS0103: The name 'speed' does not exist in the current context", text);
            Assert.EndsWith("Evaluation aborted: Project script compilation failed.", text);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task UnityEval_WhenSnippetCompilationFails_FormatsSyntheticLocationAndSnippetTrailer()
    {
        var (tempDir, pm, client, tools) = CreateTestContext();
        try
        {
            client.EvalResultToReturn = new UnityEvalResult
            {
                Success = false,
                Message = "eval(1,5): error CS0103: The name 'xyz' does not exist in the current context"
            };

            var result = await tools.UnityEvalAsync("xyz;");

            Assert.True(result.IsError);
            string text = GetResultText(result);
            Assert.Contains("• snippet line 1, col 5: error CS0103: The name 'xyz' does not exist in the current context", text);
            Assert.DoesNotContain("file:///", text);
            Assert.DoesNotContain("eval#", text);
            Assert.EndsWith("Evaluation aborted: Dynamic snippet compilation failed.", text);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task UnityEval_WhenSnippetCompilationFailsWithMultipleErrorsAndWarnings_FormatsAllDiagnostics()
    {
        var (tempDir, pm, client, tools) = CreateTestContext();
        try
        {
            client.EvalResultToReturn = new UnityEvalResult
            {
                Success = false,
                Message = "eval(1,4): error CS1002: ; expected | eval(1,1): error CS0103: The name 'xyz' does not exist in the current context | eval(2,10): warning CS0219: Variable 'foo' unused"
            };

            var result = await tools.UnityEvalAsync("xyz");

            Assert.True(result.IsError);
            string text = GetResultText(result);
            Assert.Contains("Warnings:", text);
            Assert.Contains("• snippet line 2, col 10: warning CS0219: Variable 'foo' unused", text);
            Assert.Contains("Errors:", text);
            Assert.Contains("• snippet line 1, col 4: error CS1002: ; expected", text);
            Assert.Contains("• snippet line 1, col 1: error CS0103: The name 'xyz' does not exist in the current context", text);
            Assert.DoesNotContain("file:///", text);
            Assert.EndsWith("Evaluation aborted: Dynamic snippet compilation failed.", text);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void DiagnosticFormatter_ParseCompilerDiagnostics_WhenPipeSeparated_ParsesMultipleDiagnostics()
    {
        string text = "eval(1,4): error CS1002: ; expected | eval(1,1): error CS0103: The name 'xyz' does not exist in the current context";
        var diags = DiagnosticFormatter.Default.ParseCompilerDiagnostics(text);

        Assert.Equal(2, diags.Count);
        Assert.Equal("eval", diags[0].File);
        Assert.Equal(1, diags[0].Line);
        Assert.Equal(4, diags[0].Column);
        Assert.Equal("CS1002", diags[0].Code);
        Assert.Equal("; expected", diags[0].Message);

        Assert.Equal("eval", diags[1].File);
        Assert.Equal(1, diags[1].Line);
        Assert.Equal(1, diags[1].Column);
        Assert.Equal("CS0103", diags[1].Code);
        Assert.Equal("The name 'xyz' does not exist in the current context", diags[1].Message);
    }

    [Theory]
    [InlineData("eval", true)]
    [InlineData("EVAL", true)]
    [InlineData("<eval>", true)]
    [InlineData("<stdin>", true)]
    [InlineData("snippet", true)]
    [InlineData("SNIPPET", true)]
    [InlineData("Assets/Scripts/Player.cs", false)]
    [InlineData("eval.cs", false)]
    [InlineData("snippet.cs", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void DiagnosticFormatter_IsEvalSynthetic_IdentifiesSyntheticFileNames(string? file, bool expected)
    {
        Assert.Equal(expected, DiagnosticFormatter.IsEvalSynthetic(file));
    }

    [Theory]
    [InlineData("eval")]
    [InlineData("EVAL")]
    [InlineData("<eval>")]
    [InlineData("snippet")]
    public void DiagnosticFormatter_BuildFileUri_WhenSyntheticFile_ReturnsEmpty(string file)
    {
        string uri = DiagnosticFormatter.BuildFileUri(file, 1, "C:/Repo");
        Assert.Equal(string.Empty, uri);
    }

    [Theory]
    [InlineData("Assets/Scripts/Foo.cs", 10, "C:/Repo", "file:///C:/Repo/Assets/Scripts/Foo.cs#L10")]
    [InlineData("Assets/Scripts/Foo.cs", 10, "C:\\Repo", "file:///C:/Repo/Assets/Scripts/Foo.cs#L10")]
    [InlineData("Assets/Scripts/Foo.cs", 10, "C:\\Repo\\", "file:///C:/Repo/Assets/Scripts/Foo.cs#L10")]
    [InlineData("Assets/Scripts/Foo.cs", 10, "/home/user/repo", "file:///home/user/repo/Assets/Scripts/Foo.cs#L10")]
    [InlineData("Assets/Scripts/Foo.cs", 10, "/home/user/repo/", "file:///home/user/repo/Assets/Scripts/Foo.cs#L10")]
    [InlineData("./Assets/Scripts/Foo.cs", 10, "C:/Repo", "file:///C:/Repo/Assets/Scripts/Foo.cs#L10")]
    [InlineData(".\\Assets\\Scripts\\Foo.cs", 10, "C:\\Repo", "file:///C:/Repo/Assets/Scripts/Foo.cs#L10")]
    [InlineData("C:/Repo/Assets/Scripts/Foo.cs", 10, "C:/Repo", "file:///C:/Repo/Assets/Scripts/Foo.cs#L10")]
    [InlineData("C:\\Repo\\Assets\\Scripts\\Foo.cs", 10, "C:/Repo", "file:///C:/Repo/Assets/Scripts/Foo.cs#L10")]
    [InlineData("C:/Repo/Assets/Scripts/Foo.cs", 10, "/home/user/repo", "file:///C:/Repo/Assets/Scripts/Foo.cs#L10")]
    [InlineData("/home/user/repo/Assets/Scripts/Foo.cs", 10, "/home/user/repo", "file:///home/user/repo/Assets/Scripts/Foo.cs#L10")]
    [InlineData("/home/user/repo/Assets/Scripts/Foo.cs", 10, "C:/Repo", "file:///home/user/repo/Assets/Scripts/Foo.cs#L10")]
    public void BuildFileUri_WindowsAndPosixPaths_FormatsCorrectFileUris(string file, int line, string projectRoot, string expectedUri)
    {
        string uri = DiagnosticFormatter.BuildFileUri(file, line, projectRoot);
        Assert.Equal(expectedUri, uri);
    }

    // ==========================================
    // 7. Tool description regression tests
    // ==========================================

    [Theory]
    [InlineData("unity_refresh", "Refreshes AssetDatabase and returns compiler diagnostics. Fast (<200ms) when unchanged. Use to verify compilation after editing scripts. Note: unity_run_tests and unity_eval automatically refresh pending changes beforehand, so calling unity_refresh immediately before those tools is unnecessary.")]
    [InlineData("unity_eval", "Evaluates C# top-level script source code in-memory against the active Unity Editor to query or modify state. Write code directly as top-level statements without class or method wrappers. Top-level 'await' is supported for asynchronous code. Use 'return <value>;' to return a result; void statements and 'return;' complete without returning a value. No namespaces are pre-imported by default; include 'using UnityEngine;' to access Unity types (e.g., GameObject, Transform).")]
    [InlineData("unity_run_tests", "Runs EditMode/PlayMode tests with failure diagnostics.")]
    public void UnityTools_Methods_HaveExpectedRefinedDescriptions(string toolName, string expectedDescription)
    {
        var methods = typeof(UnityTools).GetMethods(BindingFlags.Public | BindingFlags.Instance);
        var targetMethod = methods.FirstOrDefault(m =>
            m.GetCustomAttribute<McpServerToolAttribute>()?.Name == toolName);

        Assert.NotNull(targetMethod);
        var descAttr = targetMethod.GetCustomAttribute<DescriptionAttribute>();
        Assert.NotNull(descAttr);
        Assert.Equal(expectedDescription, descAttr.Description);
    }

    [Fact]
    public void UnityTools_UnityEval_CodeParameter_HasAccurateDescription()
    {
        var evalMethod = typeof(UnityTools).GetMethod(nameof(UnityTools.UnityEvalAsync));
        Assert.NotNull(evalMethod);
        var codeParam = evalMethod.GetParameters().FirstOrDefault(p => p.Name == "code");
        Assert.NotNull(codeParam);
        var descAttr = codeParam.GetCustomAttribute<DescriptionAttribute>();
        Assert.NotNull(descAttr);
        Assert.Equal("Raw C# source text to evaluate. Send plain text directly—do not wrap in JSON.", descAttr.Description);
    }

    [Theory]
    [InlineData("return 1 + 1;", "return 1 + 1;")]
    [InlineData("using System.IO;\nreturn 42;", "using System.IO;\nreturn 42;")]
    [InlineData("{\"code\": \"return 50;\"}", "return 50;")]
    [InlineData("{\n  \"code\": \"using UnityEngine;\\nDebug.Log(1);\"\n}", "using UnityEngine;\nDebug.Log(1);")]
    public void UnityTools_UnwrapJsonCodeIfPresent_ExtractsCodeProperly(string input, string expected)
    {
        string actual = UnityTools.UnwrapJsonCodeIfPresent(input);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void UnityTools_UnityRefresh_CleanParameter_HasAccurateDescription()
    {
        var refreshMethod = typeof(UnityTools).GetMethod(nameof(UnityTools.UnityRefreshAsync));
        Assert.NotNull(refreshMethod);
        var cleanParam = refreshMethod.GetParameters().FirstOrDefault(p => p.Name == "clean");
        Assert.NotNull(cleanParam);
        var descAttr = cleanParam.GetCustomAttribute<DescriptionAttribute>();
        Assert.NotNull(descAttr);
        Assert.Contains("forces a full clean rebuild", descAttr.Description);
    }

    [Fact]
    public void UnityTools_UnityStop_ForceParameter_HasAccurateDescription()
    {
        var stopMethod = typeof(UnityTools).GetMethod(nameof(UnityTools.UnityStopAsync));
        Assert.NotNull(stopMethod);
        var forceParam = stopMethod.GetParameters().FirstOrDefault(p => p.Name == "force");
        Assert.NotNull(forceParam);
        var descAttr = forceParam.GetCustomAttribute<DescriptionAttribute>();
        Assert.NotNull(descAttr);
        Assert.Contains("forces termination even if Unity is running as an interactive GUI Editor", descAttr.Description);
    }

    [Fact]
    public void UnityTools_AllTools_HaveNonEmptyDescriptions()
    {
        var methods = typeof(UnityTools).GetMethods(BindingFlags.Public | BindingFlags.Instance);
        var toolMethods = methods.Where(m => m.GetCustomAttribute<McpServerToolAttribute>() != null).ToList();
        Assert.NotEmpty(toolMethods);
        foreach (var m in toolMethods)
        {
            var desc = m.GetCustomAttribute<DescriptionAttribute>();
            Assert.NotNull(desc);
            Assert.False(string.IsNullOrWhiteSpace(desc.Description));
        }
    }
}
