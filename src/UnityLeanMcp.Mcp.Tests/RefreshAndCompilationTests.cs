using System;
using System.IO;
using System.Text;
using UnityEngine;
using Xunit;

namespace UnityLeanMcp.Mcp.Tests
{
    [Trait("Category", "Unit")]
    [Collection("CommandGate")]
    public class RefreshAndCompilationTests
    {
        [Fact]
        public void AssemblyDiagnosticsSurviveReloadAndNoOpRefreshUntilReplacedPerAssembly()
        {
            const string key = UnityLeanMcpCompilationTracker.AssemblyDiagnosticsKey;
            string previous = UnityEditor.SessionState.GetString(key, "")!;
            var callback = typeof(UnityLeanMcpCompilationTracker).GetMethod("OnAssemblyCompilationFinished",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            var diagnostics = (System.Collections.IDictionary)typeof(UnityLeanMcpCompilationTracker).GetField("s_AssemblyDiagnostics",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;
            var previousMap = new Dictionary<string, List<string>>((Dictionary<string, List<string>>)diagnostics);
            UnityCommandGate.ForceReset();
            RefreshErrorLogCapture.Clear();
            try
            {
                diagnostics.Clear();
                foreach (string assembly in new[] { "Assembly-A", "Assembly-B" })
                    callback.Invoke(null, new object[] { assembly, new[] { new UnityEditor.Compilation.CompilerMessage
                    {
                        message = "CS1030: " + assembly,
                        file = "Assets/" + assembly + ".cs", line = 1, column = 1,
                        type = UnityEditor.Compilation.CompilerMessageType.Warning
                    } } });

                diagnostics.Clear(); // Domain reload discards managed dictionaries.
                UnityLeanMcpCompilationTracker.RestoreAssemblyDiagnostics();
                UnityLeanMcpCompilationTracker.ClearCapturedDiagnostics(); // No-op refresh has no compiler callbacks.
                var restored = UnityLeanMcpCompilationTracker.WriteActiveErrorsToFile();
                Assert.Contains(restored, d => d.Contains("warning CS1030: Assembly-A"));
                Assert.Contains(restored, d => d.Contains("warning CS1030: Assembly-B"));

                callback.Invoke(null, new object[] { "Assembly-A", Array.Empty<UnityEditor.Compilation.CompilerMessage>() });
                diagnostics.Clear();
                UnityLeanMcpCompilationTracker.RestoreAssemblyDiagnostics();
                restored = UnityLeanMcpCompilationTracker.WriteActiveErrorsToFile();
                Assert.DoesNotContain(restored, d => d.Contains("Assembly-A"));
                Assert.Contains(restored, d => d.Contains("Assembly-B"));

                callback.Invoke(null, new object[] { "Assembly-B", Array.Empty<UnityEditor.Compilation.CompilerMessage>() });
                diagnostics.Clear();
                UnityLeanMcpCompilationTracker.RestoreAssemblyDiagnostics();
                Assert.Empty(UnityLeanMcpCompilationTracker.WriteActiveErrorsToFile());
            }
            finally
            {
                UnityEditor.SessionState.SetString(key, previous);
                diagnostics.Clear();
                foreach (var entry in previousMap) diagnostics.Add(entry.Key, entry.Value);
            }
        }

        [Fact]
        public void AssemblyErrorDiagnostics_PrunedOnCacheHitCompilationPassAndDomainReloadWhenCompilationSucceeded()
        {
            const string key = UnityLeanMcpCompilationTracker.AssemblyDiagnosticsKey;
            string previous = UnityEditor.SessionState.GetString(key, "")!;
            var onAssemblyFinished = typeof(UnityLeanMcpCompilationTracker).GetMethod("OnAssemblyCompilationFinished",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            var onCompilationStarted = typeof(UnityLeanMcpCompilationTracker).GetMethod("OnCompilationStarted",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            var onCompilationFinished = typeof(UnityLeanMcpCompilationTracker).GetMethod("OnCompilationFinished",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            var diagnostics = (System.Collections.IDictionary)typeof(UnityLeanMcpCompilationTracker).GetField("s_AssemblyDiagnostics",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;
            var previousMap = new Dictionary<string, List<string>>((Dictionary<string, List<string>>)diagnostics);
            UnityCommandGate.ForceReset();
            RefreshErrorLogCapture.Clear();
            try
            {
                diagnostics.Clear();
                UnityEditor.EditorUtility.scriptCompilationFailed = true;

                // Assembly-Warning compiled with a warning; Assembly-Broken1 and Assembly-Broken2 failed with errors.
                onAssemblyFinished.Invoke(null, new object[] { "Assembly-Warning", new[] { new UnityEditor.Compilation.CompilerMessage
                {
                    message = "CS0219: unused variable",
                    file = "Assets/Warning.cs", line = 10, column = 5,
                    type = UnityEditor.Compilation.CompilerMessageType.Warning
                } } });
                onAssemblyFinished.Invoke(null, new object[] { "Assembly-Broken1", new[] { new UnityEditor.Compilation.CompilerMessage
                {
                    message = "CS1002: ; expected",
                    file = "Assets/Broken1.cs", line = 4, column = 12,
                    type = UnityEditor.Compilation.CompilerMessageType.Error
                } } });
                onAssemblyFinished.Invoke(null, new object[] { "Assembly-Broken2", new[] { new UnityEditor.Compilation.CompilerMessage
                {
                    message = "CS0103: The name 'foo' does not exist",
                    file = "Assets/Broken2.cs", line = 8, column = 9,
                    type = UnityEditor.Compilation.CompilerMessageType.Error
                } } });

                // 1. Partial fix where Assembly-Broken1 is fixed and compiled with no errors,
                // while Assembly-Broken2 still fails in the compilation pass.
                onCompilationStarted.Invoke(null, new object?[] { null });
                onAssemblyFinished.Invoke(null, new object[] { "Assembly-Broken1", Array.Empty<UnityEditor.Compilation.CompilerMessage>() });
                onAssemblyFinished.Invoke(null, new object[] { "Assembly-Broken2", new[] { new UnityEditor.Compilation.CompilerMessage
                {
                    message = "CS0103: The name 'foo' does not exist",
                    file = "Assets/Broken2.cs", line = 8, column = 9,
                    type = UnityEditor.Compilation.CompilerMessageType.Error
                } } });
                onCompilationFinished.Invoke(null, new object?[] { null });

                var afterPartialFix = UnityLeanMcpCompilationTracker.WriteActiveErrorsToFile();
                Assert.DoesNotContain(afterPartialFix, d => d.Contains("Broken1.cs"));
                Assert.Contains(afterPartialFix, d => d.Contains("Assets/Broken2.cs(8,9): error CS0103"));
                Assert.Contains(afterPartialFix, d => d.Contains("Assets/Warning.cs(10,5): warning CS0219"));

                // 2. Remaining error in Assembly-Broken2 is fixed on Unity side via cache hit (0 items updated -> no OnAssemblyCompilationFinished)
                // followed by domain reload where scriptCompilationFailed is false.
                UnityEditor.EditorUtility.scriptCompilationFailed = false;
                UnityEditor.EditorApplication.isCompiling = false;
                diagnostics.Clear();
                UnityLeanMcpCompilationTracker.RestoreAssemblyDiagnostics();

                var afterFullFixReload = UnityLeanMcpCompilationTracker.WriteActiveErrorsToFile();
                Assert.DoesNotContain(afterFullFixReload, d => d.Contains("Broken2.cs"));
                Assert.Contains(afterFullFixReload, d => d.Contains("Assets/Warning.cs(10,5): warning CS0219"));
                Assert.DoesNotContain("Broken2.cs", UnityEditor.SessionState.GetString(key, "")!);
            }
            finally
            {
                UnityEditor.EditorUtility.scriptCompilationFailed = false;
                UnityEditor.EditorApplication.isCompiling = false;
                UnityLeanMcpCompilationTracker.IsCompiling = false;
                UnityLeanMcpCompilationTracker.ScriptCompilationFailed = false;
                UnityLeanMcpCompilationTracker.CompilationFinished = false;
                UnityEditor.SessionState.SetString(key, previous);
                diagnostics.Clear();
                foreach (var entry in previousMap) diagnostics.Add(entry.Key, entry.Value);
            }
        }

        [Fact]
        public void ObserveOperationUntilSettled_AfterErrorsResolvedInUnity_DoesNotReturnStaleCompilerOrScopedDiagnostics()
        {
            const string key = UnityLeanMcpCompilationTracker.AssemblyDiagnosticsKey;
            string previous = UnityEditor.SessionState.GetString(key, "")!;
            var onAssemblyFinished = typeof(UnityLeanMcpCompilationTracker).GetMethod("OnAssemblyCompilationFinished",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            var diagnostics = (System.Collections.IDictionary)typeof(UnityLeanMcpCompilationTracker).GetField("s_AssemblyDiagnostics",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;
            var previousMap = new Dictionary<string, List<string>>((Dictionary<string, List<string>>)diagnostics);
            UnityCommandGate.ForceReset();
            RefreshErrorLogCapture.Clear();
            try
            {
                diagnostics.Clear();

                // First refresh fails with a C# compile error and a scoped log error.
                UnityEditor.EditorUtility.scriptCompilationFailed = true;
                UnityLeanMcpCompilationTracker.ScriptCompilationFailed = true;
                Assert.Equal(BeginGateResult.Started, UnityCommandGate.TryBegin(OperationKinds.Refresh, "refresh-fail-1", OperationStatus.Refreshing, out _));
                RefreshErrorLogCapture.StartCapture("refresh-fail-1");
                RefreshErrorLogCapture.RecordError("Shader error in 'Custom/Broken': syntax error at Assets/Broken.shader(3)", "", LogType.Error);
                onAssemblyFinished.Invoke(null, new object[] { "Assembly-CSharp", new[] { new UnityEditor.Compilation.CompilerMessage
                {
                    message = "CS1002: ; expected",
                    file = "Assets/BrokenScript.cs", line = 12, column = 1,
                    type = UnityEditor.Compilation.CompilerMessageType.Error
                } } });

                for (int i = 0; i < 5; i++)
                {
                    UnityLeanMcpCompilationTracker.ObserveOperationUntilSettled();
                }

                string failResultFile = UnityLeanMcpPaths.GetRefreshResultFile("refresh-fail-1");
                Assert.True(File.Exists(failResultFile));
                var failResult = JsonUtility.FromJson<UnityLeanMcp.UnityRefreshResult>(File.ReadAllText(failResultFile))!;
                Assert.False(failResult.success);
                Assert.Contains("Assets/BrokenScript.cs(12,1): error CS1002", failResult.message);
                Assert.Contains("Assets/Broken.shader(3): error", failResult.message);
                File.Delete(failResultFile);

                // User fixes all issues on the Unity side; Unity finishes compiling via cache hit (no OnAssemblyCompilationFinished).
                UnityEditor.EditorUtility.scriptCompilationFailed = false;
                UnityLeanMcpCompilationTracker.ScriptCompilationFailed = false;

                // Subsequent unity_refresh runs and settles.
                Assert.Equal(BeginGateResult.Started, UnityCommandGate.TryBegin(OperationKinds.Refresh, "refresh-clean-2", OperationStatus.Refreshing, out _));
                RefreshErrorLogCapture.StartCapture("refresh-clean-2");
                for (int i = 0; i < 5; i++)
                {
                    UnityLeanMcpCompilationTracker.ObserveOperationUntilSettled();
                }

                string cleanResultFile = UnityLeanMcpPaths.GetRefreshResultFile("refresh-clean-2");
                Assert.True(File.Exists(cleanResultFile));
                var cleanResult = JsonUtility.FromJson<UnityLeanMcp.UnityRefreshResult>(File.ReadAllText(cleanResultFile))!;
                Assert.True(cleanResult.success);
                Assert.Equal("", cleanResult.message);
                Assert.False(UnityLeanMcpCompilationTracker.RefreshRequired);
                Assert.False(File.Exists(UnityLeanMcpPaths.DiagnosticsFile));
                File.Delete(cleanResultFile);
            }
            finally
            {
                UnityEditor.EditorUtility.scriptCompilationFailed = false;
                UnityLeanMcpCompilationTracker.ScriptCompilationFailed = false;
                UnityLeanMcpCompilationTracker.RefreshRequired = true;
                RefreshErrorLogCapture.Clear();
                UnityCommandGate.ForceReset();
                UnityEditor.SessionState.SetString(key, previous);
                diagnostics.Clear();
                foreach (var entry in previousMap) diagnostics.Add(entry.Key, entry.Value);
            }
        }

        [Fact]
        public void RefreshErrorLogCapture_StartAndStopCapture_CapturesOnlyErrorsAndExceptions()
        {
            UnityCommandGate.ForceReset();
            Assert.Equal(BeginGateResult.Started, UnityCommandGate.TryBegin(OperationKinds.Refresh, "op-capture-test", OperationStatus.Refreshing, out _));
            RefreshErrorLogCapture.Clear("op-flood");

            using (var capture = RefreshErrorLogCapture.StartCapture("op-capture-test"))
            {
                // Internal logs should be ignored
                RefreshErrorLogCapture.RecordError("UnityLeanMcp: internal harness log", "", LogType.Error);

                // Non-error logs should be ignored
                RefreshErrorLogCapture.RecordError("Normal log message", "", LogType.Log);
                RefreshErrorLogCapture.RecordError("Warning log message", "", LogType.Warning);

                // Error, Exception, Assert should be captured
                RefreshErrorLogCapture.RecordError("Shader error in 'Custom/Shader': syntax error at Assets/Shaders/Test.shader(10)", "", LogType.Error);
                RefreshErrorLogCapture.RecordError("Exception: NullReferenceException", "in Assets/Scripts/Test.cs:line 42", LogType.Exception);
                RefreshErrorLogCapture.RecordError("Assertion failed: value > 0", "in Assets/Scripts/Test.cs:line 88", LogType.Assert);

                var captured = RefreshErrorLogCapture.GetCapturedErrors("op-capture-test");
                Assert.Equal(3, captured.Count);
                Assert.Contains(captured, d => d.Contains("Assets/Shaders/Test.shader(10)"));
                Assert.Contains(captured, d => d.Contains("Assets/Scripts/Test.cs(42)"));
                Assert.Contains(captured, d => d.Contains("Assets/Scripts/Test.cs(88)"));
            }

            UnityCommandGate.ForceReset();
        }

        [Fact]
        public void RefreshErrorLogCapture_FormatDiagnostic_ExtractsFileAndStackTrace()
        {
            string formattedShader = RefreshErrorLogCapture.FormatDiagnostic(
                "Shader error in 'Hidden/Fx': failed at Assets/Shaders/Fx.shader(25,10)", "");
            Assert.Equal("Assets/Shaders/Fx.shader(25,10): error Shader error in 'Hidden/Fx': failed at Assets/Shaders/Fx.shader(25,10)", formattedShader);

            string formattedQuoted = RefreshErrorLogCapture.FormatDiagnostic(
                "Failed to import 'Assets/Textures/Sprite.png'. Corrupt header.", "");
            Assert.Equal("Assets/Textures/Sprite.png: error Failed to import 'Assets/Textures/Sprite.png'. Corrupt header.", formattedQuoted);

            string formattedStackTrace = RefreshErrorLogCapture.FormatDiagnostic(
                "NullReferenceException: Object reference not set",
                "  at Example.DoWork () [0x00000] in Assets/Scripts/Worker.cs:55\n");
            Assert.Equal("Assets/Scripts/Worker.cs(55): error NullReferenceException: Object reference not set", formattedStackTrace);
        }

        [Fact]
        public void PollRefreshHandler_WhenForeignOperationInGate_DoesNotLeakForeignOpId()
        {
            UnityCommandGate.ForceReset();
            Assert.Equal(BeginGateResult.Started, UnityCommandGate.TryBegin(OperationKinds.Test, "super-secret-test-id", OperationStatus.Running, out _));

            var handler = new PollRefreshHandler();

            // Bare poll
            using (var stream = new MemoryStream())
            using (var writer = new StreamWriter(stream) { AutoFlush = true })
            {
                handler.Handle("", writer);
                string response = Encoding.UTF8.GetString(stream.ToArray()).Trim();
                Assert.Equal("BUSY test", response);
                Assert.DoesNotContain("super-secret-test-id", response);
            }

            // Correlated poll for a different operationId
            using (var stream = new MemoryStream())
            using (var writer = new StreamWriter(stream) { AutoFlush = true })
            {
                handler.Handle("my-polled-refresh-op", writer);
                string response = Encoding.UTF8.GetString(stream.ToArray()).Trim();
                Assert.Equal("BUSY test", response);
                Assert.DoesNotContain("super-secret-test-id", response);
            }

            UnityCommandGate.ForceReset();
        }

        [Fact]
        public void PollRefreshHandler_WhenCorrelatedOpNotRunningAndNoResult_ReturnsIdle()
        {
            UnityCommandGate.ForceReset();
            var handler = new PollRefreshHandler();

            // Set global compiler flags to simulating Unity doing background work
            UnityLeanMcpCompilationTracker.IsCompiling = true;
            UnityLeanMcpCompilationTracker.ScriptCompilationFailed = true;

            try
            {
                using (var stream = new MemoryStream())
                using (var writer = new StreamWriter(stream) { AutoFlush = true })
                {
                    handler.Handle("op-never-started-12345", writer);
                    string response = Encoding.UTF8.GetString(stream.ToArray()).Trim();
                    Assert.Equal("IDLE", response);
                }
            }
            finally
            {
                UnityLeanMcpCompilationTracker.IsCompiling = false;
                UnityLeanMcpCompilationTracker.ScriptCompilationFailed = false;
            }
        }

        [Fact]
        public void PollRefreshHandler_WhenBuildFailedAndDiagnosticsFileMissing_ReturnsCompilationError()
        {
            UnityCommandGate.ForceReset();
            var handler = new PollRefreshHandler();

            string diagFile = UnityLeanMcpPaths.DiagnosticsFile;
            if (File.Exists(diagFile))
            {
                File.Delete(diagFile);
            }

            UnityLeanMcpCompilationTracker.ScriptCompilationFailed = true;
            try
            {
                using (var stream = new MemoryStream())
                using (var writer = new StreamWriter(stream) { AutoFlush = true })
                {
                    handler.Handle("", writer);
                    string response = Encoding.UTF8.GetString(stream.ToArray()).Trim();
                    // Must NOT return COMPILING indefinitely:
                    Assert.Equal("COMPILATION_ERROR", response);
                }
            }
            finally
            {
                UnityLeanMcpCompilationTracker.ScriptCompilationFailed = false;
            }
        }

        [Fact]
        public void CompilationTracker_StateLatching_TransitionsCleanly()
        {
            UnityLeanMcpCompilationTracker.CompilationRequested = false;
            Assert.False(UnityLeanMcpCompilationTracker.AwaitingCompilationStart);
            Assert.False(UnityLeanMcpCompilationTracker.CompilationInProgress);
            Assert.False(UnityLeanMcpCompilationTracker.CompilationFinished);

            // 1. Compilation requested
            UnityLeanMcpCompilationTracker.AwaitingCompilationStart = true;
            Assert.True(UnityLeanMcpCompilationTracker.AwaitingCompilationStart);
            Assert.False(UnityLeanMcpCompilationTracker.CompilationInProgress);
            Assert.False(UnityLeanMcpCompilationTracker.CompilationFinished);

            // 2. Compilation starts
            UnityLeanMcpCompilationTracker.CompilationInProgress = true;
            Assert.False(UnityLeanMcpCompilationTracker.AwaitingCompilationStart);
            Assert.True(UnityLeanMcpCompilationTracker.CompilationInProgress);
            Assert.False(UnityLeanMcpCompilationTracker.CompilationFinished);

            // 3. Compilation finishes
            UnityLeanMcpCompilationTracker.CompilationFinished = true;
            Assert.False(UnityLeanMcpCompilationTracker.AwaitingCompilationStart);
            Assert.False(UnityLeanMcpCompilationTracker.CompilationInProgress);
            Assert.True(UnityLeanMcpCompilationTracker.CompilationFinished);

            // Reset
            UnityLeanMcpCompilationTracker.CompilationFinished = false;
            Assert.False(UnityLeanMcpCompilationTracker.CompilationFinished);
        }

        [Fact]
        public void ConsoleLogCapture_CapturesAndPreservesStackTraceForErrorsAndExceptions()
        {
            using (var capture = new ConsoleLogCapture())
            {
                var method = typeof(ConsoleLogCapture).GetMethod("OnLogReceived",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                Assert.NotNull(method);

                // Error with stack trace
                method.Invoke(capture, new object[] { "Error occurred", "  at Foo.Bar () in Assets/Foo.cs:12", LogType.Error });

                // Exception with stack trace
                method.Invoke(capture, new object[] { "Unhandled exception", "  at Baz.Qux () in Assets/Baz.cs:34", LogType.Exception });

                // Log without stack trace
                method.Invoke(capture, new object[] { "Standard log", "  at Normal.Run () in Assets/Normal.cs:56", LogType.Log });

                var logs = capture.GetLogs();
                Assert.Equal(3, logs.Count);

                Assert.Equal("Error occurred", logs[0].message);
                Assert.Equal("Error", logs[0].logType);
                Assert.Equal("  at Foo.Bar () in Assets/Foo.cs:12", logs[0].stackTrace);

                Assert.Equal("Unhandled exception", logs[1].message);
                Assert.Equal("Exception", logs[1].logType);
                Assert.Equal("  at Baz.Qux () in Assets/Baz.cs:34", logs[1].stackTrace);

                Assert.Equal("Standard log", logs[2].message);
                Assert.Equal("Log", logs[2].logType);
                Assert.Null(logs[2].stackTrace);
            }
        }
    }
}
