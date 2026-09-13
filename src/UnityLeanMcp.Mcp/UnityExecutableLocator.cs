using System;
using System.Collections.Generic;
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
    public virtual string? FindUnityExecutable()
    {
        // 1. Environment variables
        string? configuredPath = Environment.GetEnvironmentVariable("UNITY_PATH")
            ?? Environment.GetEnvironmentVariable("UNITY_EDITOR");

        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
        {
            return Path.GetFullPath(configuredPath);
        }

        // 2. Read editor version from ProjectSettings/ProjectVersion.txt
        string? editorVersion = GetProjectEditorVersion();

        // 3. Check standard Unity Hub paths
        if (!string.IsNullOrWhiteSpace(editorVersion))
        {
            var hubPaths = GetStandardHubCandidatePaths(editorVersion);
            foreach (var candidate in hubPaths)
            {
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
        }

        // 4. Search in PATH
        return FindInPath();
    }

    public virtual string? FindInPath()
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
                    if (File.Exists(candidate))
                    {
                        return Path.GetFullPath(candidate);
                    }
                }
            }
        }

        return null;
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
            paths.Add("/opt/unity/Editor/Unity");
            paths.Add("/opt/Unity/Editor/Unity");
        }

        return paths;
    }
}
