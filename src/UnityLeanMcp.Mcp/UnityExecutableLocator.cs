using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace UnityLeanMcp.Mcp;

public class UnityExecutableLocator : IUnityExecutableLocator
{
    private readonly IUnityPathResolver _pathResolver;
    private readonly ILogger? _logger;

    public UnityExecutableLocator(IUnityPathResolver pathResolver, ILogger<UnityExecutableLocator>? logger = null)
        : this(pathResolver, (ILogger?)logger)
    {
    }

    public UnityExecutableLocator(IUnityPathResolver pathResolver, ILogger? logger)
    {
        _pathResolver = pathResolver ?? throw new ArgumentNullException(nameof(pathResolver));
        _logger = logger;
    }

    public UnityExecutableLocator(string projectRoot, ILogger? logger = null)
        : this(new UnityPathResolver(projectRoot), logger)
    {
    }

    /// <summary>
    /// Locates the Unity executable for this project:
    /// - Checks UNITY_PATH and UNITY_EDITOR environment variables.
    /// - Reads ProjectSettings/ProjectVersion.txt (extracts m_EditorVersion:).
    /// - Checks standard Unity Hub paths on Windows, macOS, and Linux.
    /// - Checks PATH (unity-editor, Unity, Unity.exe, unity).
    /// </summary>
    public virtual UnityLocatorResult FindUnityExecutable()
    {
        string? firstDiagnostic = null;

        // 1. Environment variables
        foreach (var configured in new[]
        {
            (Name: "UNITY_PATH", Path: Environment.GetEnvironmentVariable("UNITY_PATH")),
            (Name: "UNITY_EDITOR", Path: Environment.GetEnvironmentVariable("UNITY_EDITOR"))
        })
        {
            if (TryAcceptCandidate(configured.Path, configured.Name, out string? executable, ref firstDiagnostic))
            {
                return UnityLocatorResult.Found(executable);
            }
        }

        // 2. Read editor version from ProjectSettings/ProjectVersion.txt
        string? editorVersion = GetProjectEditorVersion();

        // 3. Check standard Unity Hub paths
        if (!string.IsNullOrWhiteSpace(editorVersion))
        {
            var hubPaths = GetStandardHubCandidatePaths(editorVersion);
            foreach (var candidate in hubPaths)
            {
                if (TryAcceptCandidate(candidate, "Unity Hub", out string? executable, ref firstDiagnostic))
                {
                    return UnityLocatorResult.Found(executable);
                }
            }
        }

        // 4. Search in PATH
        return FindInPathCore(ref firstDiagnostic);
    }

    public virtual UnityLocatorResult FindInPath()
    {
        string? firstDiagnostic = null;
        return FindInPathCore(ref firstDiagnostic);
    }

    private UnityLocatorResult FindInPathCore(ref string? firstDiagnostic)
    {
        string[] binaryNames = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new[] { "Unity.exe", "unity-editor.exe", "unity.exe" }
            : new[] { "unity-editor", "Unity", "unity" };

        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathEnv))
        {
            char sep = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ';' : ':';
            var directories = pathEnv.Split(sep, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var dir in directories)
            {
                foreach (var binary in binaryNames)
                {
                    string candidate = Path.Combine(dir, binary);
                    if (TryAcceptCandidate(candidate, "PATH", out string? executable, ref firstDiagnostic))
                    {
                        return UnityLocatorResult.Found(executable);
                    }
                }
            }
        }

        string diagnostic = firstDiagnostic ??
                            "No candidate matched a Unity Editor installation layout. " +
                            "Set UNITY_PATH or UNITY_EDITOR to the Editor executable, or install an Editor via Unity Hub.";
        return UnityLocatorResult.NotFound(diagnostic);
    }

    private bool TryAcceptCandidate(string? candidate, string source, [NotNullWhen(true)] out string? executable, ref string? firstDiagnostic)
    {
        executable = null;
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(candidate.Trim());
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            RecordRejection(source, candidate, "the path is not valid", ref firstDiagnostic);
            return false;
        }

        if (!File.Exists(fullPath))
        {
            if (Directory.Exists(fullPath))
            {
                foreach (var candidateFile in GetDirectoryCandidateExecutables(fullPath))
                {
                    if (File.Exists(candidateFile) && TryAcceptCandidate(candidateFile, source, out executable, ref firstDiagnostic))
                    {
                        return true;
                    }
                }

                RecordRejection(source, fullPath, "it is a directory but contains no Unity Editor executable", ref firstDiagnostic);
                return false;
            }

            // A PATH directory is probed with several platform-specific names;
            // missing names are normal and should not hide a later, meaningful
            // rejection (such as a same-named Unity CLI binary).
            if (!string.Equals(source, "PATH", StringComparison.Ordinal))
            {
                RecordRejection(source, fullPath, "the file does not exist", ref firstDiagnostic);
            }
            return false;
        }

        if (!IsExecutableFile(fullPath))
        {
            RecordRejection(source, fullPath, "the file is not executable", ref firstDiagnostic);
            return false;
        }

        string? rejectionReason = GetEditorLayoutRejectionReason(fullPath);
        if (rejectionReason != null)
        {
            RecordRejection(source, fullPath, rejectionReason, ref firstDiagnostic);
            return false;
        }

        executable = fullPath;
        return true;
    }

    private static bool IsExecutableFile(string path)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return true;
        }

        try
        {
            UnixFileMode mode = File.GetUnixFileMode(path);
            return (mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
        }
        catch (PlatformNotSupportedException)
        {
            // The check is an additional guard, not a requirement for runtimes
            // that cannot expose POSIX mode bits.
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string? GetEditorLayoutRejectionReason(string executablePath)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            var executable = new FileInfo(executablePath);
            DirectoryInfo? macOsDirectory = executable.Directory;
            DirectoryInfo? contentsDirectory = macOsDirectory?.Parent;
            DirectoryInfo? appDirectory = contentsDirectory?.Parent;

            bool isMacEditorBinary = string.Equals(macOsDirectory?.Name, "MacOS", StringComparison.Ordinal) &&
                                     string.Equals(contentsDirectory?.Name, "Contents", StringComparison.Ordinal) &&
                                     appDirectory?.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase) == true;
            if (isMacEditorBinary &&
                contentsDirectory != null &&
                File.Exists(Path.Combine(contentsDirectory.FullName, "Managed", "UnityEditor.dll")))
            {
                return null;
            }

            return "it is not inside a Unity Editor app bundle (expected Unity.app/Contents/MacOS/Unity with Contents/Managed/UnityEditor.dll)";
        }

        string? editorDirectory = Path.GetDirectoryName(executablePath);
        if (string.IsNullOrWhiteSpace(editorDirectory))
        {
            return "it has no containing Unity Editor installation directory";
        }

        // On Windows and Linux, Unity Editor installations contain Data/Managed/UnityEditor.dll and/or UnityEngine.dll
        string managedEditorAssembly = Path.Combine(editorDirectory, "Data", "Managed", "UnityEditor.dll");
        string managedEngineAssembly = Path.Combine(editorDirectory, "Data", "Managed", "UnityEngine.dll");
        if (File.Exists(managedEditorAssembly) || File.Exists(managedEngineAssembly))
        {
            return null;
        }

        return $"it is not inside a Unity Editor installation (expected Data/Managed/UnityEditor.dll or Data/Managed/UnityEngine.dll relative to '{editorDirectory}')";
    }

    private void RecordRejection(string source, string candidate, string reason, ref string? firstDiagnostic)
    {
        string diagnostic = $"{source} candidate '{candidate}' was rejected: {reason}.";
        // Preserve the first useful rejection. In particular, a configured
        // UNITY_PATH pointing at the Unity CLI should not be hidden by a later
        // missing entry encountered while scanning PATH.
        if (firstDiagnostic == null)
        {
            firstDiagnostic = diagnostic;
        }
        _logger?.LogWarning("{Diagnostic}", diagnostic);
    }

    public virtual string? GetProjectEditorVersion()
    {
        string versionFilePath = Path.Combine(_pathResolver.ProjectRoot, "ProjectSettings", "ProjectVersion.txt");
        if (!File.Exists(versionFilePath))
        {
            return null;
        }

        try
        {
            foreach (var line in File.ReadAllLines(versionFilePath))
            {
                if (line.StartsWith("m_EditorVersion:", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = line.Split(':', 2);
                    if (parts.Length == 2)
                    {
                        return parts[1].Trim();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to read editor version from {Path}", versionFilePath);
        }

        return null;
    }

    public virtual List<string> GetStandardHubCandidatePaths(string version)
    {
        var paths = new List<string>();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            string? programFiles = Environment.GetEnvironmentVariable("ProgramFiles");
            string? programW6432 = Environment.GetEnvironmentVariable("ProgramW6432");
            string? localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");

            if (!string.IsNullOrWhiteSpace(programFiles))
                paths.Add(Path.Combine(programFiles, "Unity", "Hub", "Editor", version, "Editor", "Unity.exe"));
            if (!string.IsNullOrWhiteSpace(programW6432))
                paths.Add(Path.Combine(programW6432, "Unity", "Hub", "Editor", version, "Editor", "Unity.exe"));
            if (!string.IsNullOrWhiteSpace(localAppData))
                paths.Add(Path.Combine(localAppData, "Unity", "Hub", "Editor", version, "Editor", "Unity.exe"));

            paths.Add($@"C:\Program Files\Unity\Hub\Editor\{version}\Editor\Unity.exe");
            paths.Add($@"C:\Program Files (x86)\Unity\Hub\Editor\{version}\Editor\Unity.exe");
            paths.Add($@"C:\Unity\Hub\Editor\{version}\Editor\Unity.exe");
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            paths.Add($"/Applications/Unity/Hub/Editor/{version}/Unity.app/Contents/MacOS/Unity");
            string? home = Environment.GetEnvironmentVariable("HOME");
            if (!string.IsNullOrWhiteSpace(home))
            {
                paths.Add(Path.Combine(home, "Unity", "Hub", "Editor", version, "Unity.app", "Contents", "MacOS", "Unity"));
            }
        }
        else // Linux
        {
            string? home = Environment.GetEnvironmentVariable("HOME");
            if (!string.IsNullOrWhiteSpace(home))
            {
                paths.Add(Path.Combine(home, "Unity", "Hub", "Editor", version, "Editor", "Unity"));
            }
            paths.Add($"/opt/unity/Editor/{version}/Editor/Unity");
            paths.Add($"/opt/Unity/Editor/{version}/Editor/Unity");
            paths.Add($"/opt/unity/hub/Editor/{version}/Editor/Unity");
            paths.Add($"/opt/unity/editors/{version}/Editor/Unity");
            paths.Add($"/opt/Unity/Hub/Editor/{version}/Editor/Unity");
        }

        return paths;
    }

    private static IEnumerable<string> GetDirectoryCandidateExecutables(string directoryPath)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            yield return Path.Combine(directoryPath, "Editor", "Unity.exe");
            yield return Path.Combine(directoryPath, "Unity.exe");
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            yield return Path.Combine(directoryPath, "Unity.app", "Contents", "MacOS", "Unity");
            yield return Path.Combine(directoryPath, "Contents", "MacOS", "Unity");
            yield return Path.Combine(directoryPath, "MacOS", "Unity");
            yield return Path.Combine(directoryPath, "Unity");
        }
        else // Linux
        {
            yield return Path.Combine(directoryPath, "Editor", "Unity");
            yield return Path.Combine(directoryPath, "Unity");
        }
    }
}
