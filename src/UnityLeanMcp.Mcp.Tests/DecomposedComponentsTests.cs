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
    public async Task UnitySocketTransport_SendCommandAsync_RethrowsCallerCancellation()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        using var cancellation = new CancellationTokenSource();
        var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseServer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            accepted.SetResult();
            await releaseServer.Task;
        });

        try
        {
            var transport = new UnitySocketTransport(NullLogger.Instance);
            Task<string?> commandTask = transport.SendCommandAsync(port, "WAIT", timeoutSeconds: 30, cancellation.Token);

            await accepted.Task;
            cancellation.Cancel();

            await Assert.ThrowsAsync<OperationCanceledException>(() => commandTask);
        }
        finally
        {
            releaseServer.TrySetResult();
            listener.Stop();
            await serverTask;
        }
    }

    [Fact]
    public async Task UnitySocketTransport_SendCommandAsync_ReturnsNullForInternalTimeout()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var releaseServer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await releaseServer.Task;
        });

        try
        {
            var transport = new UnitySocketTransport(NullLogger.Instance);

            string? response = await transport.SendCommandAsync(port, "WAIT", timeoutSeconds: 1);

            Assert.Null(response);
        }
        finally
        {
            releaseServer.TrySetResult();
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

    [Theory]
    [InlineData("ERROR: Missing argument", "Missing argument")]
    [InlineData("ERROR:Missing", "Missing")]
    [InlineData("ERROR Missing argument", "Missing argument")]
    [InlineData("ERROR", "")]
    [InlineData("FAILURE: Runner failed", "Runner failed")]
    [InlineData("FAILURE:Runner", "Runner")]
    [InlineData("FAILURE Runner failed", "Runner failed")]
    [InlineData("FAILURE", "")]
    [InlineData("SUCCESS All tests passed", "All tests passed")]
    [InlineData("SUCCESS:All tests passed", "All tests passed")]
    [InlineData("SUCCESS", "")]
    public void UnityClient_StripStatusPrefix_StripsAllPrefixVariants(string input, string expected)
    {
        string actual = UnityClient.StripStatusPrefix(input);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task OperationPoller_PollOperationUntilTerminalAsync_WhenSocketReturnsErrorWithColon_StripsPrefixAndUnescapes()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "poller_err_test_" + Guid.NewGuid().ToString("N"));
        var pathResolver = new UnityPathResolver(tempDir);
        var mockPm = new StubProcessManager(pathResolver);
        var mockTransport = new StubSocketTransport("ERROR: Something failed\\nDetails at line 10\\tCode 42");
        var poller = new OperationPoller(mockPm, pathResolver, mockTransport, NullLogger.Instance);

        var spec = new OperationPollingSpec<UnityOperationResult>
        {
            OperationId = "op_err_1",
            Kind = "test",
            ResultFilePath = Path.Combine(tempDir, "nonexistent.json"),
            IsMatch = r => r.OperationId == "op_err_1",
            PollCommand = "POLL op_err_1"
        };

        var result = await poller.PollOperationUntilTerminalAsync(spec, CancellationToken.None);

        Assert.False(result.Success);
        Assert.DoesNotContain("ERROR", result.Message);
        Assert.Equal("Something failed\nDetails at line 10\tCode 42", result.Message);
    }

    [Fact]
    public async Task OperationPoller_PollOperationUntilTerminalAsync_WhenSocketReturnsErrorWithoutColon_StripsPrefixAndUnescapes()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "poller_err_test_" + Guid.NewGuid().ToString("N"));
        var pathResolver = new UnityPathResolver(tempDir);
        var mockPm = new StubProcessManager(pathResolver);
        var mockTransport = new StubSocketTransport("ERROR Runner crashed\\nFatal exception");
        var poller = new OperationPoller(mockPm, pathResolver, mockTransport, NullLogger.Instance);

        var spec = new OperationPollingSpec<UnityOperationResult>
        {
            OperationId = "op_err_2",
            Kind = "test",
            ResultFilePath = Path.Combine(tempDir, "nonexistent.json"),
            IsMatch = r => r.OperationId == "op_err_2",
            PollCommand = "POLL op_err_2"
        };

        var result = await poller.PollOperationUntilTerminalAsync(spec, CancellationToken.None);

        Assert.False(result.Success);
        Assert.DoesNotContain("ERROR", result.Message);
        Assert.Equal("Runner crashed\nFatal exception", result.Message);
    }

    [Fact]
    public async Task OperationPoller_PollOperationUntilTerminalAsync_WhenSocketReturnsBareError_ReturnsEmptyMessage()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "poller_err_test_" + Guid.NewGuid().ToString("N"));
        var pathResolver = new UnityPathResolver(tempDir);
        var mockPm = new StubProcessManager(pathResolver);
        var mockTransport = new StubSocketTransport("ERROR");
        var poller = new OperationPoller(mockPm, pathResolver, mockTransport, NullLogger.Instance);

        var spec = new OperationPollingSpec<UnityOperationResult>
        {
            OperationId = "op_err_3",
            Kind = "test",
            ResultFilePath = Path.Combine(tempDir, "nonexistent.json"),
            IsMatch = r => r.OperationId == "op_err_3",
            PollCommand = "POLL op_err_3"
        };

        var result = await poller.PollOperationUntilTerminalAsync(spec, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(string.Empty, result.Message);
    }

    [Fact]
    public void UnityClient_EnrichRefreshResultWithDiagnostics_WithErrorDiagnostics_MarksSuccessFalseAndEnrichesMessage()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "test_enrich_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(tempDir, "Temp"));
        try
        {
            var resolver = new UnityPathResolver(tempDir);
            string errorText = "Assets/Scripts/Foo.cs(10,5): error CS0103: The name 'bar' does not exist";
            File.WriteAllText(resolver.CompilationErrorsFile, errorText);

            var pm = new StubProcessManager(resolver);
            var client = new UnityClient(pm, resolver, NullLogger<UnityClient>.Instance);

            var result = new UnityRefreshResult
            {
                OperationId = "op1",
                Success = true,
                Message = "AssetDatabase refresh completed successfully."
            };

            client.EnrichRefreshResultWithDiagnostics(result);

            Assert.False(result.Success);
            Assert.Equal(errorText, result.Message);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void UnityClient_EnrichRefreshResultWithDiagnostics_WithWarningDiagnosticsOnly_LeavesSuccessTrueAndEnrichesMessage()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "test_enrich_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(tempDir, "Temp"));
        try
        {
            var resolver = new UnityPathResolver(tempDir);
            string warningText = "Assets/Scripts/Foo.cs(10,5): warning CS0219: Variable is assigned but never used";
            File.WriteAllText(resolver.CompilationErrorsFile, warningText);

            var pm = new StubProcessManager(resolver);
            var client = new UnityClient(pm, resolver, NullLogger<UnityClient>.Instance);

            var result = new UnityRefreshResult
            {
                OperationId = "op2",
                Success = true,
                Message = "AssetDatabase refresh completed successfully."
            };

            client.EnrichRefreshResultWithDiagnostics(result);

            Assert.True(result.Success);
            Assert.Equal(warningText, result.Message);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void UnityClient_EnrichRefreshResultWithDiagnostics_WithUnstructuredErrorText_MarksSuccessFalseAndEnrichesMessage()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "test_enrich_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(tempDir, "Temp"));
        try
        {
            var resolver = new UnityPathResolver(tempDir);
            string unstructuredError = "Fatal compiler error: Unexpected compilation failure occurred.";
            File.WriteAllText(resolver.CompilationErrorsFile, unstructuredError);

            var pm = new StubProcessManager(resolver);
            var client = new UnityClient(pm, resolver, NullLogger<UnityClient>.Instance);

            var result = new UnityRefreshResult
            {
                OperationId = "op3",
                Success = true,
                Message = "AssetDatabase refresh completed successfully."
            };

            client.EnrichRefreshResultWithDiagnostics(result);

            Assert.False(result.Success);
            Assert.Equal(unstructuredError, result.Message);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void UnityClient_EnrichRefreshResultWithDiagnostics_WithWarningsAndErrors_MarksSuccessFalseAndEnrichesMessage()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "test_enrich_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(tempDir, "Temp"));
        try
        {
            var resolver = new UnityPathResolver(tempDir);
            string diagText = "Assets/Scripts/Foo.cs(5,10): warning CS0219: Variable is assigned but never used\r\nAssets/Scripts/Foo.cs(10,5): error CS0103: The name 'bar' does not exist";
            File.WriteAllText(resolver.CompilationErrorsFile, diagText);

            var pm = new StubProcessManager(resolver);
            var client = new UnityClient(pm, resolver, NullLogger<UnityClient>.Instance);

            var result = new UnityRefreshResult
            {
                OperationId = "op4",
                Success = true,
                Message = "AssetDatabase refresh completed successfully."
            };

            client.EnrichRefreshResultWithDiagnostics(result);

            Assert.False(result.Success);
            Assert.Equal(diagText, result.Message);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void UnityClient_EnrichRefreshResultWithDiagnostics_WhenNoCompilationErrorsFile_LeavesResultUnchanged()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "test_enrich_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(tempDir, "Temp"));
        try
        {
            var resolver = new UnityPathResolver(tempDir);
            var pm = new StubProcessManager(resolver);
            var client = new UnityClient(pm, resolver, NullLogger<UnityClient>.Instance);

            var result = new UnityRefreshResult
            {
                OperationId = "op5",
                Success = true,
                Message = "AssetDatabase refresh completed successfully."
            };

            client.EnrichRefreshResultWithDiagnostics(result);

            Assert.True(result.Success);
            Assert.Equal("AssetDatabase refresh completed successfully.", result.Message);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Theory]
    [InlineData("Assets/Scripts/Foo.cs(10,5): error CS0103: The name 'bar' does not exist", false)]
    [InlineData("Assets/Scripts/Foo.cs(10,5): warning CS0219: Variable is assigned but never used", true)]
    [InlineData("Unstructured error output", false)]
    [InlineData("Unstructured informative output", true)]
    public void UnityClient_EnrichRefreshResultWithDiagnostics_Static_EvaluatesDiagnosticsCorrectly(string errorText, bool expectedSuccess)
    {
        var result = new UnityRefreshResult
        {
            OperationId = "op6",
            Success = true,
            Message = "Initial"
        };

        UnityClient.EnrichRefreshResultWithDiagnostics(result, errorText);

        Assert.Equal(expectedSuccess, result.Success);
        Assert.Equal(errorText, result.Message);
    }

    [Fact]
    public void RoslynCompilerHelper_ExtractUsingDirectivesFallback_WhenUsingFollowedByCode_PreservesTrailingCodeAndColumnIndex()
    {
        string source = "using System; int x = 42;";
        bool found = UnityLeanMcp.RoslynCompilerHelper.ExtractUsingDirectivesFallback(source, out var usings, out var methodBody);

        Assert.True(found);
        Assert.Single(usings);
        Assert.Equal("using System;", usings[0]);
        Assert.Equal(source.IndexOf("int x = 42;", StringComparison.Ordinal), methodBody.IndexOf("int x = 42;", StringComparison.Ordinal));
        Assert.Equal(new string(' ', "using System;".Length) + " int x = 42;", methodBody);
    }

    [Fact]
    public void RoslynCompilerHelper_ExtractUsingDirectivesFallback_MultipleUsingsOnSingleLine_ExtractsBothAndPreservesCode()
    {
        string source = "using System; using System.Collections.Generic; return 1;";
        bool found = UnityLeanMcp.RoslynCompilerHelper.ExtractUsingDirectivesFallback(source, out var usings, out var methodBody);

        Assert.True(found);
        Assert.Equal(2, usings.Count);
        Assert.Equal("using System;", usings[0]);
        Assert.Equal("using System.Collections.Generic;", usings[1]);
        Assert.Equal(source.IndexOf("return 1;", StringComparison.Ordinal), methodBody.IndexOf("return 1;", StringComparison.Ordinal));
        Assert.Equal(new string(' ', source.IndexOf("return 1;", StringComparison.Ordinal)) + "return 1;", methodBody);
    }

    [Theory]
    [InlineData("\r\n")]
    [InlineData("\n")]
    public void RoslynCompilerHelper_ExtractUsingDirectivesFallback_MultiLineSnippet_PreservesLineCountAndEndings(string lineEnding)
    {
        string[] lines = new[]
        {
            "using System;",
            "using System.Collections.Generic;",
            "",
            "int a = 10;",
            "int b = 20;",
            "return a + b;"
        };
        string source = string.Join(lineEnding, lines);

        bool found = UnityLeanMcp.RoslynCompilerHelper.ExtractUsingDirectivesFallback(source, out var usings, out var methodBody);

        Assert.True(found);
        Assert.Equal(2, usings.Count);
        Assert.Equal("using System;", usings[0]);
        Assert.Equal("using System.Collections.Generic;", usings[1]);

        string[] resultLines = methodBody.Split(new[] { lineEnding }, StringSplitOptions.None);
        Assert.Equal(lines.Length, resultLines.Length);
        Assert.Equal(new string(' ', "using System;".Length), resultLines[0]);
        Assert.Equal(new string(' ', "using System.Collections.Generic;".Length), resultLines[1]);
        Assert.Equal("", resultLines[2]);
        Assert.Equal("int a = 10;", resultLines[3]);
        Assert.Equal("int b = 20;", resultLines[4]);
        Assert.Equal("return a + b;", resultLines[5]);

        if (lineEnding == "\r\n")
        {
            Assert.Contains("\r\n", methodBody);
        }
        else
        {
            Assert.DoesNotContain("\r", methodBody);
        }
    }

    [Fact]
    public void RoslynCompilerHelper_ExtractUsingDirectivesFallback_WhenNoUsings_ReturnsFalseAndPreservesBody()
    {
        string source = "int a = 1;\nreturn a;";
        bool found = UnityLeanMcp.RoslynCompilerHelper.ExtractUsingDirectivesFallback(source, out var usings, out var methodBody);

        Assert.False(found);
        Assert.Empty(usings);
        Assert.Equal(source, methodBody);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \t  \n  ")]
    public void RoslynCompilerHelper_ExtractUsingDirectivesFallback_WhenNullOrWhitespace_ReturnsFalse(string? source)
    {
        bool found = UnityLeanMcp.RoslynCompilerHelper.ExtractUsingDirectivesFallback(source!, out var usings, out var methodBody);

        Assert.False(found);
        Assert.Empty(usings);
        Assert.Equal(source ?? "", methodBody);
    }

    [Fact]
    public async Task RoslynCompilerHelper_EnsureInitialized_RetriesAfterFailureAndSerializesConcurrentCallers()
    {
        string originalContentsPath = UnityEditor.EditorApplication.applicationContentsPath;
        string missingContentsPath = Path.Combine(Path.GetTempPath(), "unity_roslyn_missing_" + Guid.NewGuid().ToString("N"));
        string existingContentsPath = Path.Combine(Path.GetTempPath(), "unity_roslyn_existing_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(existingContentsPath);

        try
        {
            UnityEditor.EditorApplication.applicationContentsPath = missingContentsPath;
            UnityLeanMcp.RoslynCompilerHelper.EnsureInitialized();
            Assert.Contains("invalid or does not exist", UnityLeanMcp.RoslynCompilerHelper.UnsupportedReason);

            UnityEditor.EditorApplication.applicationContentsPath = existingContentsPath;
            var callers = new Task[8];
            for (int i = 0; i < callers.Length; i++)
            {
                callers[i] = Task.Run(() => UnityLeanMcp.RoslynCompilerHelper.EnsureInitialized());
            }

            await Task.WhenAll(callers);

            Assert.False(UnityLeanMcp.RoslynCompilerHelper.IsSupported);
            Assert.Contains("could not be found or loaded", UnityLeanMcp.RoslynCompilerHelper.UnsupportedReason);
        }
        finally
        {
            UnityEditor.EditorApplication.applicationContentsPath = originalContentsPath;
            try { Directory.Delete(existingContentsPath, recursive: true); } catch { }
        }
    }

    [Fact]
    public void RoslynCompilerHelper_GetSupportStatus_EvaluatesAtomically()
    {
        var status = UnityLeanMcp.RoslynCompilerHelper.GetSupportStatus();
        Assert.Equal(UnityLeanMcp.RoslynCompilerHelper.IsSupported, status.IsSupported);
        Assert.Equal(UnityLeanMcp.RoslynCompilerHelper.UnsupportedReason, status.UnsupportedReason ?? "");
    }

    [Fact]
    public void UnityClient_WithCustomOptions_SetsPropertiesProperly()
    {
        string projectRoot = Path.Combine(Path.GetTempPath(), "unity_client_opts_" + Guid.NewGuid().ToString("N"));
        var resolver = new UnityPathResolver(projectRoot);
        var pm = new StubProcessManager(resolver);
        var options = new UnityClientOptions(PollIntervalMs: 123, BusyGracePeriod: TimeSpan.FromSeconds(7));
        IUnityClient client = new UnityClient(pm, NullLogger<UnityClient>.Instance, options: options);

        Assert.Equal(123, client.PollIntervalMs);
        Assert.Equal(TimeSpan.FromSeconds(7), client.BusyGracePeriod);
    }

    [Fact]
    public void UnityProcessManager_ConstructorInjection_InitializesDelegates()
    {
        string projectRoot = Path.Combine(Path.GetTempPath(), "unity_pm_inject_" + Guid.NewGuid().ToString("N"));
        var resolver = new UnityPathResolver(projectRoot);
        using var dummyProc = System.Diagnostics.Process.GetCurrentProcess();
        var pm = new UnityProcessManager(
            resolver,
            NullLogger<UnityProcessManager>.Instance,
            processProvider: () => new[] { dummyProc },
            processStarter: _ => dummyProc);

        Assert.NotNull(pm.ProcessProvider);
        Assert.NotNull(pm.ProcessStarter);
        Assert.Same(dummyProc, pm.ProcessProvider()[0]);
        Assert.Same(dummyProc, pm.ProcessStarter(new System.Diagnostics.ProcessStartInfo()));
    }

    private sealed class StubSocketTransport : IUnitySocketTransport
    {
        private readonly string? _response;
        public StubSocketTransport(string? response) => _response = response;
        public Task<string?> SendCommandAsync(int port, string command, int timeoutSeconds = 10, CancellationToken cancellationToken = default) => Task.FromResult(_response);
        public Task<bool> IsSocketReadyAsync(int port, int timeoutSeconds = 2, CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private sealed class StubProcessManager : IUnityProcessManager
    {
        public IUnityPathResolver PathResolver { get; }
        public IUnityExecutableLocator ExecutableLocator => throw new NotImplementedException();
        public StubProcessManager(IUnityPathResolver pathResolver) => PathResolver = pathResolver;
        public bool IsUnityRunning(out int? processId) { processId = 1234; return true; }
        public string GetUnityMode(int? pid = null) => "Batchmode";
        public int ReadPortFile() => 12345;
        public Task EnsureUnityRunningAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> StopUnityAsync(bool force = false, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public void PurgeOperationState() { }
    }
}
