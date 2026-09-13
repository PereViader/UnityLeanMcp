using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace UnityLeanMcp.Mcp.Tests;

[Trait("Category", "Unit")]
public class UnityProcessManagerTests
{
    private static Process StartDummyProcess()
    {
        var psi = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new ProcessStartInfo("ping.exe", "127.0.0.1 -n 15") { CreateNoWindow = true, UseShellExecute = false }
            : new ProcessStartInfo("sleep", "15") { CreateNoWindow = true, UseShellExecute = false };
        return Process.Start(psi)!;
    }

    [Fact]
    public void ReadFileWithRetry_WithFromOffset_ReadsOnlyAppendedContent()
    {
        string tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempFile, "Historical line 1\nHistorical line 2\n", Encoding.UTF8);
            long offset = new FileInfo(tempFile).Length;

            File.AppendAllText(tempFile, "New line 3\nNew line 4\n", Encoding.UTF8);

            string result = UnityProcessManager.ReadFileWithRetry(tempFile, fromOffset: offset);

            Assert.Equal("New line 3\nNew line 4\n", result);
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
        }
    }

    [Fact]
    public void ReadFileWithRetry_WhenFileTruncated_ReadsFromBeginning()
    {
        string tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempFile, "A very long historical text that will be truncated later.\n", Encoding.UTF8);
            long offset = new FileInfo(tempFile).Length;

            // Truncate and write shorter new text
            File.WriteAllText(tempFile, "Short fresh text.\n", Encoding.UTF8);
            Assert.True(new FileInfo(tempFile).Length < offset);

            string result = UnityProcessManager.ReadFileWithRetry(tempFile, fromOffset: offset);

            Assert.Equal("Short fresh text.\n", result);
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
        }
    }

    [Fact]
    public void ReadFileWithRetry_WhenOffsetEqualsLength_ReturnsEmpty()
    {
        string tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempFile, "Some existing content\n", Encoding.UTF8);
            long offset = new FileInfo(tempFile).Length;

            string result = UnityProcessManager.ReadFileWithRetry(tempFile, fromOffset: offset);

            Assert.Equal(string.Empty, result);
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
        }
    }

    [Fact]
    public void ReadFileWithRetry_WhenTransientUnauthorizedAccessException_RetriesAndSucceeds()
    {
        int attempts = 0;
        string result = UnityProcessManager.ReadFileWithRetry(
            "dummy_path.txt",
            maxRetries: 5,
            delayMs: 1,
            fromOffset: 0,
            reader: (path, offset) =>
            {
                attempts++;
                if (attempts < 3)
                {
                    throw new UnauthorizedAccessException("Simulated access denied");
                }
                return "Recovered content";
            });

        Assert.Equal("Recovered content", result);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public void ReadFileWithRetry_WhenTransientIOException_RetriesAndSucceeds()
    {
        int attempts = 0;
        string result = UnityProcessManager.ReadFileWithRetry(
            "dummy_path.txt",
            maxRetries: 5,
            delayMs: 1,
            fromOffset: 0,
            reader: (path, offset) =>
            {
                attempts++;
                if (attempts < 3)
                {
                    throw new IOException("Simulated sharing violation");
                }
                return "Recovered from IO exception";
            });

        Assert.Equal("Recovered from IO exception", result);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public void ReadFileWithRetry_WhenMixedTransientExceptions_RetriesAndSucceeds()
    {
        int attempts = 0;
        string result = UnityProcessManager.ReadFileWithRetry(
            "dummy_path.txt",
            maxRetries: 5,
            delayMs: 1,
            fromOffset: 0,
            reader: (path, offset) =>
            {
                attempts++;
                if (attempts == 1)
                {
                    throw new UnauthorizedAccessException("Attempt 1: Access denied");
                }
                if (attempts == 2)
                {
                    throw new IOException("Attempt 2: Sharing violation");
                }
                return "Recovered after mixed exceptions";
            });

        Assert.Equal("Recovered after mixed exceptions", result);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public void ReadFileWithRetry_WhenUnauthorizedAccessExceptionExceedsMaxRetries_ThrowsUnauthorizedAccessException()
    {
        int attempts = 0;
        var ex = Assert.Throws<UnauthorizedAccessException>(() =>
            UnityProcessManager.ReadFileWithRetry(
                "dummy_path.txt",
                maxRetries: 3,
                delayMs: 1,
                fromOffset: 0,
                reader: (path, offset) =>
                {
                    attempts++;
                    throw new UnauthorizedAccessException("Persistent access denied");
                }));

        Assert.Contains("Persistent access denied", ex.Message);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task ReadFileWithRetry_WhenFileInitiallyLocked_RetriesAndReadsContent()
    {
        string tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempFile, "Retry file content", Encoding.UTF8);

            using var stream = new FileStream(tempFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

            // Release the lock asynchronously after a short delay
            _ = Task.Run(async () =>
            {
                await Task.Delay(50);
                stream.Dispose();
            });

            string result = UnityProcessManager.ReadFileWithRetry(tempFile, maxRetries: 10, delayMs: 25);

            Assert.Equal("Retry file content", result);
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
        }
    }

    [Fact]
    public async Task EnsureUnityRunningAsync_WhenUnityAlreadyRunning_IgnoresHistoricalCompilationErrorsInLogFile()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "unity_pm_test_" + Guid.NewGuid().ToString("N"));
        string unityTemp = Path.Combine(tempDir, "Temp");
        Directory.CreateDirectory(unityTemp);

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var serverTask = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                TcpClient tcp;
                try
                {
                    tcp = await listener.AcceptTcpClientAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                _ = Task.Run(async () =>
                {
                    using (tcp)
                    using (var stream = tcp.GetStream())
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                    using (var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true })
                    {
                        string? line = await reader.ReadLineAsync(cts.Token);
                        if (line == null) return;

                        if (line == "PING")
                        {
                            await writer.WriteLineAsync("PONG".AsMemory(), cts.Token);
                        }
                        else if (line.StartsWith("POLL_REFRESH"))
                        {
                            await writer.WriteLineAsync("READY".AsMemory(), cts.Token);
                        }
                    }
                }, cts.Token);
            }
        });

        try
        {
            // Simulate already running Unity instance
            await File.WriteAllTextAsync(Path.Combine(unityTemp, "unity_lean_mcp_port.txt"), port.ToString());
            await File.WriteAllTextAsync(Path.Combine(unityTemp, "unity_lean_mcp_process.pid"), Environment.ProcessId.ToString());

            // Write historical compilation error to unity_background_log.txt
            string historicalErrorLog = "Assets/Scripts/Broken.cs(10,5): error CS0103: The name 'foo' does not exist in the current context\n";
            await File.WriteAllTextAsync(Path.Combine(tempDir, "unity_background_log.txt"), historicalErrorLog);

            var procManager = new UnityProcessManager(tempDir, NullLogger<UnityProcessManager>.Instance);

            // Should succeed without throwing UnityCompilationException from historical log
            var ensureTask = procManager.EnsureUnityRunningAsync(cts.Token);
            await ensureTask;
        }
        finally
        {
            cts.Cancel();
            listener.Stop();
            try { await serverTask; } catch { }
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task EnsureUnityRunningAsync_WhenSocketTemporarilyUnavailable_WaitsForReadinessWithoutAbortingOnHistoricalErrors()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "unity_pm_test_delayed_" + Guid.NewGuid().ToString("N"));
        string unityTemp = Path.Combine(tempDir, "Temp");
        Directory.CreateDirectory(unityTemp);

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var serverStartedTime = DateTime.UtcNow;

        var serverTask = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                TcpClient tcp;
                try
                {
                    tcp = await listener.AcceptTcpClientAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                _ = Task.Run(async () =>
                {
                    using (tcp)
                    using (var stream = tcp.GetStream())
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                    using (var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true })
                    {
                        // Simulate delay (e.g. domain reload) before socket responds
                        if (DateTime.UtcNow - serverStartedTime < TimeSpan.FromSeconds(2))
                        {
                            // Close connection to simulate unavailable socket during initial probe
                            return;
                        }

                        string? line = await reader.ReadLineAsync(cts.Token);
                        if (line == null) return;

                        if (line == "PING")
                        {
                            await writer.WriteLineAsync("PONG".AsMemory(), cts.Token);
                        }
                        else if (line.StartsWith("POLL_REFRESH"))
                        {
                            await writer.WriteLineAsync("READY".AsMemory(), cts.Token);
                        }
                    }
                }, cts.Token);
            }
        });

        try
        {
            // Simulate already running Unity instance
            await File.WriteAllTextAsync(Path.Combine(unityTemp, "unity_lean_mcp_port.txt"), port.ToString());
            await File.WriteAllTextAsync(Path.Combine(unityTemp, "unity_lean_mcp_process.pid"), Environment.ProcessId.ToString());

            // Write historical compilation error to unity_background_log.txt
            string historicalErrorLog = "Assets/Scripts/Broken.cs(10,5): error CS0103: The name 'foo' does not exist in the current context\n";
            await File.WriteAllTextAsync(Path.Combine(tempDir, "unity_background_log.txt"), historicalErrorLog);

            var procManager = new UnityProcessManager(tempDir, NullLogger<UnityProcessManager>.Instance);

            // Should wait for socket readiness without aborting on historical errors in WaitForSocketReadinessAsync
            await procManager.EnsureUnityRunningAsync(cts.Token);
        }
        finally
        {
            cts.Cancel();
            listener.Stop();
            try { await serverTask; } catch { }
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task WaitForSocketReadinessAsync_WhenStartedProcessNotNull_IgnoresHistoricalErrorsBeforeOffset()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "unity_pm_test_proc_hist_" + Guid.NewGuid().ToString("N"));
        string unityTemp = Path.Combine(tempDir, "Temp");
        Directory.CreateDirectory(unityTemp);

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var serverTask = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                TcpClient tcp;
                try { tcp = await listener.AcceptTcpClientAsync(cts.Token); }
                catch { break; }

                _ = Task.Run(async () =>
                {
                    using (tcp)
                    using (var stream = tcp.GetStream())
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                    using (var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true })
                    {
                        string? line = await reader.ReadLineAsync(cts.Token);
                        if (line == "PING") await writer.WriteLineAsync("PONG".AsMemory(), cts.Token);
                        else if (line != null && line.StartsWith("POLL_REFRESH")) await writer.WriteLineAsync("READY".AsMemory(), cts.Token);
                    }
                }, cts.Token);
            }
        });

        using var proc = StartDummyProcess();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(unityTemp, "unity_lean_mcp_port.txt"), port.ToString());

            // Write historical error BEFORE offset
            string logFile = Path.Combine(tempDir, "unity_background_log.txt");
            await File.WriteAllTextAsync(logFile, "Assets/Scripts/OldBroken.cs(10,5): error CS0103: The name 'old' does not exist\n");
            long initialOffset = new FileInfo(logFile).Length;

            var procManager = new UnityProcessManager(tempDir, NullLogger<UnityProcessManager>.Instance);

            // Should succeed without throwing UnityCompilationException because historical error is before offset
            await procManager.WaitForSocketReadinessAsync(proc, cts.Token, initialOffset);
        }
        finally
        {
            try { if (!proc.HasExited) proc.Kill(true); } catch { }
            cts.Cancel();
            listener.Stop();
            try { await serverTask; } catch { }
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task WaitForSocketReadinessAsync_WhenStartedProcessNotNull_ThrowsWhenNewErrorsAppendedAfterOffset()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "unity_pm_test_proc_new_" + Guid.NewGuid().ToString("N"));
        string unityTemp = Path.Combine(tempDir, "Temp");
        Directory.CreateDirectory(unityTemp);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var proc = StartDummyProcess();

        try
        {
            string logFile = Path.Combine(tempDir, "unity_background_log.txt");
            await File.WriteAllTextAsync(logFile, "Some clean startup log line\n");
            long initialOffset = new FileInfo(logFile).Length;

            // Append new error AFTER offset
            await File.AppendAllTextAsync(logFile, "Assets/Scripts/NewBroken.cs(42,1): error CS0246: The type or namespace could not be found\n");

            var procManager = new UnityProcessManager(tempDir, NullLogger<UnityProcessManager>.Instance);

            var ex = await Assert.ThrowsAsync<UnityCompilationException>(async () =>
            {
                await procManager.WaitForSocketReadinessAsync(proc, cts.Token, initialOffset);
            });

            Assert.Contains("NewBroken.cs", ex.Message);
            try { proc.WaitForExit(3000); } catch { }
            Assert.True(proc.HasExited, "Started process should have been killed when compilation error was detected.");
        }
        finally
        {
            try { if (!proc.HasExited) proc.Kill(true); } catch { }
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task WaitForSocketReadinessAsync_WhenStartedProcessExitsUnexpectedly_ThrowsInvalidOperationExceptionImmediately()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "unity_pm_test_proc_exit_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(tempDir, "Temp"));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var proc = StartDummyProcess();
        proc.Kill(true);
        proc.WaitForExit();

        try
        {
            var procManager = new UnityProcessManager(tempDir, NullLogger<UnityProcessManager>.Instance);
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await procManager.WaitForSocketReadinessAsync(proc, cts.Token);
            });

            Assert.Contains("exited unexpectedly", ex.Message);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task WaitForSocketReadinessAsync_WhenNullProcessAndNotRunning_ThrowsInvalidOperationExceptionImmediately()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "unity_pm_test_null_proc_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(tempDir, "Temp"));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            var procManager = new UnityProcessManager(tempDir, NullLogger<UnityProcessManager>.Instance)
            {
                ProcessProvider = () => Array.Empty<Process>()
            };
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await procManager.WaitForSocketReadinessAsync(null, cts.Token);
            });

            Assert.Contains("is not running", ex.Message);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void FindProjectUnityPid_WhenMultipleDummyProcessesExist_DoesNotArbitrarilyReturnFirstProcess()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "unity_pm_test_multi_pid_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(tempDir, "Temp"));

        using var proc1 = StartDummyProcess();
        using var proc2 = StartDummyProcess();

        try
        {
            var procManager = new UnityProcessManager(tempDir, NullLogger<UnityProcessManager>.Instance);

            // 1. When multiple candidate processes exist without port file, must return null (not proc1.Id)
            int? detectedPid = procManager.FindProjectUnityPid(new[] { proc1, proc2 });
            Assert.Null(detectedPid);
            Assert.NotEqual(proc1.Id, detectedPid);

            // 2. Even if PortFile exists, it does not prove which process owns it when multiple processes exist
            File.WriteAllText(procManager.PortFile, "65432");
            int? detectedPidWithPort = procManager.FindProjectUnityPid(new[] { proc1, proc2 });
            Assert.Null(detectedPidWithPort);

            // 3. Single process candidate correctly resolves
            int? singlePid = procManager.FindProjectUnityPid(new[] { proc1 });
            Assert.Equal(proc1.Id, singlePid);

            // 4. ProcessProvider delegate with multiple processes also resolves to null
            procManager.ProcessProvider = () => new[] { proc1, proc2 };
            Assert.Null(procManager.FindProjectUnityPid());
        }
        finally
        {
            try { if (!proc1.HasExited) proc1.Kill(true); } catch { }
            try { if (!proc2.HasExited) proc2.Kill(true); } catch { }
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task StopUnityAsync_WhenMultipleProcessesExistAndPidCannotBeProven_DoesNotKillProcesses()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "unity_pm_test_safe_stop_" + Guid.NewGuid().ToString("N"));
        string tempSubDir = Path.Combine(tempDir, "Temp");
        Directory.CreateDirectory(tempSubDir);

        using var proc1 = StartDummyProcess();
        using var proc2 = StartDummyProcess();

        // Lock the lockfile so IsUnityRunning sees Unity as active, but PID cannot be proven
        string lockFilePath = Path.Combine(tempSubDir, "UnityLockfile");
        using var lockStream = File.Open(lockFilePath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);

        try
        {
            var procManager = new UnityProcessManager(tempDir, NullLogger<UnityProcessManager>.Instance)
            {
                ProcessProvider = () => new[] { proc1, proc2 }
            };

            Assert.True(procManager.IsUnityRunning(out int? runningPid));
            Assert.Null(runningPid);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            bool stopped = await procManager.StopUnityAsync(cts.Token);

            Assert.False(stopped);
            Assert.False(proc1.HasExited, "proc1 should NOT have been killed by StopUnityAsync.");
            Assert.False(proc2.HasExited, "proc2 should NOT have been killed by StopUnityAsync.");
        }
        finally
        {
            try { if (!proc1.HasExited) proc1.Kill(true); } catch { }
            try { if (!proc2.HasExited) proc2.Kill(true); } catch { }
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task StopUnityAsync_WhenGuiModeAndNotForced_RefusesToStopAndReturnsFalse()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "unity_pm_test_gui_stop_" + Guid.NewGuid().ToString("N"));
        string tempSubDir = Path.Combine(tempDir, "Temp");
        Directory.CreateDirectory(tempSubDir);

        using var proc = StartDummyProcess();
        string lockFilePath = Path.Combine(tempSubDir, "UnityLockfile");
        using var lockStream = File.Open(lockFilePath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);

        try
        {
            var procManager = new UnityProcessManager(tempDir, NullLogger<UnityProcessManager>.Instance)
            {
                ProcessProvider = () => new[] { proc }
            };

            // When running without PID file, it is detected as GUI mode
            Assert.Equal("GUI", procManager.GetUnityMode());

            bool stopped = await procManager.StopUnityAsync(force: false);

            Assert.False(stopped);
            Assert.False(proc.HasExited, "proc should NOT have been killed when force is false.");
        }
        finally
        {
            try { if (!proc.HasExited) proc.Kill(true); } catch { }
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void UnityPathResolver_ResolvesExpectedPaths()
    {
        string projectRoot = Path.Combine(Path.GetTempPath(), "test_proj_paths");
        var resolver = new UnityPathResolver(projectRoot);

        Assert.Equal(Path.GetFullPath(projectRoot), resolver.ProjectRoot);
        Assert.Equal(Path.Combine(resolver.ProjectRoot, "Temp"), resolver.TempDir);
        Assert.Equal(Path.Combine(resolver.TempDir, "unity_lean_mcp_operation.json"), resolver.OperationFile);
        Assert.Equal(Path.Combine(resolver.TempDir, "unity_compilation_errors.txt"), resolver.CompilationErrorsFile);
        Assert.Equal(Path.Combine(resolver.TempDir, "unity_lean_mcp_port.txt"), resolver.PortFile);
        Assert.Equal(Path.Combine(resolver.ProjectRoot, "unity_background_log.txt"), resolver.LogFile);
        Assert.Equal(Path.Combine(resolver.TempDir, "unity_lean_mcp_process.pid"), resolver.PidFile);
        Assert.Equal(Path.Combine(resolver.TempDir, "unity_refresh_result.json"), resolver.RefreshResultFile);
        Assert.Equal(Path.Combine(resolver.TempDir, "unity_eval_result.json"), resolver.EvalResultFile);
        Assert.Equal(Path.Combine(resolver.TempDir, "unity_execute_result.json"), resolver.ExecuteResultFile);
        Assert.Equal(Path.Combine(resolver.TempDir, "unity_test_running.txt"), resolver.TestRunningFile);
        Assert.Equal(Path.Combine(resolver.TempDir, "unity_test_results.json"), resolver.TestResultsFile);
        Assert.Equal(Path.Combine(resolver.TempDir, "unity_eval_op1.json"), resolver.GetEvalResultFile("op1"));
        Assert.Equal(Path.Combine(resolver.TempDir, "unity_execute_op2.json"), resolver.GetExecuteResultFile("op2"));
        Assert.Equal(Path.Combine(resolver.TempDir, "unity_test_op3.json"), resolver.GetTestResultsFile("op3"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void UnityPathResolver_ThrowsOnNullOrEmpty(string? invalidRoot)
    {
        Assert.Throws<ArgumentException>(() => new UnityPathResolver(invalidRoot!));
    }

    [Fact]
    public void UnityProcessManager_InjectsCustomPathResolver()
    {
        string projectRoot = Path.Combine(Path.GetTempPath(), "test_custom_pm_resolver");
        var resolver = new UnityPathResolver(projectRoot);
        var pm = new UnityProcessManager(resolver, NullLogger<UnityProcessManager>.Instance);

        Assert.Same(resolver, pm.PathResolver);
        Assert.Equal(resolver.ProjectRoot, pm.ProjectRoot);
        Assert.Equal(resolver.OperationFile, pm.OperationFile);
        Assert.Equal(resolver.TempDir, pm.TempDir);
        Assert.Equal(resolver.PortFile, pm.PortFile);
        Assert.Equal(resolver.GetEvalResultFile("op1"), pm.GetEvalResultFile("op1"));
        Assert.Equal(resolver.GetExecuteResultFile("op2"), pm.GetExecuteResultFile("op2"));
        Assert.Equal(resolver.GetTestResultsFile("op3"), pm.GetTestResultsFile("op3"));
    }

    [Fact]
    public void PurgeOperationState_DeletesOrphanedOperationFiles()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "test_purge_" + Guid.NewGuid().ToString("N"));
        string unityTemp = Path.Combine(tempDir, "Temp");
        Directory.CreateDirectory(unityTemp);

        try
        {
            var pm = new UnityProcessManager(tempDir, NullLogger<UnityProcessManager>.Instance);

            string file1 = Path.Combine(unityTemp, "unity_eval_123.json");
            string file2 = Path.Combine(unityTemp, "unity_execute_456.json");
            string file3 = Path.Combine(unityTemp, "unity_test_789.json");
            string keepFile = Path.Combine(unityTemp, "other_file.txt");

            File.WriteAllText(file1, "{}");
            File.WriteAllText(file2, "{}");
            File.WriteAllText(file3, "{}");
            File.WriteAllText(keepFile, "keep");

            pm.PurgeOperationState();

            Assert.False(File.Exists(file1));
            Assert.False(File.Exists(file2));
            Assert.False(File.Exists(file3));
            Assert.True(File.Exists(keepFile));
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task OperationPoller_DeletesResultFileAfterSuccessfulRead()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "test_poller_clean_" + Guid.NewGuid().ToString("N"));
        string unityTemp = Path.Combine(tempDir, "Temp");
        Directory.CreateDirectory(unityTemp);

        try
        {
            var pm = new UnityProcessManager(tempDir, NullLogger<UnityProcessManager>.Instance);
            var poller = new OperationPoller(pm, pm.PathResolver, new UnitySocketTransport(NullLogger<UnitySocketTransport>.Instance));

            string opId = "clean_test_op";
            string resultFile = pm.GetEvalResultFile(opId);
            var evalResult = new UnityEvalResult
            {
                OperationId = opId,
                Success = true,
                Payload = "result_ok"
            };
            File.WriteAllText(resultFile, System.Text.Json.JsonSerializer.Serialize(evalResult));
            Assert.True(File.Exists(resultFile));

            var spec = new OperationPollingSpec<UnityEvalResult>
            {
                OperationId = opId,
                ResultFilePath = resultFile,
                IsMatch = r => r.OperationId == opId,
                PollCommand = $"POLL_EVAL {opId}",
                PollTimeoutSeconds = 1,
                PollIntervalMs = 50
            };

            var result = await poller.PollOperationUntilTerminalAsync(spec, CancellationToken.None);

            Assert.True(result.Success);
            Assert.Equal("result_ok", result.Payload);
            Assert.False(File.Exists(resultFile), "Result file should have been deleted by OperationPoller after terminal read.");
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }
}
