using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace UnityLeanMcp.Mcp.Tests;

[Trait("Category", "Unit")]
public class UnityExecutableLocatorTests
{
    [Fact]
    public void GetProjectEditorVersion_WhenVersionFileDoesNotExist_ReturnsNull()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "test_locator_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var locator = new UnityExecutableLocator(tempDir, NullLogger<UnityExecutableLocator>.Instance);
            string? version = locator.GetProjectEditorVersion();
            Assert.Null(version);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void GetProjectEditorVersion_WhenVersionFileExists_ExtractsVersionProperly()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "test_locator_" + Guid.NewGuid().ToString("N"));
        string settingsDir = Path.Combine(tempDir, "ProjectSettings");
        Directory.CreateDirectory(settingsDir);
        string versionFile = Path.Combine(settingsDir, "ProjectVersion.txt");

        try
        {
            File.WriteAllText(versionFile, "m_EditorVersion: 2022.3.10f1\nm_EditorVersionWithRevision: 2022.3.10f1 (e39a2d6092d0)\n");

            var locator = new UnityExecutableLocator(tempDir, NullLogger<UnityExecutableLocator>.Instance);
            string? version = locator.GetProjectEditorVersion();

            Assert.Equal("2022.3.10f1", version);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void GetProjectEditorVersion_WhenVersionKeyMissing_ReturnsNull()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "test_locator_" + Guid.NewGuid().ToString("N"));
        string settingsDir = Path.Combine(tempDir, "ProjectSettings");
        Directory.CreateDirectory(settingsDir);
        string versionFile = Path.Combine(settingsDir, "ProjectVersion.txt");

        try
        {
            File.WriteAllText(versionFile, "some_other_key: 12345\n");

            var locator = new UnityExecutableLocator(tempDir, NullLogger<UnityExecutableLocator>.Instance);
            string? version = locator.GetProjectEditorVersion();

            Assert.Null(version);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void GetStandardHubCandidatePaths_ReturnsCandidatesContainingVersion()
    {
        var locator = new UnityExecutableLocator(Path.GetTempPath(), NullLogger<UnityExecutableLocator>.Instance);
        string version = "6000.0.23f1";

        var paths = locator.GetStandardHubCandidatePaths(version);

        Assert.NotEmpty(paths);
        foreach (var p in paths)
        {
            Assert.Contains(version, p);
        }
    }

    [Fact]
    public void FindInPath_WhenBinaryExistsInPath_ReturnsFullPath()
    {
        string tempBinDir = Path.Combine(Path.GetTempPath(), "test_path_bin_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempBinDir);

        string fakeBinaryPath = CreateEditorFixture(tempBinDir);
        string pathDirectory = Path.GetDirectoryName(fakeBinaryPath)!;

        string? originalPath = Environment.GetEnvironmentVariable("PATH");
        char sep = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ';' : ':';

        try
        {
            Environment.SetEnvironmentVariable("PATH", pathDirectory + sep + originalPath);

            var locator = new UnityExecutableLocator(Path.GetTempPath(), NullLogger<UnityExecutableLocator>.Instance);
            UnityLocatorResult result = locator.FindInPath();

            Assert.True(result.Success);
            Assert.NotNull(result.ExecutablePath);
            Assert.Equal(Path.GetFullPath(fakeBinaryPath), Path.GetFullPath(result.ExecutablePath));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
            try { Directory.Delete(tempBinDir, true); } catch { }
        }
    }

    [Fact]
    public void FindUnityExecutable_WhenUnityPathEnvSetAndFileExists_ReturnsConfiguredPath()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "test_env_exe_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string fakeExe = CreateEditorFixture(tempDir);

        string? originalUnityPath = Environment.GetEnvironmentVariable("UNITY_PATH");
        string? originalUnityEditor = Environment.GetEnvironmentVariable("UNITY_EDITOR");
        try
        {
            Environment.SetEnvironmentVariable("UNITY_PATH", fakeExe);
            Environment.SetEnvironmentVariable("UNITY_EDITOR", null);

            var locator = new UnityExecutableLocator(tempDir, NullLogger<UnityExecutableLocator>.Instance);
            UnityLocatorResult result = locator.FindUnityExecutable();

            Assert.True(result.Success);
            Assert.NotNull(result.ExecutablePath);
            Assert.Equal(Path.GetFullPath(fakeExe), Path.GetFullPath(result.ExecutablePath));
        }
        finally
        {
            Environment.SetEnvironmentVariable("UNITY_PATH", originalUnityPath);
            Environment.SetEnvironmentVariable("UNITY_EDITOR", originalUnityEditor);
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void FindUnityExecutable_WhenUnityEditorEnvSetAndFileExists_ReturnsConfiguredPath()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "test_env_editor_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string fakeExe = CreateEditorFixture(tempDir);

        string? originalUnityPath = Environment.GetEnvironmentVariable("UNITY_PATH");
        string? originalUnityEditor = Environment.GetEnvironmentVariable("UNITY_EDITOR");
        try
        {
            Environment.SetEnvironmentVariable("UNITY_PATH", null);
            Environment.SetEnvironmentVariable("UNITY_EDITOR", fakeExe);

            var locator = new UnityExecutableLocator(tempDir, NullLogger<UnityExecutableLocator>.Instance);
            UnityLocatorResult result = locator.FindUnityExecutable();

            Assert.True(result.Success);
            Assert.NotNull(result.ExecutablePath);
            Assert.Equal(Path.GetFullPath(fakeExe), Path.GetFullPath(result.ExecutablePath));
        }
        finally
        {
            Environment.SetEnvironmentVariable("UNITY_PATH", originalUnityPath);
            Environment.SetEnvironmentVariable("UNITY_EDITOR", originalUnityEditor);
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void FindUnityExecutable_WhenConfiguredPathIsUnityCli_RejectsItWithDiagnostic()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "test_cli_env_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string cliPath = Path.Combine(tempDir, RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Unity.exe" : "Unity");
        File.WriteAllText(cliPath, "unity cli");
        MakeExecutableIfSupported(cliPath);

        string? originalUnityPath = Environment.GetEnvironmentVariable("UNITY_PATH");
        string? originalUnityEditor = Environment.GetEnvironmentVariable("UNITY_EDITOR");
        string? originalPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("UNITY_PATH", cliPath);
            Environment.SetEnvironmentVariable("UNITY_EDITOR", null);
            Environment.SetEnvironmentVariable("PATH", GetIsolatedPathWithDotnet(tempDir, originalPath));

            var locator = new UnityExecutableLocator(tempDir, NullLogger<UnityExecutableLocator>.Instance);
            UnityLocatorResult result = locator.FindUnityExecutable();

            Assert.False(result.Success);
            Assert.Null(result.ExecutablePath);
            Assert.Contains("UNITY_PATH", result.Diagnostic);
            Assert.Contains("Unity Editor", result.Diagnostic);
        }
        finally
        {
            Environment.SetEnvironmentVariable("UNITY_PATH", originalUnityPath);
            Environment.SetEnvironmentVariable("UNITY_EDITOR", originalUnityEditor);
            Environment.SetEnvironmentVariable("PATH", originalPath);
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void FindUnityExecutable_WhenConfiguredPathIsStandalonePlayer_RejectsItWithDiagnostic()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "test_player_env_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string exeName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Game.exe" : "Game";
        string playerExe = Path.Combine(tempDir, exeName);
        File.WriteAllText(playerExe, "standalone player binary");
        MakeExecutableIfSupported(playerExe);

        // Standalone player layout: <name>_Data/Managed/UnityEngine.dll
        string playerManagedDir = Path.Combine(tempDir, $"{Path.GetFileNameWithoutExtension(exeName)}_Data", "Managed");
        Directory.CreateDirectory(playerManagedDir);
        File.WriteAllText(Path.Combine(playerManagedDir, "UnityEngine.dll"), "player engine dll");

        string? originalUnityPath = Environment.GetEnvironmentVariable("UNITY_PATH");
        string? originalUnityEditor = Environment.GetEnvironmentVariable("UNITY_EDITOR");
        string? originalPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("UNITY_PATH", playerExe);
            Environment.SetEnvironmentVariable("UNITY_EDITOR", null);
            Environment.SetEnvironmentVariable("PATH", GetIsolatedPathWithDotnet(tempDir, originalPath));

            var locator = new UnityExecutableLocator(tempDir, NullLogger<UnityExecutableLocator>.Instance);
            UnityLocatorResult result = locator.FindUnityExecutable();

            Assert.False(result.Success);
            Assert.Null(result.ExecutablePath);
            Assert.Contains("UNITY_PATH", result.Diagnostic);
            Assert.Contains("not inside a Unity Editor", result.Diagnostic);
        }
        finally
        {
            Environment.SetEnvironmentVariable("UNITY_PATH", originalUnityPath);
            Environment.SetEnvironmentVariable("UNITY_EDITOR", originalUnityEditor);
            Environment.SetEnvironmentVariable("PATH", originalPath);
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void FindInPath_WhenPathCandidateIsUnityCli_RejectsIt()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "test_cli_path_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string cliPath = Path.Combine(tempDir, RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Unity.exe" : "Unity");
        File.WriteAllText(cliPath, "unity cli");
        MakeExecutableIfSupported(cliPath);

        string? originalPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", GetIsolatedPathWithDotnet(tempDir, originalPath));
            var locator = new UnityExecutableLocator(tempDir, NullLogger<UnityExecutableLocator>.Instance);
            UnityLocatorResult result = locator.FindInPath();

            Assert.False(result.Success);
            Assert.Null(result.ExecutablePath);
            Assert.Contains("PATH", result.Diagnostic);
            Assert.Contains("Unity Editor", result.Diagnostic);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void FindUnityExecutable_WhenHubCandidatesContainCliAndEditor_ReturnsEditorCandidate()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "test_hub_candidates_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string cliPath = Path.Combine(tempDir, "cli", RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Unity.exe" : "Unity");
        Directory.CreateDirectory(Path.GetDirectoryName(cliPath)!);
        File.WriteAllText(cliPath, "unity cli");
        MakeExecutableIfSupported(cliPath);
        string editorPath = CreateEditorFixture(Path.Combine(tempDir, "editor"));

        string? originalUnityPath = Environment.GetEnvironmentVariable("UNITY_PATH");
        string? originalUnityEditor = Environment.GetEnvironmentVariable("UNITY_EDITOR");
        string? originalPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("UNITY_PATH", null);
            Environment.SetEnvironmentVariable("UNITY_EDITOR", null);
            Environment.SetEnvironmentVariable("PATH", GetIsolatedPathWithDotnet(tempDir, originalPath));

            var locator = new CandidateLocator(tempDir, new List<string> { cliPath, editorPath });
            UnityLocatorResult result = locator.FindUnityExecutable();

            Assert.True(result.Success);
            Assert.Equal(Path.GetFullPath(editorPath), result.ExecutablePath);
        }
        finally
        {
            Environment.SetEnvironmentVariable("UNITY_PATH", originalUnityPath);
            Environment.SetEnvironmentVariable("UNITY_EDITOR", originalUnityEditor);
            Environment.SetEnvironmentVariable("PATH", originalPath);
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void UnityProcessManager_ExposesAndDelegatesToExecutableLocator()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "test_pm_locator_" + Guid.NewGuid().ToString("N"));
        string settingsDir = Path.Combine(tempDir, "ProjectSettings");
        Directory.CreateDirectory(settingsDir);
        string versionFile = Path.Combine(settingsDir, "ProjectVersion.txt");

        try
        {
            File.WriteAllText(versionFile, "m_EditorVersion: 2021.3.16f1\n");

            var pm = new UnityProcessManager(tempDir, NullLogger<UnityProcessManager>.Instance);

            Assert.NotNull(pm.ExecutableLocator);
            Assert.Equal("2021.3.16f1", pm.GetProjectEditorVersion());
            Assert.Equal(((UnityExecutableLocator)pm.ExecutableLocator).GetProjectEditorVersion(), pm.GetProjectEditorVersion());
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    private class MockExecutableLocator : UnityExecutableLocator
    {
        public MockExecutableLocator() : base(new UnityPathResolver(Path.GetTempPath()))
        {
        }

        public string? ExecutableToReturn { get; set; }
        public string? VersionToReturn { get; set; }
        public List<string> CandidatePathsToReturn { get; set; } = new();
        public string? DiagnosticToReturn { get; set; }

        public override UnityLocatorResult FindUnityExecutable() =>
            !string.IsNullOrWhiteSpace(ExecutableToReturn)
                ? UnityLocatorResult.Found(ExecutableToReturn)
                : UnityLocatorResult.NotFound(DiagnosticToReturn ?? "No candidate matched a Unity Editor installation layout.");

        public override UnityLocatorResult FindInPath() =>
            !string.IsNullOrWhiteSpace(ExecutableToReturn)
                ? UnityLocatorResult.Found(ExecutableToReturn)
                : UnityLocatorResult.NotFound(DiagnosticToReturn ?? "No candidate matched a Unity Editor installation layout.");

        public override string? GetProjectEditorVersion() => VersionToReturn;
        public override List<string> GetStandardHubCandidatePaths(string version) => CandidatePathsToReturn;
    }

    [Fact]
    public void UnityProcessManager_SupportsCustomInjectedExecutableLocator()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "test_pm_custom_locator_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var mockLocator = new MockExecutableLocator
            {
                ExecutableToReturn = Path.Combine(tempDir, "MockUnity.exe"),
                VersionToReturn = "9999.1.0f1"
            };

            var resolver = new UnityPathResolver(tempDir);
            var pm = new UnityProcessManager(
                resolver,
                NullLogger<UnityProcessManager>.Instance,
                executableLocator: mockLocator);

            Assert.Same(mockLocator, pm.ExecutableLocator);
            Assert.Equal("9999.1.0f1", pm.GetProjectEditorVersion());
            Assert.Equal(Path.Combine(tempDir, "MockUnity.exe"), pm.FindUnityExecutable());
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task UnityProcessManager_EnsureUnityRunningAsync_ThrowsFileNotFoundWhenExecutableNotFound()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "test_pm_missing_exe_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var mockLocator = new MockExecutableLocator
            {
                ExecutableToReturn = null,
                VersionToReturn = "2022.3.99f1"
            };

            var resolver = new UnityPathResolver(tempDir);
            var pm = new UnityProcessManager(
                resolver,
                NullLogger<UnityProcessManager>.Instance,
                executableLocator: mockLocator)
            {
                ProcessProvider = () => Array.Empty<Process>()
            };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var ex = await Assert.ThrowsAsync<FileNotFoundException>(() => pm.EnsureUnityRunningAsync(cts.Token));
            Assert.Contains("Unity executable not found for project", ex.Message);
            Assert.Contains("2022.3.99f1", ex.Message);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    private sealed class CandidateLocator : UnityExecutableLocator
    {
        private readonly List<string> _candidates;

        public CandidateLocator(string projectRoot, List<string> candidates)
            : base(projectRoot, NullLogger<UnityExecutableLocator>.Instance)
        {
            _candidates = candidates;
        }

        public override string? GetProjectEditorVersion() => "6000.0.0f1";
        public override List<string> GetStandardHubCandidatePaths(string version) => _candidates;
    }

    private static string CreateEditorFixture(string root)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            string appRoot = Path.Combine(root, "Unity.app");
            string executable = Path.Combine(appRoot, "Contents", "MacOS", "Unity");
            Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
            Directory.CreateDirectory(Path.Combine(appRoot, "Contents", "Managed"));
            Directory.CreateDirectory(Path.Combine(appRoot, "Contents", "Resources"));
            File.WriteAllText(executable, "unity editor");
            File.WriteAllText(Path.Combine(appRoot, "Contents", "Managed", "UnityEditor.dll"), "editor metadata");
            MakeExecutableIfSupported(executable);
            return executable;
        }

        string editorDirectory = Path.Combine(root, "Editor");
        string executableName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Unity.exe" : "Unity";
        string executablePath = Path.Combine(editorDirectory, executableName);
        string managedDirectory = Path.Combine(editorDirectory, "Data", "Managed");
        Directory.CreateDirectory(managedDirectory);
        File.WriteAllText(executablePath, "unity editor");
        File.WriteAllText(Path.Combine(managedDirectory, "UnityEditor.dll"), "editor metadata");
        File.WriteAllText(Path.Combine(managedDirectory, "UnityEngine.dll"), "engine metadata");
        MakeExecutableIfSupported(executablePath);
        return executablePath;
    }

    private static void MakeExecutableIfSupported(string path)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static string GetIsolatedPathWithDotnet(string tempDir, string? originalPath)
    {
        char sep = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ';' : ':';
        string dotnetBinary = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "dotnet.exe" : "dotnet";
        if (!string.IsNullOrWhiteSpace(originalPath))
        {
            foreach (var dir in originalPath.Split(sep, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (File.Exists(Path.Combine(dir, dotnetBinary)))
                {
                    return $"{tempDir}{sep}{dir}";
                }
            }
        }
        string? dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrWhiteSpace(dotnetRoot) && File.Exists(Path.Combine(dotnetRoot, dotnetBinary)))
        {
            return $"{tempDir}{sep}{dotnetRoot}";
        }
        return tempDir;
    }
}
