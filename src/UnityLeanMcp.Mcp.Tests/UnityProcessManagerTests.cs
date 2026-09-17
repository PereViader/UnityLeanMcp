using System;
using System.Collections.Generic;
using System.Diagnostics;
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
public class UnityProcessManagerTests
{
    private static string GetDummyExecutablePath()
    {
        string executableName = "UnityLeanMcp.Mcp.Tests" + (OperatingSystem.IsWindows() ? ".exe" : string.Empty);
        return Path.Combine(AppContext.BaseDirectory, executableName);
    }

    private static Process StartDummyProcess()
    {
        string executablePath = GetDummyExecutablePath();
        var psi = new ProcessStartInfo(executablePath)
        {
            CreateNoWindow = true,
            UseShellExecute = false
        };

        // Launch the test project's own apphost so the fixture does not
        // depend on an OS utility, shell, or machine-specific PATH entry.
        psi.ArgumentList.Add("--unity-lean-mcp-dummy-process");

        return Process.Start(psi)
            ?? throw new InvalidOperationException("The test child process could not be started.");
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
    public void ReadFileWithRetry_WhenFileInitiallyLocked_RetriesAndReadsContent()
    {
        string tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempFile, "Retry file content", Encoding.UTF8);

            using var stream = new FileStream(tempFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

            int attempts = 0;
            string result = UnityProcessManager.ReadFileWithRetry(
                tempFile,
                maxRetries: 10,
                delayMs: 25,
                fromOffset: 0,
                reader: (path, _) =>
                {
                    attempts++;
                    try
                    {
                        return File.ReadAllText(path, Encoding.UTF8);
                    }
                    catch (IOException) when (attempts == 1)
                    {
                        stream.Dispose();
                        throw;
                    }
                    catch (UnauthorizedAccessException) when (attempts == 1)
                    {
                        stream.Dispose();
                        throw;
                    }
                });

            Assert.Equal("Retry file content", result);
            Assert.Equal(2, attempts);
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
        }
    }

    [Fact]
    public async Task EnsureUnityRunningAsync_WhenProjectPortRespondsPong_DoesNotRequireProcessDiscoveryOrStartUnity()
    {
        string projectRoot = Path.Combine(Path.GetTempPath(), "unity_pm_test_live_socket_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(projectRoot, "Temp"));

        try
        {
            var resolver = new UnityPathResolver(projectRoot);
            File.WriteAllText(resolver.PortFile, "45678");
            var transport = new DiscoverySocketTransport(isReady: true);
            int startAttempts = 0;
            var manager = new UnityProcessManager(
                resolver,
                NullLogger<UnityProcessManager>.Instance,
                socketTransport: transport,
                executableLocator: new FixedExecutableLocator())
            {
                ProcessStarter = _ =>
                {
                    startAttempts++;
                    throw new InvalidOperationException("A live project socket must prevent auto-start.");
                }
            };

            await manager.EnsureUnityRunningAsync();

            Assert.Equal(1, transport.ReadinessProbeCount);
            Assert.Equal(0, startAttempts);
        }
        finally
        {
            try { Directory.Delete(projectRoot, true); } catch { }
        }
    }

    [Fact]
    public async Task EnsureUnityRunningAsync_WhenProjectPortIsStale_FallsBackToAutoStart()
    {
        string projectRoot = Path.Combine(Path.GetTempPath(), "unity_pm_test_stale_socket_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(projectRoot, "Temp"));

        try
        {
            var resolver = new UnityPathResolver(projectRoot);
            File.WriteAllText(resolver.PortFile, "45678");
            var transport = new DiscoverySocketTransport(isReady: false);
            int startAttempts = 0;
            var manager = new UnityProcessManager(
                resolver,
                NullLogger<UnityProcessManager>.Instance,
                socketTransport: transport,
                executableLocator: new FixedExecutableLocator())
            {
                ProcessStarter = _ =>
                {
                    startAttempts++;
                    throw new InvalidOperationException("Expected startup attempt after stale socket discovery.");
                }
            };

            await Assert.ThrowsAsync<InvalidOperationException>(() => manager.EnsureUnityRunningAsync());

            Assert.True(transport.ReadinessProbeCount >= 2);
            Assert.Equal(1, startAttempts);
        }
        finally
        {
            try { Directory.Delete(projectRoot, true); } catch { }
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

            var procManager = new UnityProcessManager(tempDir, NullLogger<UnityProcessManager>.Instance)
            {
                // The test uses the host test process as a stand-in for Unity.
                // Inject it explicitly now that production PID files require ownership metadata.
                ProcessProvider = () => new[] { Process.GetCurrentProcess() }
            };

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

            var procManager = new UnityProcessManager(tempDir, NullLogger<UnityProcessManager>.Instance)
            {
                // The test uses the host test process as a stand-in for Unity.
                // Inject it explicitly now that production PID files require ownership metadata.
                ProcessProvider = () => new[] { Process.GetCurrentProcess() }
            };

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
    public async Task EnsureUnityRunningAsync_ConcurrentCallsShareProjectStartupAndDisposeLaunchedProcess()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "unity_pm_test_concurrent_start_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(tempDir, "Temp"));

        var firstReadinessEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseReadiness = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var manager = new CoordinatedStartupProcessManager(
            tempDir,
            firstReadinessEntered,
            releaseReadiness,
            NullLogger<UnityProcessManager>.Instance);

        try
        {
            Task first = manager.EnsureUnityRunningAsync();
            await firstReadinessEntered.Task;

            Task second = manager.EnsureUnityRunningAsync();
            releaseReadiness.SetResult(true);

            await Task.WhenAll(first, second);

            Assert.Equal(1, manager.StartCount);
            Assert.NotNull(manager.StartedProcess);
            Assert.Throws<InvalidOperationException>(() => manager.StartedProcess!.HasExited);
        }
        finally
        {
            releaseReadiness.TrySetResult(true);
            try
            {
                if (manager.StartedPid is int pid)
                {
                    using var process = Process.GetProcessById(pid);
                    if (!process.HasExited)
                    {
                        process.Kill(true);
                    }
                }
            }
            catch { }

            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void IsUnityRunning_LiveUnrelatedPidInPidFileIsRejected()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "unity_pm_test_unrelated_pid_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(tempDir, "Temp"));
        using var unrelatedProcess = StartDummyProcess();

        try
        {
            var manager = new UnityProcessManager(tempDir, NullLogger<UnityProcessManager>.Instance);
            File.WriteAllText(manager.PathResolver.PidFile, unrelatedProcess.Id.ToString());

            Assert.False(manager.IsUnityRunning(out int? processId));
            Assert.Null(processId);
            Assert.False(File.Exists(manager.PathResolver.PidFile));
        }
        finally
        {
            try { if (!unrelatedProcess.HasExited) unrelatedProcess.Kill(true); } catch { }
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void IsUnityRunning_ReusedPidWithMismatchedIdentityIsRejected()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "unity_pm_test_reused_pid_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(tempDir, "Temp"));
        using var process = StartDummyProcess();

        try
        {
            var manager = new UnityProcessManager(tempDir, NullLogger<UnityProcessManager>.Instance);
            File.WriteAllText(manager.PathResolver.PidFile, process.Id.ToString());
            File.WriteAllText(
                manager.PathResolver.PidFile + ".identity.json",
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    ProcessId = process.Id,
                    StartTimeUtcTicks = DateTime.UtcNow.AddHours(-1).Ticks,
                    ExecutablePath = GetDummyExecutablePath(),
                    ProjectRoot = manager.PathResolver.ProjectRoot
                }));

            Assert.False(manager.IsUnityRunning(out int? processId));
            Assert.Null(processId);
            Assert.False(File.Exists(manager.PathResolver.PidFile + ".identity.json"));
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(true); } catch { }
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void IsUnityRunning_MatchingPidIdentityIsAccepted()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "unity_pm_test_owned_pid_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(tempDir, "Temp"));
        using var process = StartDummyProcess();

        try
        {
            var manager = new UnityProcessManager(tempDir, NullLogger<UnityProcessManager>.Instance);
            File.WriteAllText(manager.PathResolver.PidFile, process.Id.ToString());
            File.WriteAllText(
                manager.PathResolver.PidFile + ".identity.json",
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    ProcessId = process.Id,
                    StartTimeUtcTicks = process.StartTime.ToUniversalTime().Ticks,
                    ExecutablePath = GetDummyExecutablePath(),
                    ProjectRoot = manager.PathResolver.ProjectRoot
                }));

            Assert.True(manager.IsUnityRunning(out int? processId));
            Assert.Equal(process.Id, processId);
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(true); } catch { }
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void IsUnityRunning_MatchingIdentityRecoversWhenPidPointerIsMissing()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "unity_pm_test_identity_recovery_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(tempDir, "Temp"));
        using var process = Process.GetCurrentProcess();

        try
        {
            var manager = new UnityProcessManager(tempDir, NullLogger<UnityProcessManager>.Instance);
            File.WriteAllText(
                manager.PathResolver.PidFile + ".identity.json",
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    ProcessId = process.Id,
                    StartTimeUtcTicks = process.StartTime.ToUniversalTime().Ticks,
                    ExecutablePath = process.MainModule!.FileName,
                    ProjectRoot = manager.PathResolver.ProjectRoot
                }));

            Assert.True(manager.IsUnityRunning(out int? processId));
            Assert.Equal(process.Id, processId);
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
            File.WriteAllText(procManager.PathResolver.PortFile, "65432");
            int? detectedPidWithPort = procManager.FindProjectUnityPid(new[] { proc1, proc2 });
            Assert.Null(detectedPidWithPort);

            // 3. A single candidate without project proof is still not owned.
            int? singlePid = procManager.FindProjectUnityPid(new[] { proc1 });
            Assert.Null(singlePid);

            // 4. ProcessProvider delegate with multiple processes also resolves to null
            var procManagerWithMultiple = new UnityProcessManager(
                procManager.PathResolver,
                NullLogger<UnityProcessManager>.Instance,
                executableLocator: procManager.ExecutableLocator,
                processProvider: () => new[] { proc1, proc2 });
            Assert.Null(procManagerWithMultiple.FindProjectUnityPid());
        }
        finally
        {
            try { if (!proc1.HasExited) proc1.Kill(true); } catch { }
            try { if (!proc2.HasExited) proc2.Kill(true); } catch { }
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Theory]
    [InlineData("/Users/example/Unity.app/Contents/MacOS/Unity -projectPath \"{project}\"")]
    [InlineData("C:\\Program Files\\Unity\\Editor\\Unity.exe -projectPath=\"{project}\"")]
    public void FindProjectUnityPid_AcceptsExplicitProjectCommandLineEvidenceAcrossPlatforms(string commandLineTemplate)
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "unity_pm_test_command_line_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(tempDir, "Temp"));
        using var candidate = StartDummyProcess();

        try
        {
            string platformProjectPath = commandLineTemplate.StartsWith("C:", StringComparison.Ordinal)
                ? tempDir.Replace(Path.DirectorySeparatorChar, '\\').Replace(Path.AltDirectorySeparatorChar, '\\')
                : tempDir.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
            var procManager = new UnityProcessManager(tempDir, NullLogger<UnityProcessManager>.Instance)
            {
                ProcessProvider = () => new[] { candidate },
                ProcessCommandLineProvider = _ => commandLineTemplate.Replace("{project}", platformProjectPath, StringComparison.Ordinal)
            };

            Assert.Equal(candidate.Id, procManager.FindProjectUnityPid());
            Assert.True(procManager.IsUnityRunning(out int? processId));
            Assert.Equal(candidate.Id, processId);
        }
        finally
        {
            try { if (!candidate.HasExited) candidate.Kill(true); } catch { }
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void FindProjectUnityPid_DoesNotTreatUnrelatedCommandLineAsProjectOwnership()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "unity_pm_test_unrelated_command_line_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(tempDir, "Temp"));
        using var candidate = StartDummyProcess();

        try
        {
            var procManager = new UnityProcessManager(tempDir, NullLogger<UnityProcessManager>.Instance)
            {
                ProcessProvider = () => new[] { candidate },
                ProcessCommandLineProvider = _ => "Unity -projectPath /tmp/a-different-project"
            };

            Assert.Null(procManager.FindProjectUnityPid());
            Assert.False(procManager.IsUnityRunning(out int? processId));
            Assert.Null(processId);
        }
        finally
        {
            try { if (!candidate.HasExited) candidate.Kill(true); } catch { }
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void MacOsCommandLineReader_DoesNotConsumeEnvironmentAfterArgv()
    {
        // KERN_PROCARGS2 layout:
        // [exec_path\0] [null-padding] [argv[0]\0] [argv[1]\0] [argv[2]\0] [envp\0]
        byte[] bytes = Encoding.UTF8.GetBytes(
            "/Applications/Unity.app/Contents/MacOS/Unity\0\0\0\0" +
            "/Applications/Unity.app/Contents/MacOS/Unity\0-projectPath\0/Users/example/Project\0" +
            "SHOULD_NOT_BE_USED=true\0");

        Assert.True(
            UnityProcessCommandLineReader.TryBuildMacOsCommandLine(
                bytes,
                argc: 3,
                out string commandLine));
        Assert.Equal(
            "/Applications/Unity.app/Contents/MacOS/Unity\0-projectPath\0/Users/example/Project",
            commandLine);
        Assert.DoesNotContain("SHOULD_NOT_BE_USED", commandLine, StringComparison.Ordinal);
    }

    [Fact]
    public void MacOsCommandLineReader_PreservesAllArgcArgumentsIncludingLast()
    {
        // 5 arguments: argv[0..4]
        byte[] bytes = Encoding.UTF8.GetBytes(
            "/Applications/Unity.app/Contents/MacOS/Unity\0\0\0\0" +
            "/Applications/Unity.app/Contents/MacOS/Unity\0-batchmode\0-nographics\0-projectPath\0/Users/example/FinalArgPath\0" +
            "PATH=/usr/bin\0USER=alice\0");

        Assert.True(
            UnityProcessCommandLineReader.TryBuildMacOsCommandLine(
                bytes,
                argc: 5,
                out string commandLine));

        string[] args = commandLine.Split('\0');
        Assert.Equal(5, args.Length);
        Assert.Equal("/Applications/Unity.app/Contents/MacOS/Unity", args[0]);
        Assert.Equal("-batchmode", args[1]);
        Assert.Equal("-nographics", args[2]);
        Assert.Equal("-projectPath", args[3]);
        Assert.Equal("/Users/example/FinalArgPath", args[4]);
        Assert.DoesNotContain("PATH=/usr/bin", commandLine, StringComparison.Ordinal);
    }

    [Fact]
    public void IsUnityRunning_LockedUnityLockfileWithOnlyUnprovenCandidatePreventsAutoStartWithoutAssigningOwnership()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "unity_pm_test_unproven_lock_" + Guid.NewGuid().ToString("N"));
        string tempSubDir = Path.Combine(tempDir, "Temp");
        Directory.CreateDirectory(tempSubDir);

        using var candidate = StartDummyProcess();
        string lockFilePath = Path.Combine(tempSubDir, "UnityLockfile");
        using var lockStream = File.Open(lockFilePath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);

        try
        {
            var procManager = new UnityProcessManager(tempDir, NullLogger<UnityProcessManager>.Instance)
            {
                ProcessProvider = () => new[] { candidate }
            };

            Assert.True(procManager.IsUnityRunning(out int? processId));
            Assert.Null(processId);
            Assert.True(File.Exists(lockFilePath), "An actively held Unity lockfile must not be deleted while probing ownership.");
        }
        finally
        {
            try { if (!candidate.HasExited) candidate.Kill(true); } catch { }
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task EnsureUnityRunningAsync_WhenProjectLockIsHeldButItsProcessCannotBeAttributed_WaitsWithoutStartingBatchUnity()
    {
        string projectRoot = Path.Combine(Path.GetTempPath(), "unity_pm_test_unattributed_lock_" + Guid.NewGuid().ToString("N"));
        string tempDir = Path.Combine(projectRoot, "Temp");
        Directory.CreateDirectory(tempDir);

        using var unprovenCandidate = StartDummyProcess();
        string lockFilePath = Path.Combine(tempDir, "UnityLockfile");
        using var lockStream = File.Open(lockFilePath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);

        try
        {
            var manager = new LockAwareProcessManager(
                new UnityPathResolver(projectRoot),
                NullLogger<UnityProcessManager>.Instance,
                new DiscoverySocketTransport(isReady: false))
            {
                ProcessProvider = () => new[] { unprovenCandidate },
                ProcessStarter = _ => throw new InvalidOperationException(
                    "A held project lock must never cause a conflicting batchmode launch.")
            };

            await manager.EnsureUnityRunningAsync();

            Assert.True(manager.WaitedForExistingEditorSocket);
        }
        finally
        {
            try { if (!unprovenCandidate.HasExited) unprovenCandidate.Kill(true); } catch { }
            try { Directory.Delete(projectRoot, true); } catch { }
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

        // Lock the lockfile so IsUnityRunning sees Unity as active, but does not
        // receive enough ownership evidence to terminate either candidate.
        string lockFilePath = Path.Combine(tempSubDir, "UnityLockfile");
        using var lockStream = File.Open(lockFilePath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);

        string operationFile = Path.Combine(tempSubDir, "unity_lean_mcp_operation.json");
        string testRunningFile = Path.Combine(tempSubDir, "unity_test_running.txt");
        string refreshResultFile = Path.Combine(tempSubDir, "unity_refresh_result.json");
        File.WriteAllText(operationFile, "operation");
        File.WriteAllText(testRunningFile, "running");
        File.WriteAllText(refreshResultFile, "refresh history");

        try
        {
            var procManager = new UnityProcessManager(tempDir, NullLogger<UnityProcessManager>.Instance)
            {
                ProcessProvider = () => new[] { proc1, proc2 }
            };

            Assert.True(procManager.IsUnityRunning(out int? runningPid));
            Assert.Null(runningPid);

            bool stopped = await procManager.StopUnityAsync(force: true);

            Assert.False(stopped, "StopUnityAsync must refuse to stop an unattributed Editor.");
            Assert.False(proc1.HasExited, "proc1 should NOT have been killed by StopUnityAsync.");
            Assert.False(proc2.HasExited, "proc2 should NOT have been killed by StopUnityAsync.");
            Assert.True(File.Exists(operationFile), "Operation state must be preserved when Editor ownership is unproven.");
            Assert.True(File.Exists(testRunningFile), "Test-running state must be preserved when Editor ownership is unproven.");
            Assert.True(File.Exists(refreshResultFile), "Shared refresh history must be preserved.");
        }
        finally
        {
            try { if (!proc1.HasExited) proc1.Kill(true); } catch { }
            try { if (!proc2.HasExited) proc2.Kill(true); } catch { }
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task EnsureUnityRunningAsync_ConcurrentManagersShareInterProcessStartupClaim()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "unity_pm_test_interprocess_start_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(tempDir, "Temp"));

        var state = new SharedStartupState();
        var first = new SharedStartupProcessManager(tempDir, state, NullLogger<UnityProcessManager>.Instance);
        var second = new SharedStartupProcessManager(tempDir, state, NullLogger<UnityProcessManager>.Instance);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        try
        {
            Task firstStartup = first.EnsureUnityRunningAsync(cancellation.Token);
            await state.FirstReadinessEntered.Task.WaitAsync(cancellation.Token);

            Task secondStartup = second.EnsureUnityRunningAsync(cancellation.Token);
            state.ReleaseReadiness.TrySetResult(true);

            await Task.WhenAll(firstStartup, secondStartup);

            Assert.Equal(1, Volatile.Read(ref state.StartCount));
            Assert.True(File.Exists(first.PathResolver.StartupLockFile));
        }
        finally
        {
            state.ReleaseReadiness.TrySetResult(true);
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task StopUnityAsync_WhenExitIsAcknowledged_WaitsUntilUnityExits()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "unity_pm_test_wait_for_stop_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(tempDir, "Temp"));
        var manager = new ControlledStopProcessManager(tempDir, NullLogger<UnityProcessManager>.Instance);

        try
        {
            Task<bool> stopTask = manager.StopUnityAsync(force: true);
            await manager.ExitRequested.Task;

            Assert.False(stopTask.IsCompleted);
            manager.MarkExited();

            Assert.True(await stopTask);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task StopUnityAsync_WhenProcessRemainsRunning_StopsOnlyWhenCancelled()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "unity_pm_test_cancel_stop_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(tempDir, "Temp"));
        var manager = new ControlledStopProcessManager(tempDir, NullLogger<UnityProcessManager>.Instance);
        using var cancellation = new CancellationTokenSource();

        try
        {
            Task<bool> stopTask = manager.StopUnityAsync(force: true, cancellation.Token);
            await manager.ExitRequested.Task;
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await stopTask);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task StopUnityAsync_WhenFallbackIdentityDoesNotMatch_DoesNotKillPidReuse()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "unity_pm_test_stop_pid_reuse_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(tempDir, "Temp"));
        using var currentProcess = Process.GetCurrentProcess();
        var manager = new ReusedPidStopProcessManager(tempDir, NullLogger<UnityProcessManager>.Instance, currentProcess.Id);

        try
        {
            File.WriteAllText(manager.PathResolver.PidFile, currentProcess.Id.ToString());
            File.WriteAllText(
                manager.PathResolver.PidFile + ".identity.json",
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    ProcessId = currentProcess.Id,
                    StartTimeUtcTicks = currentProcess.StartTime.ToUniversalTime().Ticks - 1,
                    ExecutablePath = currentProcess.MainModule!.FileName,
                    ProjectRoot = manager.PathResolver.ProjectRoot
                }));

            Assert.False(await manager.StopUnityAsync(force: true));
            Assert.False(currentProcess.HasExited);
        }
        finally
        {
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

        string operationFile = Path.Combine(tempSubDir, "unity_lean_mcp_operation.json");
        string testRunningFile = Path.Combine(tempSubDir, "unity_test_running.txt");
        string refreshResultFile = Path.Combine(tempSubDir, "unity_refresh_result.json");
        File.WriteAllText(operationFile, "operation");
        File.WriteAllText(testRunningFile, "running");
        File.WriteAllText(refreshResultFile, "refresh history");

        try
        {
            var procManager = new UnityProcessManager(tempDir, NullLogger<UnityProcessManager>.Instance);

            // The sidecar is the explicit ownership proof for this fixture.
            // Keep the PID pointer absent so the proven process still follows
            // the GUI-mode path exercised by this test.
            File.WriteAllText(
                procManager.PathResolver.PidFile + ".identity.json",
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    ProcessId = proc.Id,
                    StartTimeUtcTicks = proc.StartTime.ToUniversalTime().Ticks,
                    ExecutablePath = proc.MainModule!.FileName,
                    ProjectRoot = procManager.PathResolver.ProjectRoot
                }));

            // When running without the PID pointer, the explicitly proven
            // process is still detected as GUI mode.
            Assert.Equal("GUI", procManager.GetUnityMode());

            procManager.PurgeOperationState();
            Assert.True(File.Exists(operationFile), "PurgeOperationState must preserve state while Unity is running.");

            bool stopped = await procManager.StopUnityAsync(force: false);

            Assert.False(stopped);
            Assert.False(proc.HasExited, "proc should NOT have been killed when force is false.");
            Assert.True(File.Exists(operationFile), "Operation state must be preserved after GUI-stop refusal.");
            Assert.True(File.Exists(testRunningFile), "Running marker must be preserved after GUI-stop refusal.");
            Assert.True(File.Exists(refreshResultFile), "Shared refresh history must be preserved.");
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
        Assert.Equal(Path.Combine(resolver.TempDir, "unity_lean_mcp_startup.lock"), resolver.StartupLockFile);
        Assert.Equal(Path.Combine(resolver.TempDir, "unity_refresh_result.json"), resolver.GetResultFilePath(UnityOperationKind.Refresh));
        Assert.Equal(Path.Combine(resolver.TempDir, "unity_refresh_result.json"), resolver.GetResultFilePath(UnityOperationKind.Recompile));
        Assert.Equal(Path.Combine(resolver.TempDir, "unity_eval_result.json"), resolver.GetResultFilePath(UnityOperationKind.Eval));
        Assert.Equal(Path.Combine(resolver.TempDir, "unity_test_running.txt"), resolver.TestRunningFile);
        Assert.Equal(Path.Combine(resolver.TempDir, "unity_test_results.json"), resolver.GetResultFilePath(UnityOperationKind.Test));
        Assert.Equal(Path.Combine(resolver.TempDir, "unity_refresh_op0.json"), resolver.GetResultFilePath(UnityOperationKind.Refresh, "op0"));
        Assert.Equal(Path.Combine(resolver.TempDir, "unity_refresh_op01.json"), resolver.GetResultFilePath(UnityOperationKind.Recompile, "op01"));
        Assert.Equal(Path.Combine(resolver.TempDir, "unity_eval_op1.json"), resolver.GetResultFilePath(UnityOperationKind.Eval, "op1"));
        Assert.Equal(Path.Combine(resolver.TempDir, "unity_test_op3.json"), resolver.GetResultFilePath(UnityOperationKind.Test, "op3"));
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
        Assert.Equal(resolver.ProjectRoot, pm.PathResolver.ProjectRoot);
        Assert.Equal(resolver.OperationFile, pm.PathResolver.OperationFile);
        Assert.Equal(resolver.TempDir, pm.PathResolver.TempDir);
        Assert.Equal(resolver.PortFile, pm.PathResolver.PortFile);
        Assert.Equal(resolver.StartupLockFile, pm.PathResolver.StartupLockFile);
        Assert.Equal(resolver.GetResultFilePath(UnityOperationKind.Eval, "op1"), pm.PathResolver.GetResultFilePath(UnityOperationKind.Eval, "op1"));
        Assert.Equal(resolver.GetResultFilePath(UnityOperationKind.Test, "op3"), pm.PathResolver.GetResultFilePath(UnityOperationKind.Test, "op3"));
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
            string file2 = Path.Combine(unityTemp, "unity_recompile_456.json");
            string file3 = Path.Combine(unityTemp, "unity_test_789.json");
            string refreshResult = Path.Combine(unityTemp, "unity_refresh_result.json");
            string testResults = Path.Combine(unityTemp, "unity_test_results.json");
            string keepFile = Path.Combine(unityTemp, "other_file.txt");

            File.WriteAllText(file1, "{}");
            File.WriteAllText(file2, "{}");
            File.WriteAllText(file3, "{}");
            File.WriteAllText(refreshResult, "refresh history");
            File.WriteAllText(testResults, "test history");
            File.WriteAllText(keepFile, "keep");

            pm.PurgeOperationState();

            Assert.False(File.Exists(file1));
            Assert.False(File.Exists(file2));
            Assert.False(File.Exists(file3));
            Assert.True(File.Exists(refreshResult));
            Assert.True(File.Exists(testResults));
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
            string resultFile = pm.PathResolver.GetResultFilePath(UnityOperationKind.Eval, opId);
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

    [Fact]
    public async Task OperationPoller_StaticResultFile_WithoutOperationId_PreservesResultFileAfterTerminalRead()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "test_poller_static_preserve_" + Guid.NewGuid().ToString("N"));
        string unityTemp = Path.Combine(tempDir, "Temp");
        Directory.CreateDirectory(unityTemp);

        try
        {
            var pm = new UnityProcessManager(tempDir, NullLogger<UnityProcessManager>.Instance);
            var poller = new OperationPoller(pm, pm.PathResolver, new UnitySocketTransport(NullLogger<UnitySocketTransport>.Instance));

            string opId = "refresh_op_123";
            string resultFile = pm.PathResolver.GetResultFilePath(UnityOperationKind.Refresh);
            var refreshResult = new UnityRefreshResult
            {
                OperationId = opId,
                Success = true,
                Message = "Refresh completed"
            };
            File.WriteAllText(resultFile, System.Text.Json.JsonSerializer.Serialize(refreshResult));
            Assert.True(File.Exists(resultFile));

            var spec = new OperationPollingSpec<UnityRefreshResult>
            {
                OperationId = opId,
                ResultFilePath = resultFile,
                IsMatch = r => r.OperationId == opId,
                PollCommand = $"POLL_REFRESH {opId}",
                PollTimeoutSeconds = 1,
                PollIntervalMs = 50,
                DeleteResultFileOnCompletion = null
            };

            var result = await poller.PollOperationUntilTerminalAsync(spec, CancellationToken.None);

            Assert.True(result.Success);
            Assert.True(File.Exists(resultFile), "Shared static result file should be preserved after terminal read.");
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task OperationPoller_StaticResultFile_WhenDeleteExplicitlyTrue_DeletesResultFileAfterTerminalRead()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "test_poller_static_del_" + Guid.NewGuid().ToString("N"));
        string unityTemp = Path.Combine(tempDir, "Temp");
        Directory.CreateDirectory(unityTemp);

        try
        {
            var pm = new UnityProcessManager(tempDir, NullLogger<UnityProcessManager>.Instance);
            var poller = new OperationPoller(pm, pm.PathResolver, new UnitySocketTransport(NullLogger<UnitySocketTransport>.Instance));

            string opId = "refresh_op_456";
            string resultFile = pm.PathResolver.GetResultFilePath(UnityOperationKind.Refresh);
            var refreshResult = new UnityRefreshResult
            {
                OperationId = opId,
                Success = true,
                Message = "Refresh completed"
            };
            File.WriteAllText(resultFile, System.Text.Json.JsonSerializer.Serialize(refreshResult));
            Assert.True(File.Exists(resultFile));

            var spec = new OperationPollingSpec<UnityRefreshResult>
            {
                OperationId = opId,
                ResultFilePath = resultFile,
                IsMatch = r => r.OperationId == opId,
                PollCommand = $"POLL_REFRESH {opId}",
                PollTimeoutSeconds = 1,
                PollIntervalMs = 50,
                DeleteResultFileOnCompletion = true
            };

            var result = await poller.PollOperationUntilTerminalAsync(spec, CancellationToken.None);

            Assert.True(result.Success);
            Assert.False(File.Exists(resultFile), "Static result file should be deleted when DeleteResultFileOnCompletion is explicitly true.");
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task OperationPoller_OperationScopedResultFile_WhenDeleteExplicitlyFalse_PreservesResultFileAfterTerminalRead()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "test_poller_scoped_keep_" + Guid.NewGuid().ToString("N"));
        string unityTemp = Path.Combine(tempDir, "Temp");
        Directory.CreateDirectory(unityTemp);

        try
        {
            var pm = new UnityProcessManager(tempDir, NullLogger<UnityProcessManager>.Instance);
            var poller = new OperationPoller(pm, pm.PathResolver, new UnitySocketTransport(NullLogger<UnitySocketTransport>.Instance));

            string opId = "eval_keep_op";
            string resultFile = pm.PathResolver.GetResultFilePath(UnityOperationKind.Eval, opId);
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
                PollIntervalMs = 50,
                DeleteResultFileOnCompletion = false
            };

            var result = await poller.PollOperationUntilTerminalAsync(spec, CancellationToken.None);

            Assert.True(result.Success);
            Assert.True(File.Exists(resultFile), "Operation-scoped result file should NOT be deleted when DeleteResultFileOnCompletion is false.");
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Theory]
    [InlineData("Temp/unity_refresh_result.json", "refresh_123", false)]
    [InlineData("Temp/unity_eval_op123.json", "op123", true)]
    [InlineData("Temp/unity_test_OP456.json", "op456", true)]
    [InlineData("Temp/unity_recompile_op789.json", "OP789", true)]
    [InlineData("Temp/unity_refresh_result.json", null, false)]
    [InlineData(null, "op123", false)]
    [InlineData("", "", false)]
    public void OperationPoller_IsOperationScopedResultFile_IdentifiesScopedFilesAccurately(string? filePath, string? opId, bool expected)
    {
        bool actual = OperationPoller.IsOperationScopedResultFile(filePath!, opId);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void FindProjectUnityPid_WhenProcessesAllocatedByGetUnityProcesses_DisposesProcessesInFinally()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "unity_pm_test_dispose_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(tempDir, "Temp"));

        using var dummy = StartDummyProcess();
        var processHandleToDispose = Process.GetProcessById(dummy.Id);

        try
        {
            var procManager = new DisposingTestProcessManager(tempDir, NullLogger<UnityProcessManager>.Instance, processHandleToDispose);

            int? result = procManager.FindProjectUnityPid();
            Assert.Null(result);

            // The process handle allocated by GetUnityProcesses should have been disposed in finally
            Assert.Throws<InvalidOperationException>(() => processHandleToDispose.HasExited);
        }
        finally
        {
            try { if (!dummy.HasExited) dummy.Kill(true); } catch { }
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    private class DisposingTestProcessManager : UnityProcessManager
    {
        private readonly Process _processToReturn;

        public DisposingTestProcessManager(string projectRoot, Microsoft.Extensions.Logging.ILogger<UnityProcessManager> logger, Process processToReturn)
            : base(projectRoot, logger)
        {
            _processToReturn = processToReturn;
        }

        internal override Process[] GetUnityProcesses()
        {
            return new[] { _processToReturn };
        }
    }

    private sealed class FixedExecutableLocator : IUnityExecutableLocator
    {
        public UnityLocatorResult FindUnityExecutable() => UnityLocatorResult.Found("/test/unity-editor");
    }

    private sealed class DiscoverySocketTransport : IUnitySocketTransport
    {
        private readonly bool _isReady;

        public DiscoverySocketTransport(bool isReady)
        {
            _isReady = isReady;
        }

        public int ReadinessProbeCount { get; private set; }

        public Task<string?> SendCommandAsync(
            int port,
            string command,
            int timeoutSeconds = 10,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(_isReady && command == "PING" ? "PONG" : null);

        public Task<bool> IsSocketReadyAsync(
            int port,
            int timeoutSeconds = 2,
            CancellationToken cancellationToken = default)
        {
            ReadinessProbeCount++;
            return Task.FromResult(_isReady);
        }
    }

    private sealed class LockAwareProcessManager : UnityProcessManager
    {
        public bool WaitedForExistingEditorSocket { get; private set; }

        public LockAwareProcessManager(
            UnityPathResolver pathResolver,
            Microsoft.Extensions.Logging.ILogger<UnityProcessManager> logger,
            IUnitySocketTransport socketTransport)
            : base(pathResolver, logger, socketTransport: socketTransport, executableLocator: new FixedExecutableLocator())
        {
        }

        internal override Task WaitForSocketReadinessAsync(
            Process? startedProcess,
            CancellationToken cancellationToken,
            long initialLogOffset = 0)
        {
            WaitedForExistingEditorSocket = true;
            return Task.CompletedTask;
        }
    }

    private sealed class SharedStartupState
    {
        public int StartCount;
        public bool Running;
        public TaskCompletionSource<bool> FirstReadinessEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ReleaseReadiness { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class SharedStartupProcessManager : UnityProcessManager
    {
        private readonly SharedStartupState _state;

        public SharedStartupProcessManager(
            string projectRoot,
            SharedStartupState state,
            Microsoft.Extensions.Logging.ILogger<UnityProcessManager> logger)
            : base(
                new UnityPathResolver(projectRoot),
                logger,
                executableLocator: new FixedExecutableLocator())
        {
            _state = state;
            ProcessStarter = _ =>
            {
                Interlocked.Increment(ref _state.StartCount);
                return Process.GetCurrentProcess();
            };
        }

        public override bool IsUnityRunning(out int? processId)
        {
            bool running = Volatile.Read(ref _state.Running);
            processId = running ? Environment.ProcessId : null;
            return running;
        }

        public override Task<bool> IsSocketReadyAsync(
            int timeoutSeconds = 2,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Volatile.Read(ref _state.Running));

        internal override async Task WaitForSocketReadinessAsync(
            Process? startedProcess,
            CancellationToken cancellationToken,
            long initialLogOffset = 0)
        {
            _state.FirstReadinessEntered.TrySetResult(true);
            await _state.ReleaseReadiness.Task.WaitAsync(cancellationToken);
            Volatile.Write(ref _state.Running, true);
        }
    }

    private sealed class CoordinatedStartupProcessManager : UnityProcessManager
    {
        private readonly TaskCompletionSource<bool> _firstReadinessEntered;
        private readonly TaskCompletionSource<bool> _releaseReadiness;
        private bool _running;
        private int? _startedPid;

        public int StartCount { get; private set; }
        public int? StartedPid => _startedPid;
        public Process? StartedProcess { get; private set; }

        public CoordinatedStartupProcessManager(
            string projectRoot,
            TaskCompletionSource<bool> firstReadinessEntered,
            TaskCompletionSource<bool> releaseReadiness,
            Microsoft.Extensions.Logging.ILogger<UnityProcessManager> logger)
            : base(
                new UnityPathResolver(projectRoot),
                logger,
                executableLocator: new FixedExecutableLocator())
        {
            _firstReadinessEntered = firstReadinessEntered;
            _releaseReadiness = releaseReadiness;
            ProcessStarter = _ =>
            {
                StartCount++;
                StartedProcess = StartDummyProcess();
                _startedPid = StartedProcess.Id;
                return StartedProcess;
            };
        }

        public override bool IsUnityRunning(out int? processId)
        {
            processId = _running ? _startedPid : null;
            return _running;
        }

        public override Task<bool> IsSocketReadyAsync(int timeoutSeconds = 2, CancellationToken cancellationToken = default) =>
            Task.FromResult(Volatile.Read(ref _running));

        internal override async Task WaitForSocketReadinessAsync(Process? startedProcess, CancellationToken cancellationToken, long initialLogOffset = 0)
        {
            _firstReadinessEntered.TrySetResult(true);
            await _releaseReadiness.Task.WaitAsync(cancellationToken);
            _running = true;
        }
    }

    private class ControlledStopProcessManager : UnityProcessManager
    {
        private int _running = 1;

        public TaskCompletionSource<bool> ExitRequested { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ControlledStopProcessManager(string projectRoot, Microsoft.Extensions.Logging.ILogger<UnityProcessManager> logger)
            : base(projectRoot, logger)
        {
        }

        public override bool IsUnityRunning(out int? processId)
        {
            bool running = Volatile.Read(ref _running) != 0;
            processId = running ? 12345 : null;
            return running;
        }

        public override string GetUnityMode(int? pid = null) => "Batchmode";

        public override Task<string?> ProbeSocketCommandAsync(
            string command,
            int timeoutSeconds = 2,
            CancellationToken cancellationToken = default)
        {
            ExitRequested.TrySetResult(true);
            return Task.FromResult<string?>("EXITING");
        }

        public void MarkExited() => Volatile.Write(ref _running, 0);
    }

    private sealed class ReusedPidStopProcessManager : UnityProcessManager
    {
        private readonly int _pid;

        public ReusedPidStopProcessManager(
            string projectRoot,
            Microsoft.Extensions.Logging.ILogger<UnityProcessManager> logger,
            int pid)
            : base(projectRoot, logger)
        {
            _pid = pid;
        }

        public override bool IsUnityRunning(out int? processId)
        {
            processId = _pid;
            return true;
        }

        public override string GetUnityMode(int? pid = null) => "Batchmode";

        public override Task<string?> ProbeSocketCommandAsync(
            string command,
            int timeoutSeconds = 2,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);
    }
}
