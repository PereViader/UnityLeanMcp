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
