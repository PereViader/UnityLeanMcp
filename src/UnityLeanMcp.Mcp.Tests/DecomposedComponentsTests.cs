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

[Trait("Category", "Unit")]
public class DecomposedComponentsTests
{
    [Fact]
    public void UnityLogScanner_HasCompilationErrors_IdentifiesErrorsAccurately()
    {
        var scanner = new UnityLogScanner();

        string logWithError = "Random info line\r\nAssets/Scripts/Test.cs(10,5): error CS0103: The name 'x' does not exist in the current context\r\nAnother line";
        string logWithWarningOnly = "Random info line\r\nAssets/Scripts/Test.cs(10,5): warning CS0168: The variable 'x' is declared but never used\r\nAnother line";
        string cleanLog = "Compilation succeeded.\r\nAsset database refreshed.";

        Assert.True(scanner.HasCompilationErrors(logWithError));
        Assert.False(scanner.HasCompilationErrors(logWithWarningOnly));
        Assert.False(scanner.HasCompilationErrors(cleanLog));
        Assert.False(scanner.HasCompilationErrors(""));
    }

    [Fact]
    public void UnityLogScanner_ExtractUniqueCompilationLines_DeduplicatesDiagnostics()
    {
        var scanner = new UnityLogScanner();

        string log = string.Join(Environment.NewLine, new[]
        {
            "Building project...",
            @"C:\Unity\Assets\Script.cs(12,34): error CS0103: The name 'bar' does not exist",
            @"C:\Unity\Assets\Script.cs(12,34): error CS0103: The name 'bar' does not exist",
            @"C:\Unity\Assets\Script.cs(5,10): warning CS0219: Variable is assigned but its value is never used",
            "Done."
        });

        var lines = scanner.ExtractUniqueCompilationLines(log);

        Assert.Equal(2, lines.Count);
        Assert.Equal(@"C:\Unity\Assets\Script.cs(12,34): error CS0103: The name 'bar' does not exist", lines[0]);
        Assert.Equal(@"C:\Unity\Assets\Script.cs(5,10): warning CS0219: Variable is assigned but its value is never used", lines[1]);
    }

    [Fact]
    public void UnityLogScanner_GetLogSnippet_ReturnsLastLinesOrMissingNotice()
    {
        var scanner = new UnityLogScanner();
        string tempFile = Path.GetTempFileName();

        try
        {
            var lines = new StringBuilder();
            for (int i = 1; i <= 40; i++)
            {
                lines.AppendLine($"Line {i}");
            }
            File.WriteAllText(tempFile, lines.ToString());

            string snippet = scanner.GetLogSnippet(tempFile);
            Assert.Contains("Last log lines:", snippet);
            Assert.Contains("Line 40", snippet);
            Assert.DoesNotContain("Line 5\n", snippet); // Only last 25 lines

            string missingSnippet = scanner.GetLogSnippet(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
            Assert.Equal("No Unity log file found.", missingSnippet);
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
        }
    }

    [Fact]
    public async Task UnitySocketTransport_SendCommandAsync_SendsAndReceivesLine()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(cts.Token);
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };

            string? cmd = await reader.ReadLineAsync(cts.Token);
            if (cmd == "PING")
            {
                await writer.WriteLineAsync("PONG");
            }
            else
            {
                await writer.WriteLineAsync($"ECHO: {cmd}");
            }
        }, cts.Token);

        try
        {
            var transport = new UnitySocketTransport(NullLogger.Instance);

            bool isReady = await transport.IsSocketReadyAsync(port, timeoutSeconds: 2, cts.Token);
            Assert.True(isReady);
        }
        finally
        {
            listener.Stop();
            await serverTask;
        }
    }

    [Fact]
    public async Task OperationPoller_PollOperationUntilTerminalAsync_ReadsResultFile()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "unity_poller_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string resultFile = Path.Combine(tempDir, "result.json");
        string opFile = Path.Combine(tempDir, "op.json");

        var pathResolver = new UnityPathResolver(tempDir);
        var pm = new UnityProcessManager(pathResolver, NullLogger<UnityProcessManager>.Instance);
        var transport = new UnitySocketTransport(NullLogger.Instance);
        var poller = new OperationPoller(pm, pathResolver, transport, NullLogger.Instance);

        string opId = "test_op_123";

        // Write terminal result file
        File.WriteAllText(resultFile, $"{{\"operationId\":\"{opId}\",\"success\":true,\"message\":\"Completed successfully\"}}");

        var spec = new OperationPollingSpec<UnityOperationResult>
        {
            OperationId = opId,
            Kind = "test",
            ResultFilePath = resultFile,
            IsMatch = r => r.OperationId == opId,
            PollCommand = $"POLL {opId}",
            PollIntervalMs = 50
        };

        var result = await poller.PollOperationUntilTerminalAsync(spec, CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result.Success);
        Assert.Equal(opId, result.OperationId);
        Assert.Equal("Completed successfully", result.Message);

        try { Directory.Delete(tempDir, true); } catch { }
    }

    [Fact]
    public void DiagnosticFormatter_SanitizeTestStackTrace_SyncTestWithReflectionPlumbing_PreservesUserCodeAndStripsPlumbing()
    {
        var formatter = new DiagnosticFormatter();
        string rawTrace = string.Join(Environment.NewLine, new[]
        {
            "  at MyProject.Engine.Compute () [0x00010] in C:\\Engine.cs:10",
            "  at MyProject.Tests.PlayerTests.MoveTest () [0x00025] in C:\\PlayerTests.cs:42",
            "  at (wrapper managed-to-native) System.Reflection.RuntimeMethodInfo.InternalInvoke(System.Reflection.RuntimeMethodInfo,object,object[],System.Exception&)",
            "  at System.Reflection.RuntimeMethodInfo.Invoke (System.Object obj, System.Reflection.BindingFlags invokeAttr, System.Reflection.Binder binder, System.Object[] parameters, System.Globalization.CultureInfo culture) [0x0006a] in <...>:0",
            "  at System.Reflection.MethodBase.Invoke (System.Object obj, System.Object[] parameters) [0x00000] in <...>:0",
            "  at NUnit.Framework.Internal.Commands.TestMethodCommand.Execute (NUnit.Framework.Internal.TestExecutionContext context) [0x0001c] in <...>:0",
            "  at UnityEditor.TestTools.TestRunner.EditorEnumeratorTestWorkItem.Execute () [0x0003b] in <...>:0",
            "  at UnityEditor.TestTools.TestRunner.TestWorkItem.PerformWork () [0x00000] in <...>:0"
        });

        string sanitized = formatter.SanitizeTestStackTrace(rawTrace);

        Assert.Contains("MyProject.Engine.Compute", sanitized);
        Assert.Contains("MyProject.Tests.PlayerTests.MoveTest", sanitized);
        Assert.DoesNotContain("System.Reflection", sanitized);
        Assert.DoesNotContain("TestMethodCommand", sanitized);
        Assert.DoesNotContain("EditorEnumeratorTestWorkItem", sanitized);
    }

    [Fact]
    public void DiagnosticFormatter_SanitizeTestStackTrace_UserCodeUsesReflection_DoesNotCreateFalsePositive()
    {
        var formatter = new DiagnosticFormatter();
        string rawTrace = string.Join(Environment.NewLine, new[]
        {
            "  at System.Reflection.MethodBase.Invoke (System.Object obj, System.Object[] parameters) [0x00000] in <...>:0",
            "  at MyProject.ReflectionHelper.Call (System.String name) [0x00005] in C:\\ReflectionHelper.cs:15",
            "  at MyProject.Tests.PlayerTests.MoveTest () [0x00025] in C:\\PlayerTests.cs:42",
            "  at (wrapper managed-to-native) System.Reflection.RuntimeMethodInfo.InternalInvoke(System.Reflection.RuntimeMethodInfo,object,object[],System.Exception&)",
            "  at System.Reflection.RuntimeMethodInfo.Invoke (System.Object obj, System.Reflection.BindingFlags invokeAttr, System.Reflection.Binder binder, System.Object[] parameters, System.Globalization.CultureInfo culture) [0x0006a] in <...>:0",
            "  at NUnit.Framework.Internal.Commands.TestMethodCommand.Execute (NUnit.Framework.Internal.TestExecutionContext context) [0x0001c] in <...>:0"
        });

        string sanitized = formatter.SanitizeTestStackTrace(rawTrace);

        // User reflection and user code must be preserved in full
        Assert.Contains("System.Reflection.MethodBase.Invoke", sanitized);
        Assert.Contains("MyProject.ReflectionHelper.Call", sanitized);
        Assert.Contains("MyProject.Tests.PlayerTests.MoveTest", sanitized);

        // Framework runner reflection and commands must be stripped
        Assert.DoesNotContain("InternalInvoke", sanitized);
        Assert.DoesNotContain("TestMethodCommand", sanitized);
    }

    [Fact]
    public void DiagnosticFormatter_SanitizeTestStackTrace_CoroutineTest_StripsRunnerPlumbing()
    {
        var formatter = new DiagnosticFormatter();
        string rawTrace = string.Join(Environment.NewLine, new[]
        {
            "  at UnityEngine.Assertions.Assert.AreEqual[T] (T expected, T actual) [0x00000] in <...>:0",
            "  at MyProject.Player.Move (UnityEngine.Vector3 dir) [0x00010] in C:\\Player.cs:25",
            "  at MyProject.Tests.PlayerTests+<MoveOverTime>d__1.MoveNext () [0x00030] in C:\\PlayerTests.cs:51",
            "  at UnityEngine.TestTools.EnumerableTestMethodCommand.AdvanceEnumerator (System.Collections.IEnumerator enumerator) [0x00010] in <...>:0",
            "  at UnityEditor.TestTools.TestRunner.EditorEnumeratorTestWorkItem.Execute () [0x0003b] in <...>:0"
        });

        string sanitized = formatter.SanitizeTestStackTrace(rawTrace);

        Assert.Contains("UnityEngine.Assertions.Assert.AreEqual", sanitized);
        Assert.Contains("MyProject.Player.Move", sanitized);
        Assert.Contains("MyProject.Tests.PlayerTests+<MoveOverTime>d__1.MoveNext", sanitized);
        Assert.DoesNotContain("EnumerableTestMethodCommand", sanitized);
        Assert.DoesNotContain("EditorEnumeratorTestWorkItem", sanitized);
    }

    [Fact]
    public void DiagnosticFormatter_SanitizeTestStackTrace_AsyncTaskTest_StripsRunnerPlumbing()
    {
        var formatter = new DiagnosticFormatter();
        string rawTrace = string.Join(Environment.NewLine, new[]
        {
            "  at MyProject.Service.CallAsync () [0x00010] in C:\\Service.cs:30",
            "  at MyProject.Tests.ServiceTests+<MyAsyncTest>d__0.MoveNext () [0x00020] in C:\\ServiceTests.cs:40",
            "  at UnityEngine.TestTools.TaskTestMethodCommand.ExecuteEnumerable (NUnit.Framework.Internal.ITestExecutionContext context) [0x00015] in <...>:0",
            "  at UnityEditor.TestTools.TestRunner.EditorEnumeratorTestWorkItem.Execute () [0x0003b] in <...>:0"
        });

        string sanitized = formatter.SanitizeTestStackTrace(rawTrace);

        Assert.Contains("MyProject.Service.CallAsync", sanitized);
        Assert.Contains("MyProject.Tests.ServiceTests+<MyAsyncTest>d__0.MoveNext", sanitized);
        Assert.DoesNotContain("TaskTestMethodCommand", sanitized);
        Assert.DoesNotContain("EditorEnumeratorTestWorkItem", sanitized);
    }

    [Fact]
    public void DiagnosticFormatter_SanitizeTestStackTrace_DirectAssertFailureInTestMethod_KeepsOnlyTestMethod()
    {
        var formatter = new DiagnosticFormatter();
        string rawTrace = string.Join(Environment.NewLine, new[]
        {
            "  at MyProject.Tests.PlayerTests.DirectFailTest () [0x00001] in C:\\PlayerTests.cs:20",
            "  at (wrapper managed-to-native) System.Reflection.RuntimeMethodInfo.InternalInvoke(System.Reflection.RuntimeMethodInfo,object,object[],System.Exception&)",
            "  at NUnit.Framework.Internal.Commands.TestMethodCommand.Execute (NUnit.Framework.Internal.TestExecutionContext context) [0x0001c] in <...>:0"
        });

        string sanitized = formatter.SanitizeTestStackTrace(rawTrace);

        Assert.Equal("  at MyProject.Tests.PlayerTests.DirectFailTest () [0x00001] in C:\\PlayerTests.cs:20", sanitized);
    }

    [Fact]
    public void DiagnosticFormatter_SanitizeTestStackTrace_NoFrameworkMarker_ReturnsOriginalStackTrace()
    {
        var formatter = new DiagnosticFormatter();
        string customTrace = "  at SomeCustomTool.Run ()\r\n  at SomeOtherTool.Start ()";

        string sanitized = formatter.SanitizeTestStackTrace(customTrace);

        Assert.Equal(customTrace, sanitized);
    }

    [Fact]
    public void DiagnosticFormatter_SanitizeTestStackTrace_NullOrEmpty_ReturnsEmpty()
    {
        var formatter = new DiagnosticFormatter();

        Assert.Equal(string.Empty, formatter.SanitizeTestStackTrace(null));
        Assert.Equal(string.Empty, formatter.SanitizeTestStackTrace(""));
        Assert.Equal(string.Empty, formatter.SanitizeTestStackTrace("   \r\n  "));
    }

    [Fact]
    public void UnityTestRunResult_InterruptedSetter_SettingTrue_SetsResultStateToInterrupted()
    {
        var result = new UnityTestRunResult
        {
            Success = true,
            ResultState = "Passed"
        };

        result.Interrupted = true;

        Assert.True(result.Interrupted);
        Assert.Equal("Interrupted", result.ResultState);
    }

    [Fact]
    public void UnityTestRunResult_InterruptedSetter_SettingFalse_WhenInterruptedAndSuccess_RestoresPassed()
    {
        var result = new UnityTestRunResult
        {
            Success = true,
            FailCount = 0,
            ResultState = "Interrupted"
        };

        Assert.True(result.Interrupted);

        result.Interrupted = false;

        Assert.False(result.Interrupted);
        Assert.Equal("Passed", result.ResultState);
    }

    [Fact]
    public void UnityTestRunResult_InterruptedSetter_SettingFalse_WhenInterruptedAndFailCount_RestoresFailed()
    {
        var result = new UnityTestRunResult
        {
            Success = false,
            FailCount = 2,
            ResultState = "Interrupted"
        };

        Assert.True(result.Interrupted);

        result.Interrupted = false;

        Assert.False(result.Interrupted);
        Assert.Equal("Failed", result.ResultState);
    }

    [Fact]
    public void UnityTestRunResult_InterruptedSetter_SettingFalse_WhenInterruptedAndNoFailures_ClearsResultState()
    {
        var result = new UnityTestRunResult
        {
            Success = false,
            FailCount = 0,
            ResultState = "Interrupted"
        };

        Assert.True(result.Interrupted);

        result.Interrupted = false;

        Assert.False(result.Interrupted);
        Assert.Equal(string.Empty, result.ResultState);
    }

    [Fact]
    public void UnityTestRunResult_InterruptedSetter_SettingFalse_WhenNotInterrupted_PreservesOriginalResultState()
    {
        var result = new UnityTestRunResult
        {
            Success = true,
            ResultState = "Passed"
        };

        result.Interrupted = false;

        Assert.False(result.Interrupted);
        Assert.Equal("Passed", result.ResultState);
    }

    [Fact]
    public void UnityTestRunResult_InterruptedSetter_ViaIOperationResultInterface_MaintainsContractSymmetry()
    {
        IOperationResult result = new UnityTestRunResult
        {
            Success = true,
            ResultState = "Passed"
        };

        result.Interrupted = true;
        Assert.True(result.Interrupted);

        result.Interrupted = false;
        Assert.False(result.Interrupted);
        Assert.Equal("Passed", ((UnityTestRunResult)result).ResultState);
    }
}
