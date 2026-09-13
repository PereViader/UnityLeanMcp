using System;
using System.Collections.Generic;
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

        string binaryName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Unity.exe" : "unity-editor";
        string fakeBinaryPath = Path.Combine(tempBinDir, binaryName);
        File.WriteAllText(fakeBinaryPath, "echo dummy");

        string? originalPath = Environment.GetEnvironmentVariable("PATH");
        char sep = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ';' : ':';

        try
        {
            Environment.SetEnvironmentVariable("PATH", tempBinDir + sep + originalPath);

            var locator = new UnityExecutableLocator(Path.GetTempPath(), NullLogger<UnityExecutableLocator>.Instance);
            string? found = locator.FindInPath();

            Assert.NotNull(found);
            Assert.Equal(Path.GetFullPath(fakeBinaryPath), Path.GetFullPath(found));
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
        string fakeExe = Path.Combine(tempDir, "FakeUnity.exe");
        File.WriteAllText(fakeExe, "");

        string? originalUnityPath = Environment.GetEnvironmentVariable("UNITY_PATH");
        try
        {
            Environment.SetEnvironmentVariable("UNITY_PATH", fakeExe);

            var locator = new UnityExecutableLocator(tempDir, NullLogger<UnityExecutableLocator>.Instance);
            string? found = locator.FindUnityExecutable();

            Assert.NotNull(found);
            Assert.Equal(Path.GetFullPath(fakeExe), Path.GetFullPath(found));
        }
        finally
        {
            Environment.SetEnvironmentVariable("UNITY_PATH", originalUnityPath);
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void FindUnityExecutable_WhenUnityEditorEnvSetAndFileExists_ReturnsConfiguredPath()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "test_env_editor_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string fakeExe = Path.Combine(tempDir, "FakeEditor.exe");
        File.WriteAllText(fakeExe, "");

        string? originalUnityPath = Environment.GetEnvironmentVariable("UNITY_PATH");
        string? originalUnityEditor = Environment.GetEnvironmentVariable("UNITY_EDITOR");
        try
        {
            Environment.SetEnvironmentVariable("UNITY_PATH", null);
            Environment.SetEnvironmentVariable("UNITY_EDITOR", fakeExe);

            var locator = new UnityExecutableLocator(tempDir, NullLogger<UnityExecutableLocator>.Instance);
            string? found = locator.FindUnityExecutable();

            Assert.NotNull(found);
            Assert.Equal(Path.GetFullPath(fakeExe), Path.GetFullPath(found));
        }
        finally
        {
            Environment.SetEnvironmentVariable("UNITY_PATH", originalUnityPath);
            Environment.SetEnvironmentVariable("UNITY_EDITOR", originalUnityEditor);
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
            Assert.Equal(pm.ExecutableLocator.GetProjectEditorVersion(), pm.GetProjectEditorVersion());
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    private class MockExecutableLocator : IUnityExecutableLocator
    {
        public string? ExecutableToReturn { get; set; }
        public string? VersionToReturn { get; set; }
        public List<string> CandidatePathsToReturn { get; set; } = new();

        public string? FindUnityExecutable() => ExecutableToReturn;
        public string? FindInPath() => ExecutableToReturn;
        public string? GetProjectEditorVersion() => VersionToReturn;
        public List<string> GetStandardHubCandidatePaths(string version) => CandidatePathsToReturn;
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
                executableLocator: mockLocator);

            var ex = await Assert.ThrowsAsync<FileNotFoundException>(() => pm.EnsureUnityRunningAsync(CancellationToken.None));
            Assert.Contains("Unity executable not found for project", ex.Message);
            Assert.Contains("2022.3.99f1", ex.Message);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }
}
