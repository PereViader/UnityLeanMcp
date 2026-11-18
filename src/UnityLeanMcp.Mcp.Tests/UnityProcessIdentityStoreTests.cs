using System;
using System.Diagnostics;
using System.IO;

namespace UnityLeanMcp.Mcp.Tests;

public sealed class UnityProcessIdentityStoreTests
{
    [Fact]
    public void WriteAndRead_RoundTripsDurableIdentity()
    {
        string projectRoot = CreateProjectRoot();
        using Process process = Process.GetCurrentProcess();
        string executablePath = process.MainModule?.FileName ?? throw new InvalidOperationException();
        var store = CreateStore(projectRoot);

        try
        {
            store.Write(process, executablePath, projectRoot);

            Assert.True(File.Exists(store.FilePath));
            Assert.True(store.TryRead(out UnityProcessIdentityRecord identity));
            Assert.Equal(process.Id, identity.ProcessId);
            Assert.Equal(process.StartTime.ToUniversalTime().Ticks, identity.StartTimeUtcTicks);
            Assert.Equal(Path.GetFullPath(executablePath), identity.ExecutablePath);
            Assert.Equal(projectRoot, identity.ProjectRoot);
            Assert.Equal("GUI", identity.Mode);
        }
        finally
        {
            DeleteProjectRoot(projectRoot);
        }
    }

    [Fact]
    public void WriteAndRead_PreservesCustomMode()
    {
        string projectRoot = CreateProjectRoot();
        using Process process = Process.GetCurrentProcess();
        string executablePath = process.MainModule?.FileName ?? throw new InvalidOperationException();
        var store = CreateStore(projectRoot);

        try
        {
            store.Write(process, executablePath, projectRoot, mode: "Batchmode");

            Assert.True(File.Exists(store.FilePath));
            Assert.True(store.TryRead(out UnityProcessIdentityRecord identity));
            Assert.Equal("Batchmode", identity.Mode);
        }
        finally
        {
            DeleteProjectRoot(projectRoot);
        }
    }

    [Fact]
    public void Matches_RequiresProcessAndProjectIdentityToAgree()
    {
        string projectRoot = CreateProjectRoot();
        using Process process = Process.GetCurrentProcess();
        string executablePath = process.MainModule?.FileName ?? throw new InvalidOperationException();
        var store = CreateStore(projectRoot);

        try
        {
            store.Write(process, executablePath, projectRoot);
            Assert.True(store.TryRead(out UnityProcessIdentityRecord identity));
            Assert.True(store.Matches(process, identity, projectRoot));
            Assert.False(store.Matches(process, identity, Path.Combine(projectRoot, "other")));
        }
        finally
        {
            DeleteProjectRoot(projectRoot);
        }
    }

    [Fact]
    public void TryGetOwnedProcess_UsesPersistedIdentityBeforeReturningHandle()
    {
        string projectRoot = CreateProjectRoot();
        using Process process = Process.GetCurrentProcess();
        string executablePath = process.MainModule?.FileName ?? throw new InvalidOperationException();
        var store = CreateStore(projectRoot);

        try
        {
            store.Write(process, executablePath, projectRoot);

            Assert.True(store.TryGetOwnedProcess(process.Id, projectRoot, out Process? ownedProcess));
            using (ownedProcess)
            {
                Assert.NotNull(ownedProcess);
                Assert.Equal(process.Id, ownedProcess!.Id);
            }
        }
        finally
        {
            DeleteProjectRoot(projectRoot);
        }
    }

    [Fact]
    public void Matches_WithCasingDifferenceOnCaseInsensitiveOS_MatchesSuccessfully()
    {
        string projectRoot = CreateProjectRoot();
        using Process process = Process.GetCurrentProcess();
        string executablePath = process.MainModule?.FileName ?? throw new InvalidOperationException();
        var store = CreateStore(projectRoot);

        try
        {
            store.Write(process, executablePath, projectRoot);
            Assert.True(store.TryRead(out UnityProcessIdentityRecord identity));

            var casedIdentity = new UnityProcessIdentityRecord
            {
                ProcessId = identity.ProcessId,
                StartTimeUtcTicks = identity.StartTimeUtcTicks,
                ExecutablePath = identity.ExecutablePath.ToUpperInvariant(),
                ProjectRoot = identity.ProjectRoot.ToUpperInvariant()
            };

            bool matches = store.Matches(process, casedIdentity, projectRoot);
            if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows) ||
                System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.OSX))
            {
                Assert.True(matches);
            }
        }
        finally
        {
            DeleteProjectRoot(projectRoot);
        }
    }

    [Fact]
    public void ResolveRealPath_CustomLinkResolver_HandlesExistingFilesAndDirectories()
    {
        string projectRoot = CreateProjectRoot();
        string realDir = Path.Combine(projectRoot, "RealDirectory");
        Directory.CreateDirectory(realDir);
        string realFile = Path.Combine(realDir, "UnityEditor.bin");
        File.WriteAllText(realFile, "binary");

        string dirLink = Path.Combine(projectRoot, "LinkDirectory");
        string fileLink = Path.Combine(projectRoot, "LinkFile");

        try
        {
            FileUnityProcessIdentityStore.CustomLinkResolver = path =>
            {
                string norm = path.Replace('\\', '/').TrimEnd('/');
                if (string.Equals(norm, dirLink.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase))
                {
                    return realDir;
                }

                if (string.Equals(norm, fileLink.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase))
                {
                    return realFile;
                }

                return null;
            };

            string resolvedDir = FileUnityProcessIdentityStore.ResolveRealPath(dirLink);
            string resolvedFile = FileUnityProcessIdentityStore.ResolveRealPath(fileLink);

            Assert.Equal(FileUnityProcessIdentityStore.ResolveRealPath(realDir), resolvedDir);
            Assert.Equal(FileUnityProcessIdentityStore.ResolveRealPath(realFile), resolvedFile);
            Assert.True(FileUnityProcessIdentityStore.PathsEqual(fileLink, realFile));
            Assert.True(FileUnityProcessIdentityStore.PathsEqual(dirLink, realDir));
            Assert.Equal(resolvedFile, UnityLeanMcp.ProjectCommandEnvelope.ResolveRealPath(fileLink));
        }
        finally
        {
            FileUnityProcessIdentityStore.CustomLinkResolver = null;
            DeleteProjectRoot(projectRoot);
        }
    }

    [Fact]
    public void ProjectCommandEnvelope_CanonicalizesOnDiskCasingOnCaseInsensitiveVolumes()
    {
        string projectRoot = CreateProjectRoot();
        string mixedCaseProject = Path.Combine(projectRoot, "MyUnityProject");
        Directory.CreateDirectory(mixedCaseProject);

        try
        {
            string lowerCaseVariant = Path.Combine(projectRoot, "myunityproject");
            if (Directory.Exists(lowerCaseVariant))
            {
                string line = UnityLeanMcp.ProjectCommandEnvelope.Encode(lowerCaseVariant, "PING");
                Assert.True(UnityLeanMcp.ProjectCommandEnvelope.TryDecode(line, mixedCaseProject, out string command));
                Assert.Equal("PING", command);
            }
        }
        finally
        {
            DeleteProjectRoot(projectRoot);
        }
    }

    private static FileUnityProcessIdentityStore CreateStore(string projectRoot) =>
        new(Path.Combine(projectRoot, "Temp", "unity_lean_mcp_process.pid"));

    private static string CreateProjectRoot()
    {
        string projectRoot = Path.Combine(
            Path.GetTempPath(),
            "unity_process_identity_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(projectRoot, "Temp"));
        return projectRoot;
    }

    private static void DeleteProjectRoot(string projectRoot)
    {
        try { Directory.Delete(projectRoot, recursive: true); } catch { }
    }
}
