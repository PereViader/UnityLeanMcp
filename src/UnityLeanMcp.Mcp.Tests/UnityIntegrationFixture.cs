using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace UnityLeanMcp.Mcp.Tests;

[CollectionDefinition("UnityIntegration", DisableParallelization = true)]
public class UnityIntegrationCollection : ICollectionFixture<UnityIntegrationFixture>
{
}

public class UnityIntegrationFixture : IAsyncLifetime
{
    private string _repoRoot = null!;
    private string _unityRoot = null!;
    private string _dummyTestPath = null!;
    private string _dummyTestMetaPath = null!;
    private string _backupDummyTestPath = null!;
    private string _backupDummyTestMetaPath = null!;
    private string _tempDir = null!;
    private McpTestClient? _sharedClient;

    public string RepoRoot => _repoRoot;
    public string UnityRoot => _unityRoot;
    public McpTestClient SharedClient => _sharedClient ?? throw new InvalidOperationException("Shared McpTestClient is not initialized.");

    public async Task InitializeAsync()
    {
        _repoRoot = McpTestClient.GetRepoRoot();
        _unityRoot = McpTestClient.GetUnityProjectRoot();
        _dummyTestPath = Path.Combine(_unityRoot, "Assets", "Tests", "Editor", "DummyTest.cs");
        _dummyTestMetaPath = Path.Combine(_unityRoot, "Assets", "Tests", "Editor", "DummyTest.cs.meta");
        _tempDir = Path.Combine(Path.GetTempPath(), "UnityLeanMcp_TestBackup", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        _backupDummyTestPath = Path.Combine(_tempDir, "DummyTest.cs.bak");
        _backupDummyTestMetaPath = Path.Combine(_tempDir, "DummyTest.cs.meta.bak");

        // Take backup of original DummyTest.cs
        if (File.Exists(_dummyTestPath))
        {
            File.Copy(_dummyTestPath, _backupDummyTestPath, true);
        }
        if (File.Exists(_dummyTestMetaPath))
        {
            File.Copy(_dummyTestMetaPath, _backupDummyTestMetaPath, true);
        }

        // Always publish the Release server used by the Unity package. Reusing
        // an existing artifact can run integration tests against stale code.
        await PublishMcpServerAsync();

        // Ensure Unity is started and ready using shared client
        _sharedClient = new McpTestClient(_unityRoot);
        await _sharedClient.InitializeAsync();
        var startRes = await _sharedClient.CallToolAsync("unity_refresh", timeout: TimeSpan.FromSeconds(120));
        if (startRes.IsError)
        {
            throw new InvalidOperationException($"Failed to start Unity for integration tests: {startRes.Text}");
        }
    }

    private static async Task PublishMcpServerAsync()
    {
        string mcpDir = Path.Combine(
            McpTestClient.GetUnityProjectRoot(),
            "Packages",
            "com.pereviader.unityleanmcp",
            "MCP~");
        string publishedDll = Path.Combine(mcpDir, "UnityLeanMcp.Mcp.dll");

        PreparePublishDirectory(mcpDir);

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = "publish src/UnityLeanMcp.Mcp/UnityLeanMcp.Mcp.csproj -c Release -f net10.0 -o src/UnityLeanMcp.Unity3d/Packages/com.pereviader.unityleanmcp/MCP~",
            WorkingDirectory = McpTestClient.GetRepoRoot(),
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var proc = System.Diagnostics.Process.Start(psi)!;
        await proc.WaitForExitAsync();

        CleanupStaleOldFiles(mcpDir);

        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"dotnet publish failed with exit code {proc.ExitCode}. The integration tests cannot use an unverified MCP server artifact.");
        }

        if (!File.Exists(publishedDll))
        {
            throw new FileNotFoundException(
                "dotnet publish completed successfully but did not produce the package MCP server DLL.",
                publishedDll);
        }
    }

    private static void PreparePublishDirectory(string mcpDir)
    {
        if (!Directory.Exists(mcpDir))
        {
            return;
        }

        CleanupStaleOldFiles(mcpDir);

        foreach (string candidate in Directory.EnumerateFiles(mcpDir, "*.*"))
        {
            if (candidate.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                candidate.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase))
            {
                bool isLocked = false;
                try
                {
                    using (File.Open(candidate, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    {
                    }
                }
                catch
                {
                    isLocked = true;
                }

                if (isLocked)
                {
                    string oldTarget = candidate + ".old";
                    try
                    {
                        if (File.Exists(oldTarget))
                        {
                            File.Delete(oldTarget);
                        }
                        File.Move(candidate, oldTarget);
                    }
                    catch
                    {
                        try
                        {
                            File.Move(candidate, candidate + ".old." + Guid.NewGuid().ToString("N"));
                        }
                        catch
                        {
                            // Best-effort rename per LEARNINGS.md
                        }
                    }
                }
            }
        }
    }

    private static void CleanupStaleOldFiles(string mcpDir)
    {
        if (!Directory.Exists(mcpDir))
        {
            return;
        }

        foreach (string oldFile in Directory.EnumerateFiles(mcpDir, "*.old*"))
        {
            try
            {
                File.Delete(oldFile);
            }
            catch
            {
                // Ignored: still held by running host process
            }
        }
    }

    public async Task<IAsyncDisposable> UseFixtureAsync(string testCaseName)
    {
        string fixtureSource = Path.Combine(_repoRoot, "src", "UnityLeanMcp.Mcp.Tests", "Fixtures", testCaseName, "DummyTest.cs");
        if (!File.Exists(fixtureSource))
        {
            fixtureSource = Path.Combine(AppContext.BaseDirectory, "Fixtures", testCaseName, "DummyTest.cs");
        }

        if (File.Exists(fixtureSource))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_dummyTestPath)!);
            File.Copy(fixtureSource, _dummyTestPath, true);
            File.SetLastWriteTimeUtc(_dummyTestPath, DateTime.UtcNow);

            // An external file copy may not be observed by Unity's asynchronous
            // projectChanged watcher before the next tool request. Explicitly
            // refresh so the fixture always tests the source just copied rather
            // than whichever assembly Unity compiled previously. A compilation
            // error is expected for some fixtures, so the result is deliberately
            // allowed to be an MCP error.
            await SharedClient.CallToolAsync("unity_refresh");
        }

        return new DummyTestScope(this);
    }

    public void RestoreOriginalDummyTest()
    {
        try
        {
            if (File.Exists(_backupDummyTestPath))
            {
                File.Copy(_backupDummyTestPath, _dummyTestPath, true);
                File.SetLastWriteTimeUtc(_dummyTestPath, DateTime.UtcNow);
            }
            else if (File.Exists(_dummyTestPath))
            {
                File.Delete(_dummyTestPath);
            }

            if (File.Exists(_backupDummyTestMetaPath))
            {
                File.Copy(_backupDummyTestMetaPath, _dummyTestMetaPath, true);
            }
            else if (File.Exists(_dummyTestMetaPath))
            {
                File.Delete(_dummyTestMetaPath);
            }
        }
        catch { }
    }

    public async Task DisposeAsync()
    {
        RestoreOriginalDummyTest();

        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch { }

        // Stop Unity instance cleanly after all tests finish
        if (_sharedClient != null)
        {
            try
            {
                await _sharedClient.CallToolAsync("unity_stop", timeout: TimeSpan.FromSeconds(15));
                await _sharedClient.DisposeAsync();
            }
            catch { }
        }
    }

    private sealed class DummyTestScope : IAsyncDisposable
    {
        private readonly UnityIntegrationFixture _fixture;
        private bool _disposed;

        public DummyTestScope(UnityIntegrationFixture fixture)
        {
            _fixture = fixture;
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            _fixture.RestoreOriginalDummyTest();

            try
            {
                await _fixture.SharedClient.CallToolAsync("unity_refresh");
            }
            catch { }
        }
    }
}
