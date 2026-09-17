using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace UnityLeanMcp.Mcp;

internal interface IUnityProcessIdentityStore
{
    string FilePath { get; }

    bool TryRead(out UnityProcessIdentityRecord identity);

    void Write(Process process, string executablePath, string projectRoot);

    bool Matches(Process process, UnityProcessIdentityRecord identity, string projectRoot);

    bool TryGetOwnedProcess(int processId, string projectRoot, out Process? process);
}

internal sealed class UnityProcessIdentityRecord
{
    public int ProcessId { get; init; }
    public long StartTimeUtcTicks { get; init; }
    public string ExecutablePath { get; init; } = string.Empty;
    public string ProjectRoot { get; init; } = string.Empty;
}

internal sealed class FileUnityProcessIdentityStore : IUnityProcessIdentityStore
{
    private const int ReadRetries = 3;
    private const int ReadRetryDelayMilliseconds = 20;

    public string FilePath { get; }

    public FileUnityProcessIdentityStore(string pidFilePath)
    {
        if (string.IsNullOrWhiteSpace(pidFilePath))
        {
            throw new ArgumentException("The PID file path is required.", nameof(pidFilePath));
        }

        FilePath = pidFilePath + ".identity.json";
    }

    public bool TryRead(out UnityProcessIdentityRecord identity)
    {
        identity = new UnityProcessIdentityRecord();
        try
        {
            string json = ReadFileWithRetry(FilePath);
            identity = JsonSerializer.Deserialize<UnityProcessIdentityRecord>(json) ??
                       new UnityProcessIdentityRecord();
            return identity.ProcessId > 0 && identity.StartTimeUtcTicks > 0;
        }
        catch
        {
            return false;
        }
    }

    public void Write(Process process, string executablePath, string projectRoot)
    {
        if (!TryGetProcessStartTimeTicks(process, out long startTimeTicks))
        {
            throw new InvalidOperationException(
                $"Could not read the start time for Unity process {process.Id}; durable startup ownership cannot be recorded.");
        }

        string fullExecutablePath = Path.GetFullPath(executablePath);
        string resolvedExecutablePath = TryResolveFinalTarget(fullExecutablePath) ?? fullExecutablePath;
        var identity = new UnityProcessIdentityRecord
        {
            ProcessId = process.Id,
            StartTimeUtcTicks = startTimeTicks,
            ExecutablePath = resolvedExecutablePath,
            ProjectRoot = projectRoot
        };

        try
        {
            WriteTextAtomically(FilePath, JsonSerializer.Serialize(identity));
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to write PID identity to '{FilePath}'.", ex);
        }
    }

    public bool Matches(Process process, UnityProcessIdentityRecord identity, string projectRoot)
    {
        if (process.HasExited || process.Id != identity.ProcessId ||
            !TryGetProcessStartTimeTicks(process, out long startTimeTicks))
        {
            return false;
        }

        if (Math.Abs(identity.StartTimeUtcTicks - startTimeTicks) > TimeSpan.FromSeconds(3).Ticks ||
            !PathsEqual(identity.ProjectRoot, projectRoot))
        {
            return false;
        }

        string? processPath = TryGetProcessPath(process);
        return !string.IsNullOrWhiteSpace(processPath) &&
               PathsEqual(identity.ExecutablePath, processPath);
    }

    public bool TryGetOwnedProcess(int processId, string projectRoot, out Process? process)
    {
        process = null;
        if (!TryRead(out UnityProcessIdentityRecord identity) || identity.ProcessId != processId)
        {
            return false;
        }

        Process? candidate = null;
        try
        {
            candidate = Process.GetProcessById(processId);
            if (!Matches(candidate, identity, projectRoot))
            {
                return false;
            }

            process = candidate;
            candidate = null;
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            candidate?.Dispose();
        }
    }

    private static string ReadFileWithRetry(string path)
    {
        for (int attempt = 0; attempt < ReadRetries; attempt++)
        {
            try
            {
                return ReadFile(path);
            }
            catch (IOException) when (attempt < ReadRetries - 1)
            {
                Thread.Sleep(ReadRetryDelayMilliseconds);
            }
            catch (UnauthorizedAccessException) when (attempt < ReadRetries - 1)
            {
                Thread.Sleep(ReadRetryDelayMilliseconds);
            }
        }

        return ReadFile(path);
    }

    private static string ReadFile(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
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

            // The identity is published before the PID pointer. Holding the startup
            // lock prevents concurrent publication races while replacing any stale
            // identity from a prior run.
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporaryPath); } catch { }
        }
    }

    internal static bool TryGetProcessStartTimeTicks(Process process, out long ticks)
    {
        try
        {
            ticks = process.StartTime.ToUniversalTime().Ticks;
            return true;
        }
        catch
        {
            ticks = 0;
            return false;
        }
    }

    internal static string? TryGetProcessPath(Process process)
    {
        for (int i = 0; i < 5; i++)
        {
            try
            {
                if (process.MainModule?.FileName is { Length: > 0 } fileName)
                {
                    return fileName;
                }
            }
            catch
            {
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                try
                {
                    string exeSymlink = $"/proc/{process.Id}/exe";
                    var target = File.ResolveLinkTarget(exeSymlink, returnFinalTarget: true);
                    if (target != null && !string.IsNullOrWhiteSpace(target.FullName))
                    {
                        return target.FullName;
                    }
                }
                catch
                {
                }

                try
                {
                    string cmdlinePath = $"/proc/{process.Id}/cmdline";
                    if (File.Exists(cmdlinePath))
                    {
                        string cmdline = File.ReadAllText(cmdlinePath);
                        string[] tokens = cmdline.Split('\0', StringSplitOptions.RemoveEmptyEntries);
                        if (tokens.Length > 0 && !string.IsNullOrWhiteSpace(tokens[0]))
                        {
                            return tokens[0];
                        }
                    }
                }
                catch
                {
                }
            }

            if (i < 4 && !process.HasExited)
            {
                Thread.Sleep(20);
            }
        }

        return null;
    }

    private static bool PathsEqual(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        try
        {
            StringComparison comparison = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ||
                                          RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

            string fullLeft = Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string fullRight = Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (string.Equals(fullLeft, fullRight, comparison))
            {
                return true;
            }

            string resolvedLeft = TryResolveFinalTarget(fullLeft) ?? fullLeft;
            string resolvedRight = TryResolveFinalTarget(fullRight) ?? fullRight;
            return string.Equals(resolvedLeft, resolvedRight, comparison);
        }
        catch
        {
            return false;
        }
    }

    private static string? TryResolveFinalTarget(string path)
    {
        try
        {
            if (File.Exists(path) || Directory.Exists(path))
            {
                var target = File.ResolveLinkTarget(path, returnFinalTarget: true);
                if (target != null && !string.IsNullOrWhiteSpace(target.FullName))
                {
                    return target.FullName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                }
            }
        }
        catch
        {
        }

        return null;
    }
}
