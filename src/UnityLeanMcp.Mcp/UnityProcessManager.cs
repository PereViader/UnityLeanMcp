using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace UnityLeanMcp.Mcp;

public class UnityProcessManager : IUnityProcessManager
{
    private readonly IUnityPathResolver _pathResolver;
    private readonly ILogger<UnityProcessManager> _logger;
    private readonly IUnitySocketTransport _socketTransport;
    private readonly IUnityLogScanner _logScanner;
    private readonly IUnityExecutableLocator _executableLocator;
    private int? _launchedPid;

    public IUnityPathResolver PathResolver => _pathResolver;
    public IUnityExecutableLocator ExecutableLocator => _executableLocator;
    public string ProjectRoot => _pathResolver.ProjectRoot;
    public string TempDir => _pathResolver.TempDir;
    public string PidFile => _pathResolver.PidFile;
    public string PortFile => _pathResolver.PortFile;
    public string LogFile => _pathResolver.LogFile;
    public string CompilationErrorsFile => _pathResolver.CompilationErrorsFile;
    public string OperationFile => _pathResolver.OperationFile;
    public string RefreshResultFile => _pathResolver.RefreshResultFile;
    public string EvalResultFile => _pathResolver.EvalResultFile;
    public string ExecuteResultFile => _pathResolver.ExecuteResultFile;
    public string TestRunningFile => _pathResolver.TestRunningFile;
    public string TestResultsFile => _pathResolver.TestResultsFile;
    public string GetEvalResultFile(string operationId) => _pathResolver.GetEvalResultFile(operationId);
    public string GetExecuteResultFile(string operationId) => _pathResolver.GetExecuteResultFile(operationId);
    public string GetTestResultsFile(string operationId) => _pathResolver.GetTestResultsFile(operationId);

    public void PurgeOperationState()
    {
        string[] files =
        {
            _pathResolver.OperationFile,
            _pathResolver.TestRunningFile,
            _pathResolver.PidFile,
            _pathResolver.PortFile
        };

        foreach (var file in files)
        {
            if (File.Exists(file))
            {
                try { File.Delete(file); } catch { }
            }
        }

        if (Directory.Exists(_pathResolver.TempDir))
        {
            try
            {
                var orphanedFiles = Directory.GetFiles(_pathResolver.TempDir, "unity_*_*.json");
                foreach (var orphan in orphanedFiles)
                {
                    try { File.Delete(orphan); } catch { }
                }
            }
            catch { }
        }
    }

    public UnityProcessManager(
        IUnityPathResolver pathResolver,
        ILogger<UnityProcessManager> logger,
        IUnitySocketTransport? socketTransport = null,
        IUnityLogScanner? logScanner = null,
        IUnityExecutableLocator? executableLocator = null)
    {
        _pathResolver = pathResolver ?? throw new ArgumentNullException(nameof(pathResolver));
        _logger = logger;
        _socketTransport = socketTransport ?? new UnitySocketTransport(logger);
        _logScanner = logScanner ?? new UnityLogScanner();
        _executableLocator = executableLocator ?? new UnityExecutableLocator(pathResolver, logger);
    }

    public UnityProcessManager(string projectRoot, ILogger<UnityProcessManager> logger)
        : this(new UnityPathResolver(projectRoot), logger)
    {
    }

    /// <summary>
    /// Detects Unity process liveness (checking Temp/unity_lean_mcp_process.pid, Temp/UnityLockfile, and system processes)
    /// on Windows, macOS, and Linux.
    /// </summary>
    public virtual bool IsUnityRunning(out int? processId)
    {
        processId = null;

        // 1. Check Temp/unity_lean_mcp_process.pid
        if (File.Exists(_pathResolver.PidFile))
        {
            try
            {
                string pidText = ReadFileWithRetry(_pathResolver.PidFile).Trim();
                if (int.TryParse(pidText, out int pid) && pid > 0)
                {
                    if (IsProcessAlive(pid))
                    {
                        processId = pid;
                        return true;
                    }
                }
                // PID is dead, delete stale file
                try { File.Delete(_pathResolver.PidFile); } catch { }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error reading pid file {PidFile}", _pathResolver.PidFile);
            }
        }

        // 2. Check Temp/UnityLockfile or Temp/UnityLockFile
        string lockFilePath = Path.Combine(_pathResolver.TempDir, "UnityLockfile");
        if (!File.Exists(lockFilePath))
        {
            lockFilePath = Path.Combine(_pathResolver.TempDir, "UnityLockFile");
        }

        if (File.Exists(lockFilePath))
        {
            // Try reading PID from lockfile (4-byte binary or text)
            try
            {
                var fileInfo = new FileInfo(lockFilePath);
                int lockPid = 0;
                if (fileInfo.Length == 4)
                {
                    byte[] bytes = File.ReadAllBytes(lockFilePath);
                    lockPid = BitConverter.ToInt32(bytes, 0);
                }
                else if (fileInfo.Length > 0)
                {
                    string text = ReadFileWithRetry(lockFilePath, maxRetries: 1).Trim();
                    int.TryParse(text, out lockPid);
                }

                if (lockPid > 0 && IsProcessAlive(lockPid))
                {
                    processId = lockPid;
                    return true;
                }
            }
            catch { }

            // Check if file is actively locked by an operating system handle
            bool isLocked = IsFileLocked(lockFilePath);
            if (isLocked)
            {
                // File is held open by an active process
                processId = FindProjectUnityPid(allowUnprovenSingleCandidate: true);
                return true;
            }

            // Lockfile exists but is not locked and has no live process
            try { File.Delete(lockFilePath); } catch { }
        }

        // 3. Fallback: check system processes for Unity instance targeting this project
        int? sysPid = FindProjectUnityPid(allowUnprovenSingleCandidate: false);
        if (sysPid.HasValue)
        {
            processId = sysPid;
            return true;
        }

        return false;
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var proc = Process.GetProcessById(pid);
            return !proc.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsFileLocked(string filePath)
    {
        try
        {
            using var stream = File.Open(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal Func<Process[]>? ProcessProvider { get; set; }

    internal virtual Process[] GetUnityProcesses()
    {
        if (ProcessProvider != null)
        {
            return ProcessProvider();
        }

        try
        {
            var processes = Process.GetProcessesByName("Unity");
            if (processes.Length == 0 && !RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                processes = Process.GetProcessesByName("unity-editor");
            }
            return processes;
        }
        catch
        {
            return Array.Empty<Process>();
        }
    }

    internal int? FindProjectUnityPid(Process[]? candidateProcesses = null, bool allowUnprovenSingleCandidate = false)
    {
        try
        {
            var processes = candidateProcesses ?? GetUnityProcesses();
            if (processes.Length == 0)
            {
                return null;
            }

            if (processes.Length == 1 && (allowUnprovenSingleCandidate || candidateProcesses != null))
            {
                return processes[0].Id;
            }

            // Multiple Unity processes exist (or single OS process without lockfile proof). Never fall back to returning an arbitrary process.
            // Only return a PID if it can be deterministically proven to belong to this project.
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                string normalizedProject = _pathResolver.ProjectRoot.TrimEnd('/', '\\');
                foreach (var proc in processes)
                {
                    try
                    {
                        string cmdlinePath = $"/proc/{proc.Id}/cmdline";
                        if (File.Exists(cmdlinePath))
                        {
                            string cmdline = File.ReadAllText(cmdlinePath);
                            if (cmdline.Contains(normalizedProject, StringComparison.OrdinalIgnoreCase))
                            {
                                return proc.Id;
                            }
                        }
                    }
                    catch { }
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to query system Unity processes.");
            return null;
        }
    }

    /// <summary>
    /// Locates the Unity executable for this project. Delegates to <see cref="IUnityExecutableLocator"/>.
    /// </summary>
    public string? FindUnityExecutable() => _executableLocator.FindUnityExecutable();

    public virtual string GetUnityMode(int? pid = null)
    {
        if (!pid.HasValue && !IsUnityRunning(out pid))
        {
            return "Unknown";
        }

        if (_launchedPid.HasValue && _launchedPid.Value == pid)
        {
            return "Batchmode";
        }

        if (File.Exists(_pathResolver.PidFile))
        {
            try
            {
                string pidText = ReadFileWithRetry(_pathResolver.PidFile).Trim();
                if (int.TryParse(pidText, out int filePid) && filePid == pid)
                {
                    return "Batchmode";
                }
            }
            catch { }
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && pid.HasValue)
        {
            try
            {
                string cmdlinePath = $"/proc/{pid.Value}/cmdline";
                if (File.Exists(cmdlinePath))
                {
                    string cmdline = File.ReadAllText(cmdlinePath);
                    if (cmdline.Contains("batchmode", StringComparison.OrdinalIgnoreCase))
                    {
                        return "Batchmode";
                    }
                }
            }
            catch { }
        }

        return "GUI";
    }

    public string? GetProjectEditorVersion() => _executableLocator.GetProjectEditorVersion();

    /// <summary>
    /// Auto-starts Unity in headless batchmode if not already running, and waits for socket readiness.
    /// </summary>
    public async Task EnsureUnityRunningAsync(CancellationToken cancellationToken = default)
    {
        if (IsUnityRunning(out int? existingPid))
        {
            if (await IsSocketReadyAsync(2, cancellationToken))
            {
                _logger.LogInformation("Unity is already running (PID {Pid}) and socket server is ready.", existingPid);
                return;
            }

            _logger.LogInformation("Unity is running (PID {Pid}) but socket is not ready yet. Waiting for readiness...", existingPid);
            await WaitForSocketReadinessAsync(null, cancellationToken);
            return;
        }

        string? unityExe = _executableLocator.FindUnityExecutable();
        if (string.IsNullOrWhiteSpace(unityExe))
        {
            string? version = _executableLocator.GetProjectEditorVersion();
            throw new FileNotFoundException(
                $"Unity executable not found for project at '{_pathResolver.ProjectRoot}' (version: {version ?? "unknown"}). " +
                "Set the UNITY_PATH or UNITY_EDITOR environment variable or install Unity via Unity Hub.");
        }

        _logger.LogInformation("Auto-starting Unity batchmode from '{UnityExe}'...", unityExe);

        Directory.CreateDirectory(_pathResolver.TempDir);
        try { File.Delete(_pathResolver.LogFile); } catch { }
        try { File.Delete(_pathResolver.PidFile); } catch { }
        try { File.Delete(_pathResolver.CompilationErrorsFile); } catch { }

        long initialLogOffset = 0;
        if (File.Exists(_pathResolver.LogFile))
        {
            try
            {
                initialLogOffset = new FileInfo(_pathResolver.LogFile).Length;
            }
            catch
            {
                initialLogOffset = 0;
            }
        }

        var psi = new ProcessStartInfo
        {
            FileName = unityExe,
            Arguments = $"-batchmode -nographics -projectPath \"{_pathResolver.ProjectRoot}\" -logFile \"{_pathResolver.LogFile}\"",
            WorkingDirectory = _pathResolver.ProjectRoot,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        Process proc;
        try
        {
            proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to launch Unity process.");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to launch Unity process at '{unityExe}': {ex.Message}", ex);
        }

        _launchedPid = proc.Id;
        try
        {
            File.WriteAllText(_pathResolver.PidFile, proc.Id.ToString());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write PID to {PidFile}", _pathResolver.PidFile);
        }

        _logger.LogInformation("Unity process started with PID {Pid}. Waiting for socket server...", proc.Id);
        await WaitForSocketReadinessAsync(proc, cancellationToken, initialLogOffset);
    }

    public virtual async Task<bool> StartUnityAsync(CancellationToken cancellationToken = default)
    {
        await EnsureUnityRunningAsync(cancellationToken);
        return IsUnityRunning(out _);
    }

    public virtual async Task<bool> WaitForHealthyAsync(CancellationToken cancellationToken = default)
    {
        return await IsSocketReadyAsync(2, cancellationToken);
    }

    internal async Task WaitForSocketReadinessAsync(Process? startedProcess, CancellationToken cancellationToken, long initialLogOffset = 0)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 1. Check if the process exited unexpectedly (deterministic check)
            if (startedProcess != null)
            {
                if (startedProcess.HasExited)
                {
                    if (File.Exists(_pathResolver.LogFile))
                    {
                        string logText = ReadFileWithRetry(_pathResolver.LogFile, fromOffset: initialLogOffset);
                        if (_logScanner.HasCompilationErrors(logText))
                        {
                            var errorLines = _logScanner.ExtractUniqueCompilationLines(logText);
                            try
                            {
                                Directory.CreateDirectory(_pathResolver.TempDir);
                                File.WriteAllLines(_pathResolver.CompilationErrorsFile, errorLines);
                            }
                            catch { }
                            throw new UnityCompilationException(
                                string.Join(Environment.NewLine, errorLines),
                                errorLines);
                        }
                    }

                    string logSnippet = GetLogSnippet(initialLogOffset);
                    throw new InvalidOperationException(
                        $"Unity background process exited unexpectedly with exit code {startedProcess.ExitCode}.\n{logSnippet}");
                }
            }
            else
            {
                if (!IsUnityRunning(out _))
                {
                    if (File.Exists(_pathResolver.LogFile))
                    {
                        string logText = ReadFileWithRetry(_pathResolver.LogFile, fromOffset: initialLogOffset);
                        if (_logScanner.HasCompilationErrors(logText))
                        {
                            var errorLines = _logScanner.ExtractUniqueCompilationLines(logText);
                            try
                            {
                                Directory.CreateDirectory(_pathResolver.TempDir);
                                File.WriteAllLines(_pathResolver.CompilationErrorsFile, errorLines);
                            }
                            catch { }
                            throw new UnityCompilationException(
                                string.Join(Environment.NewLine, errorLines),
                                errorLines);
                        }
                    }

                    string logSnippet = GetLogSnippet(initialLogOffset);
                    throw new InvalidOperationException(
                        $"Unity background process is not running.\n{logSnippet}");
                }
            }

            // 2. Monitor unity_background_log.txt for compilation errors during startup
            if (startedProcess != null && File.Exists(_pathResolver.LogFile))
            {
                string logText = ReadFileWithRetry(_pathResolver.LogFile, fromOffset: initialLogOffset);
                if (_logScanner.HasCompilationErrors(logText))
                {
                    _logger.LogError("Compilation errors detected in Unity background log during startup.");
                    var errorLines = _logScanner.ExtractUniqueCompilationLines(logText);
                    try
                    {
                        Directory.CreateDirectory(_pathResolver.TempDir);
                        File.WriteAllLines(_pathResolver.CompilationErrorsFile, errorLines);
                    }
                    catch { }

                    if (!startedProcess.HasExited)
                    {
                        try
                        {
                            startedProcess.Kill(true);
                            startedProcess.WaitForExit();
                        }
                        catch { }
                    }

                    throw new UnityCompilationException(
                        string.Join(Environment.NewLine, errorLines),
                        errorLines);
                }
            }

            // 3. Check socket connection
            if (File.Exists(_pathResolver.PortFile))
            {
                if (await IsSocketReadyAsync(2, cancellationToken))
                {
                    // Check if server is settled
                    string? refreshState = await ProbeSocketCommandAsync("POLL_REFRESH", 2, cancellationToken);
                    if (refreshState == "READY" || refreshState == "COMPILATION_ERROR")
                    {
                        _logger.LogInformation("Unity socket server is ready (state: {State}).", refreshState);
                        return;
                    }
                }
            }

            await Task.Delay(1000, cancellationToken);
        }
    }

    public virtual async Task<bool> IsSocketReadyAsync(int timeoutSeconds = 2, CancellationToken cancellationToken = default)
    {
        int port = ReadPortFile();
        return await _socketTransport.IsSocketReadyAsync(port, timeoutSeconds, cancellationToken);
    }

    public virtual async Task<string?> ProbeSocketCommandAsync(string command, int timeoutSeconds = 2, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_pathResolver.PortFile))
        {
            return null;
        }

        int port = ReadPortFile();
        if (port <= 0 || port > 65535)
        {
            return null;
        }

        return await _socketTransport.SendCommandAsync(port, command, timeoutSeconds, cancellationToken);
    }

    public int ReadPortFile()
    {
        if (!File.Exists(_pathResolver.PortFile)) return 0;
        try
        {
            string content = ReadFileWithRetry(_pathResolver.PortFile).Trim();
            return int.TryParse(content, out int port) ? port : 0;
        }
        catch
        {
            return 0;
        }
    }

    private string GetLogSnippet(long initialOffset = 0) =>
        _logScanner.GetLogSnippet(_pathResolver.LogFile, initialOffset);

    internal static List<string> ExtractUniqueCompilationLines(string logText) =>
        new UnityLogScanner().ExtractUniqueCompilationLines(logText);

    public static string ReadFileWithRetry(string path, int maxRetries = 5, int delayMs = 100, long fromOffset = 0)
    {
        for (int i = 0; i < maxRetries; i++)
        {
            try
            {
                return ReadFileFromOffset(path, fromOffset);
            }
            catch (IOException) when (i < maxRetries - 1)
            {
                Thread.Sleep(delayMs);
            }
        }
        return ReadFileFromOffset(path, fromOffset);
    }

    private static string ReadFileFromOffset(string path, long fromOffset)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (fromOffset > 0)
        {
            if (fs.Length < fromOffset)
            {
                fs.Seek(0, SeekOrigin.Begin);
            }
            else
            {
                fs.Seek(fromOffset, SeekOrigin.Begin);
            }
        }
        using var reader = new StreamReader(fs, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    public virtual async Task<bool> StopUnityAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!IsUnityRunning(out int? pid))
            {
                PurgeOperationState();
                return true;
            }

            string mode = GetUnityMode(pid);
            if (mode == "GUI" && !force)
            {
                _logger.LogWarning("Refusing to stop Unity GUI Editor with PID {Pid} without force flag.", pid);
                return false;
            }

            int? targetPid = pid;

            // Try sending EXIT to socket first
            try
            {
                await ProbeSocketCommandAsync("EXIT", 2, cancellationToken);
            }
            catch { }

            // Wait up to 5 seconds for process to exit
            for (int i = 0; i < 25; i++)
            {
                if (!IsUnityRunning(out int? currentPid))
                {
                    PurgeOperationState();
                    return true;
                }

                if (!targetPid.HasValue && currentPid.HasValue)
                {
                    targetPid = currentPid;
                }

                await Task.Delay(200, cancellationToken);
            }

            // Force kill if still running and target PID is deterministically known
            if (targetPid.HasValue && targetPid.Value > 0)
            {
                try
                {
                    var proc = Process.GetProcessById(targetPid.Value);
                    proc.Kill(true);
                    proc.WaitForExit();
                }
                catch { }
            }

            PurgeOperationState();
            return !IsUnityRunning(out _);
        }
        finally
        {
            PurgeOperationState();
        }
    }

    public Task<bool> StopUnityAsync(CancellationToken cancellationToken) => StopUnityAsync(false, cancellationToken);
}
