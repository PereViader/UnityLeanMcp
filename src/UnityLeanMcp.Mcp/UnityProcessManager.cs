using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
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
    private readonly IUnityProcessIdentityStore _processIdentityStore;

    // This delegate is intentionally internal: it keeps process-launch behavior
    // deterministic in lifecycle tests without adding process concerns to the
    // public manager contract.
    internal Func<ProcessStartInfo, Process?>? ProcessStarter { get; init; }

    public IUnityPathResolver PathResolver => _pathResolver;
    public IUnityExecutableLocator ExecutableLocator => _executableLocator;

    public void PurgeOperationState()
    {
        if (IsUnityRunning(out int? runningPid))
        {
            _logger.LogDebug("Preserving operation state because Unity is still running (PID {Pid}).", runningPid);
            return;
        }

        string[] files =
        {
            _pathResolver.OperationFile,
            _pathResolver.TestRunningFile,
            _pathResolver.PidFile,
            GetPidIdentityFilePath(),
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
                var sharedResultFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    Path.GetFullPath(_pathResolver.GetResultFilePath(UnityOperationKind.Refresh)),
                    Path.GetFullPath(_pathResolver.GetResultFilePath(UnityOperationKind.Recompile)),
                    Path.GetFullPath(_pathResolver.GetResultFilePath(UnityOperationKind.Test)),
                    Path.GetFullPath(_pathResolver.GetResultFilePath(UnityOperationKind.Eval))
                };

                var orphanedFiles = Directory.GetFiles(_pathResolver.TempDir, "unity_*_*.json");
                foreach (var orphan in orphanedFiles)
                {
                    // Operation-scoped results are disposable after confirmed Unity
                    // termination. Shared static results are Editor history/state and
                    // must survive cleanup (for example unity_refresh_result.json).
                    if (sharedResultFiles.Contains(Path.GetFullPath(orphan)))
                    {
                        continue;
                    }

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
        IUnityExecutableLocator? executableLocator = null,
        Func<Process[]>? processProvider = null,
        Func<ProcessStartInfo, Process?>? processStarter = null)
        : this(pathResolver, logger, socketTransport, logScanner, executableLocator, null, processProvider, processStarter)
    {
    }

    internal UnityProcessManager(
        IUnityPathResolver pathResolver,
        ILogger<UnityProcessManager> logger,
        IUnitySocketTransport? socketTransport,
        IUnityLogScanner? logScanner,
        IUnityExecutableLocator? executableLocator,
        IUnityProcessIdentityStore? processIdentityStore,
        Func<Process[]>? processProvider = null,
        Func<ProcessStartInfo, Process?>? processStarter = null)
    {
        _pathResolver = pathResolver ?? throw new ArgumentNullException(nameof(pathResolver));
        _logger = logger;
        _socketTransport = socketTransport ?? new UnitySocketTransport(logger);
        _logScanner = logScanner ?? new UnityLogScanner();
        _executableLocator = executableLocator ?? new UnityExecutableLocator(pathResolver, logger);
        _processIdentityStore = processIdentityStore ?? new FileUnityProcessIdentityStore(pathResolver.PidFile);
        ProcessProvider = processProvider;
        ProcessStarter = processStarter;
    }

    public UnityProcessManager(
        string projectRoot,
        ILogger<UnityProcessManager> logger,
        Func<Process[]>? processProvider = null,
        Func<ProcessStartInfo, Process?>? processStarter = null)
        : this(new UnityPathResolver(projectRoot), logger, processProvider: processProvider, processStarter: processStarter)
    {
    }

    /// <summary>
    /// Detects Unity process liveness (checking Temp/unity_lean_mcp_process.pid, Temp/UnityLockfile, and system processes)
    /// on Windows, macOS, and Linux.
    /// </summary>
    public virtual bool IsUnityRunning(out int? processId)
    {
        processId = null;

        // 1. Check the durable PID/identity pair. The identity sidecar is
        // also checked when the PID file is missing so a crash between the
        // two atomic publications can still recover an already-launched
        // Editor instead of starting a duplicate.
        string pidIdentityFile = GetPidIdentityFilePath();
        if (_processIdentityStore.TryRead(out var identity) && identity.ProcessId > 0)
        {
            if (IsOwnedPid(identity.ProcessId))
            {
                processId = identity.ProcessId;
                return true;
            }

            try { File.Delete(_pathResolver.PidFile); } catch { }
            try { File.Delete(pidIdentityFile); } catch { }
        }
        else if (File.Exists(_pathResolver.PidFile))
        {
            try
            {
                string pidText = ReadFileWithRetry(_pathResolver.PidFile).Trim();
                if (int.TryParse(pidText, out int rawPid) && rawPid > 0 && IsOwnedPid(rawPid))
                {
                    processId = rawPid;
                    return true;
                }

                try { File.Delete(_pathResolver.PidFile); } catch { }
                try { File.Delete(pidIdentityFile); } catch { }
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

                if (lockPid > 0)
                {
                    if (IsOwnedPid(lockPid))
                    {
                        processId = lockPid;
                        return true;
                    }

                    // For GUI instances (which do not have a .identity.json record),
                    // verify the candidate process is alive and is a Unity instance.
                    try
                    {
                        using var proc = Process.GetProcessById(lockPid);
                        if (!proc.HasExited && (proc.ProcessName.Contains("Unity", StringComparison.OrdinalIgnoreCase) ||
                                                proc.ProcessName.Contains("unity-editor", StringComparison.OrdinalIgnoreCase)))
                        {
                            if (TryGetProcessCommandLine(proc, out string commandLine))
                            {
                                if (CommandLineTargetsProject(commandLine, proc.Id))
                                {
                                    processId = lockPid;
                                    return true;
                                }
                            }
                            else if (IsFileLocked(lockFilePath) || RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                            {
                                processId = lockPid;
                                return true;
                            }
                        }
                    }
                    catch { }
                }
            }
            catch { }

            // Check if file is actively locked by an operating system handle
            bool isLocked = IsFileLocked(lockFilePath);
            if (isLocked)
            {
                int? projectPid = FindProjectUnityPid();
                if (projectPid.HasValue)
                {
                    processId = projectPid.Value;
                    return true;
                }

                // A lock held in this project's Temp directory is enough to
                // establish that starting another Editor would be unsafe, even
                // when this host cannot inspect the lock holder's command line.
                // This is intentionally not process ownership proof: callers
                // receive no PID and therefore cannot use it to terminate an
                // Editor. It only makes startup wait for the existing Editor to
                // republish its socket (or release the lock) instead of racing it
                // with a conflicting batchmode launch.
                _logger.LogInformation(
                    "Unity project lockfile {LockFile} is held but its process cannot be attributed; waiting for the existing Editor socket.",
                    lockFilePath);
                return true;
            }
        }

        // 3. Fallback: check system processes for Unity instance targeting this project
        int? sysPid = FindProjectUnityPid();
        if (sysPid.HasValue)
        {
            processId = sysPid;
            return true;
        }

        return false;
    }

    private string GetPidIdentityFilePath() => _processIdentityStore.FilePath;

    private bool IsOwnedPid(int pid)
    {
        // ProcessProvider is a deliberately trusted test seam. Production
        // discovery never uses it and must validate the durable identity below.
        if (ProcessProvider != null)
        {
            try
            {
                foreach (var candidate in ProcessProvider())
                {
                    if (candidate.Id == pid && !candidate.HasExited)
                    {
                        return true;
                    }
                }
            }
            catch { }

            return false;
        }

        if (!_processIdentityStore.TryRead(out var identity) || identity.ProcessId != pid)
        {
            return false;
        }

        try
        {
            using var proc = Process.GetProcessById(pid);
            return _processIdentityStore.Matches(proc, identity, _pathResolver.ProjectRoot);
        }
        catch
        {
            return false;
        }
    }

    private bool TryGetOwnedProcessForTermination(int pid, out Process? process)
    {
        // A fallback kill is safe only when the durable sidecar can identify
        // the exact process. In particular, a PID discovered from a lockfile
        // or an injected provider is not sufficient for termination.
        return _processIdentityStore.TryGetOwnedProcess(pid, _pathResolver.ProjectRoot, out process);
    }

    private sealed class StartupClaim
    {
        public string ClaimId { get; init; } = string.Empty;
        public int ProcessId { get; init; }
        public long StartTimeUtcTicks { get; init; }
        public string ExecutablePath { get; init; } = string.Empty;
        public string ProjectRoot { get; init; } = string.Empty;
    }

    private static void WriteTextAtomically(string path, string content)
    {
        string? directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new ArgumentException("The target path must include a directory.", nameof(path));
        }

        Directory.CreateDirectory(directory);
        string temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                options: FileOptions.SequentialScan))
            {
                byte[] bytes = Encoding.UTF8.GetBytes(content);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }

            // Startup files are removed before a new launch. A non-overwriting
            // move prevents replacing an identity another host may have
            // published concurrently.
            File.Move(temporaryPath, path);
        }
        finally
        {
            try { File.Delete(temporaryPath); } catch { }
        }
    }

    private async Task<FileStream> AcquireStartupLockAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_pathResolver.TempDir);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileStream stream;
            try
            {
                stream = new FileStream(
                    _pathResolver.StartupLockFile,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 4096,
                    options: FileOptions.SequentialScan);
            }
            catch (IOException) when (File.Exists(_pathResolver.StartupLockFile))
            {
                // FileShare.None is the inter-process claim. A persistent
                // inode is intentionally retained: on POSIX, unlinking an
                // open lock file would let a waiter create a second inode.
                await Task.Delay(100, cancellationToken);
                continue;
            }
            catch (UnauthorizedAccessException) when (File.Exists(_pathResolver.StartupLockFile))
            {
                // Windows can surface a sharing violation as access denied.
                // Keep waiting for the owning handle to close; cancellation
                // remains the only bounded exit from this wait.
                await Task.Delay(100, cancellationToken);
                continue;
            }

            try
            {
                WriteStartupClaim(stream);
                return stream;
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }
    }

    private void WriteStartupClaim(FileStream stream)
    {
        using Process currentProcess = Process.GetCurrentProcess();
        FileUnityProcessIdentityStore.TryGetProcessStartTimeTicks(currentProcess, out long startTimeTicks);

        var claim = new StartupClaim
        {
            ClaimId = Guid.NewGuid().ToString("N"),
            ProcessId = currentProcess.Id,
            StartTimeUtcTicks = startTimeTicks,
            ExecutablePath = FileUnityProcessIdentityStore.TryGetProcessPath(currentProcess) ?? string.Empty,
            ProjectRoot = _pathResolver.ProjectRoot
        };

        byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(claim));
        stream.SetLength(0);
        stream.Position = 0;
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush(flushToDisk: true);
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
        catch (UnauthorizedAccessException)
        {
            // Windows can report an existing FileShare.None claim as access
            // denied. Preserve the lockfile rather than treating an
            // unprobeable file as stale and deleting it.
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal Func<Process[]>? ProcessProvider { get; init; }

    // This delegate is intentionally internal: it keeps process-discovery
    // tests deterministic without making command-line inspection part of the
    // public process-manager contract.
    internal Func<Process, string?>? ProcessCommandLineProvider { get; set; }

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

    internal int? FindProjectUnityPid(Process[]? candidateProcesses = null)
    {
        bool ownsProcesses = candidateProcesses == null && ProcessProvider == null;
        var processes = candidateProcesses ?? GetUnityProcesses();
        try
        {
            if (processes.Length == 0)
            {
                return null;
            }

            // A single candidate is not ownership proof. Accept only a
            // matching durable identity or a command line that explicitly
            // targets this project, and reject an ambiguous set of proven
            // candidates rather than returning an arbitrary PID.
            int? provenPid = null;
            if (_processIdentityStore.TryRead(out var identity))
            {
                foreach (var proc in processes)
                {
                    try
                    {
                        if (_processIdentityStore.Matches(proc, identity, _pathResolver.ProjectRoot))
                        {
                            if (provenPid.HasValue)
                            {
                                return null;
                            }

                            provenPid = proc.Id;
                        }
                    }
                    catch { }
                }
            }

            foreach (var proc in processes)
            {
                try
                {
                    if (!TryGetProcessCommandLine(proc, out string commandLine) ||
                        !CommandLineTargetsProject(commandLine, proc.Id))
                    {
                        continue;
                    }

                    if (provenPid.HasValue && provenPid.Value != proc.Id)
                    {
                        return null;
                    }

                    provenPid = proc.Id;
                }
                catch { }
            }

            return provenPid;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to query system Unity processes.");
            return null;
        }
        finally
        {
            if (ownsProcesses && processes != null)
            {
                foreach (var p in processes)
                {
                    try { p.Dispose(); } catch { }
                }
            }
        }
    }

    private bool TryGetProcessCommandLine(Process process, out string commandLine)
    {
        commandLine = string.Empty;
        if (ProcessCommandLineProvider != null)
        {
            try
            {
                commandLine = ProcessCommandLineProvider(process) ?? string.Empty;
                return !string.IsNullOrEmpty(commandLine);
            }
            catch
            {
                return false;
            }
        }

        return UnityProcessCommandLineReader.TryRead(process.Id, out commandLine);
    }

    internal bool CommandLineTargetsProject(string commandLine, int? processId = null)
    {
        string projectRoot = _pathResolver.ProjectRoot
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Replace('\\', '/');
        string normalizedCommandLine = commandLine.Replace('\\', '/');

        int searchStart = 0;
        while (searchStart < normalizedCommandLine.Length)
        {
            int matchIndex = normalizedCommandLine.IndexOf(projectRoot, searchStart, StringComparison.OrdinalIgnoreCase);
            if (matchIndex < 0)
            {
                break;
            }

            int matchEnd = matchIndex + projectRoot.Length;
            bool leftBoundary = matchIndex == 0 || IsCommandLineBoundary(normalizedCommandLine[matchIndex - 1]);
            bool rightBoundary = matchEnd == normalizedCommandLine.Length || IsCommandLineBoundary(normalizedCommandLine[matchEnd]);
            if (leftBoundary && rightBoundary)
            {
                return true;
            }

            searchStart = matchEnd;
        }

        // Support relative -projectPath arguments (e.g. -projectPath . or -projectPath ./)
        if (TryExtractProjectPathArgument(commandLine, out string? projectPathArg) && !string.IsNullOrWhiteSpace(projectPathArg))
        {
            if (FileUnityProcessIdentityStore.PathsEqual(projectPathArg, _pathResolver.ProjectRoot))
            {
                return true;
            }

            if (processId.HasValue && RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                string? procCwd = TryGetLinuxProcessCwd(processId.Value);
                if (!string.IsNullOrWhiteSpace(procCwd))
                {
                    try
                    {
                        string resolved = Path.GetFullPath(Path.Combine(procCwd, projectPathArg));
                        if (FileUnityProcessIdentityStore.PathsEqual(resolved, _pathResolver.ProjectRoot))
                        {
                            return true;
                        }
                    }
                    catch { }
                }
            }
        }

        return false;
    }

    internal static bool TryExtractProjectPathArgument(string commandLine, out string? projectPath)
    {
        projectPath = null;
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return false;
        }

        char[] separators = commandLine.Contains('\0') ? new[] { '\0' } : new[] { ' ', '\t' };
        string[] parts = commandLine.Split(separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        for (int i = 0; i < parts.Length; i++)
        {
            string part = parts[i].Trim('"', '\'');
            if (part.Equals("-projectPath", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < parts.Length)
                {
                    projectPath = parts[i + 1].Trim('"', '\'');
                    return !string.IsNullOrWhiteSpace(projectPath);
                }
            }
            else if (part.StartsWith("-projectPath=", StringComparison.OrdinalIgnoreCase))
            {
                projectPath = part.Substring("-projectPath=".Length).Trim('"', '\'');
                return !string.IsNullOrWhiteSpace(projectPath);
            }
        }

        return false;
    }

    private static string? TryGetLinuxProcessCwd(int pid)
    {
        try
        {
            string cwdLink = $"/proc/{pid}/cwd";
            if (Directory.Exists(cwdLink))
            {
                var target = File.ResolveLinkTarget(cwdLink, returnFinalTarget: true);
                return target?.FullName ?? cwdLink;
            }
        }
        catch { }

        return null;
    }

    private static bool IsCommandLineBoundary(char value) =>
        char.IsWhiteSpace(value) || value == '\0' || value == '"' || value == '=';

    /// <summary>
    /// Locates the Unity executable for this project. Delegates to <see cref="IUnityExecutableLocator"/>.
    /// </summary>
    public string? FindUnityExecutable() => _executableLocator.FindUnityExecutable().ExecutablePath;

    public virtual string GetUnityMode(int? pid = null)
    {
        if (!pid.HasValue && !IsUnityRunning(out pid))
        {
            return "Unknown";
        }

        if (pid.HasValue && File.Exists(_pathResolver.PidFile) && IsOwnedPid(pid.Value))
        {
            return "Batchmode";
        }

        if (pid.HasValue)
        {
            try
            {
                using var process = Process.GetProcessById(pid.Value);
                if (TryGetProcessCommandLine(process, out string commandLine) &&
                    commandLine.Contains("batchmode", StringComparison.OrdinalIgnoreCase))
                {
                    return "Batchmode";
                }
            }
            catch { }
        }

        return "GUI";
    }

    public string? GetProjectEditorVersion() => (_executableLocator as UnityExecutableLocator)?.GetProjectEditorVersion();

    /// <summary>
    /// Ensures the project has a live Unity socket, auto-starting Unity in headless batchmode only when
    /// no project-scoped endpoint responds to PING and process discovery finds no existing Editor.
    /// </summary>
    public async Task EnsureUnityRunningAsync(CancellationToken cancellationToken = default)
    {
        // The port file is written by the Unity package under this project's Temp
        // directory. A successful PING is stronger evidence than lockfile shape or
        // OS command-line inspection: GUI Editors commonly have zero-byte lockfiles
        // and their command lines are not inspectable on every platform. Do not use
        // the port file by itself; IsSocketReadyAsync validates it with PING/PONG so
        // an orphaned or reused port cannot suppress startup.
        if (await IsSocketReadyAsync(2, cancellationToken))
        {
            _logger.LogInformation("Unity socket server for this project is already ready.");
            return;
        }

        if (IsUnityRunning(out int? existingPid))
        {
            _logger.LogInformation("Unity is running (PID {Pid}) but socket is not ready yet. Waiting for readiness...", existingPid);
            await WaitForSocketReadinessAsync(null, cancellationToken);
            return;
        }

        // The claim is a real OS-level file handle, so separate MCP hosts
        // coordinate through the same project directory. Keep it until the
        // launched Editor is ready (or startup fails) so a second host cannot
        // launch while the first one is still publishing its identity.
        using FileStream startupLock = await AcquireStartupLockAsync(cancellationToken);
        // Another caller may have completed startup while this caller was waiting
        // for the inter-process claim. Repeat the endpoint check before process
        // discovery so a newly ready GUI Editor is never mistaken for absent.
        if (await IsSocketReadyAsync(2, cancellationToken))
        {
            _logger.LogInformation("Unity socket server for this project became ready while waiting for startup ownership.");
            return;
        }

        // Re-check process ownership before resolving or launching another Editor
        // process. Process discovery remains the fallback when the project port is
        // absent, stale, or does not answer PING.
        if (IsUnityRunning(out existingPid))
        {
            _logger.LogInformation("Unity is running (PID {Pid}) but socket is not ready yet. Waiting for readiness...", existingPid);
            await WaitForSocketReadinessAsync(null, cancellationToken);
            return;
        }

        UnityLocatorResult locatorResult = _executableLocator.FindUnityExecutable();
        if (!locatorResult.Success)
        {
            string? version = GetProjectEditorVersion();
            string diagnostic = locatorResult.Diagnostic ??
                                "No candidate matched a Unity Editor installation layout.";
            throw new FileNotFoundException(
                $"Unity executable not found for project at '{_pathResolver.ProjectRoot}' (version: {version ?? "unknown"}). " +
                $"{diagnostic} " +
                "Set the UNITY_PATH or UNITY_EDITOR environment variable or install Unity via Unity Hub.");
        }

        string unityExe = locatorResult.ExecutablePath!;

        _logger.LogInformation("Auto-starting Unity batchmode from '{UnityExe}'...", unityExe);

        Directory.CreateDirectory(_pathResolver.TempDir);
        try { File.Delete(_pathResolver.LogFile); } catch { }
        try { File.Delete(_pathResolver.PidFile); } catch { }
        try { File.Delete(GetPidIdentityFilePath()); } catch { }
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

        string projectRootArg = _pathResolver.ProjectRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string logFileArg = _pathResolver.LogFile.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        var psi = new ProcessStartInfo
        {
            FileName = unityExe,
            Arguments = $"-batchmode -nographics -projectPath \"{projectRootArg}\" -logFile \"{logFileArg}\"",
            WorkingDirectory = projectRootArg,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        Process proc;
        try
        {
            proc = (ProcessStarter ?? Process.Start)(psi) ?? throw new InvalidOperationException("Failed to launch Unity process.");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to launch Unity process at '{unityExe}': {ex.Message}", ex);
        }

        using (proc)
        {
            try
            {
                // Publish the identity before the PID pointer, and write both
                // files atomically. If the host crashes between those
                // publications, IsUnityRunning can recover from the sidecar
                // alone; a reused PID cannot satisfy its identity.
                _processIdentityStore.Write(proc, unityExe, _pathResolver.ProjectRoot);
                WriteTextAtomically(_pathResolver.PidFile, proc.Id.ToString());
            }
            catch (Exception ex)
            {
                try { File.Delete(_pathResolver.PidFile); } catch { }
                try { File.Delete(GetPidIdentityFilePath()); } catch { }
                try
                {
                    if (!proc.HasExited)
                    {
                        proc.Kill(entireProcessTree: true);
                        proc.WaitForExit();
                    }
                }
                catch (Exception cleanupException)
                {
                    _logger.LogWarning(cleanupException, "Failed to clean up Unity after startup ownership publication failed.");
                }

                throw new InvalidOperationException(
                    $"Failed to publish startup ownership for Unity process {proc.Id}.", ex);
            }

            _logger.LogInformation("Unity process started with PID {Pid}. Waiting for socket server...", proc.Id);
            await WaitForSocketReadinessAsync(proc, cancellationToken, initialLogOffset);
        }
    }

    public virtual async Task<bool> StartUnityAsync(CancellationToken cancellationToken = default)
    {
        await EnsureUnityRunningAsync(cancellationToken);
        return await IsSocketReadyAsync(2, cancellationToken);
    }

    public virtual async Task<bool> WaitForHealthyAsync(CancellationToken cancellationToken = default)
    {
        return await IsSocketReadyAsync(2, cancellationToken);
    }

    internal virtual async Task WaitForSocketReadinessAsync(Process? startedProcess, CancellationToken cancellationToken, long initialLogOffset = 0)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            bool socketIsLive = false;

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
                // An interactive GUI Editor can be live and serving this project's
                // socket even when platform process inspection cannot establish an
                // owned PID. PING/PONG is authoritative for endpoint readiness;
                // only treat Unity as absent if neither source has positive proof.
                socketIsLive = await IsSocketReadyAsync(2, cancellationToken);
                if (!socketIsLive && !IsUnityRunning(out _))
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
                if (socketIsLive || await IsSocketReadyAsync(2, cancellationToken))
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
        return ReadFileWithRetry(path, maxRetries, delayMs, fromOffset, ReadFileFromOffset);
    }

    internal static string ReadFileWithRetry(string path, int maxRetries, int delayMs, long fromOffset, Func<string, long, string> reader)
    {
        for (int i = 0; i < maxRetries; i++)
        {
            try
            {
                return reader(path, fromOffset);
            }
            catch (IOException) when (i < maxRetries - 1)
            {
                Thread.Sleep(delayMs);
            }
            catch (UnauthorizedAccessException) when (i < maxRetries - 1)
            {
                Thread.Sleep(delayMs);
            }
        }
        return reader(path, fromOffset);
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

    private async Task WaitForUnityExitAsync(CancellationToken cancellationToken)
    {
        while (IsUnityRunning(out _))
        {
            // This is an unbounded liveness poll, not a stop timeout. The
            // operation remains active until Unity exits or cancellation is
            // explicitly requested by the caller.
            await Task.Delay(200, cancellationToken);
        }
    }

    private bool TryTerminateOwnedProcess(int pid)
    {
        if (!TryGetOwnedProcessForTermination(pid, out Process? process) || process == null)
        {
            _logger.LogWarning(
                "Refusing fallback Unity termination for PID {Pid}: the recorded process identity no longer matches.",
                pid);
            return false;
        }

        using (process)
        {
            try
            {
                // The identity was checked while this process handle was
                // acquired. Killing through this handle avoids resolving the
                // numeric PID again after validation and therefore avoids PID
                // reuse terminating an unrelated process.
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit();
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Fallback Unity termination failed for PID {Pid}.", pid);
                return false;
            }
        }
    }

    public virtual async Task<bool> StopUnityAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        if (!IsUnityRunning(out int? pid))
        {
            // A live endpoint without a process identity can belong to an
            // interactive GUI Editor whose command line is unavailable to this
            // host. Do not erase its project state or claim it stopped when we
            // cannot safely identify a process to terminate.
            if (await IsSocketReadyAsync(2, cancellationToken))
            {
                _logger.LogWarning(
                    "Refusing to stop a live Unity socket without verified process ownership for project {ProjectRoot}.",
                    _pathResolver.ProjectRoot);
                return false;
            }

            // Unity is confirmed absent, so stale operation-scoped state can be
            // recovered. Do not perform this cleanup while a process may remain.
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
        string? exitResponse = null;

        // Try sending EXIT to socket first
        try
        {
            exitResponse = await ProbeSocketCommandAsync("EXIT", 2, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Graceful Unity shutdown request failed.");
        }

        // UnitySocketTransport intentionally reports transport failures as a
        // null response. Treat that as an explicit failure of the graceful
        // request, not as a reason to wait for an arbitrary deadline.
        cancellationToken.ThrowIfCancellationRequested();
        if (exitResponse == "EXITING")
        {
            await WaitForUnityExitAsync(cancellationToken);
            PurgeOperationState();
            return true;
        }

        // Re-check before fallback: Unity may have exited while the socket
        // request was in flight. If it is still running, only a PID backed by
        // a matching durable identity may be terminated.
        if (!IsUnityRunning(out int? currentPid))
        {
            PurgeOperationState();
            return true;
        }

        targetPid ??= currentPid;
        if (!targetPid.HasValue || targetPid.Value <= 0 || !TryTerminateOwnedProcess(targetPid.Value))
        {
            // A false result means Unity may still own and mutate the
            // operation state. Leave all state intact so callers can recover
            // or continue polling.
            return false;
        }

        await WaitForUnityExitAsync(cancellationToken);
        PurgeOperationState();
        return true;
    }

    public Task<bool> StopUnityAsync(CancellationToken cancellationToken) => StopUnityAsync(false, cancellationToken);
}
