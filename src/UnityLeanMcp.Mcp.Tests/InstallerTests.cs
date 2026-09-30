using static UnityLeanMcp.McpConfigurationWriter;
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace UnityLeanMcp.Mcp.Tests;

[Trait("Category", "Unit")]
public class InstallerTests
{
    [Fact]
    public void UpdateOrWriteMcpConfig_PreservesIsoLookingStringsExactly()
    {
        string directory = Path.Combine(Path.GetTempPath(), "mcp_dates_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "mcp.json");
        string[] values = { "2026-10-01T12:34:56.000Z", "2026-10-01T12:34:56+02:00" };
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(new { inputs = values }));

            UpdateOrWriteMcpConfig(path, directory);

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal(values, document.RootElement.GetProperty("inputs").EnumerateArray()
                .Select(value => value.GetString()).ToArray());
            Assert.True(document.RootElement.GetProperty("mcpServers").TryGetProperty("unity-lean-mcp", out _));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("{\"inputs\": [\"keep\"],")]
    [InlineData("{\"inputs\": [\"keep\"]} {\"unexpected\": true}")]
    public void UpdateOrWriteMcpConfig_InvalidJsonLeavesOriginalBytesUnchanged(string original)
    {
        string directory = Path.Combine(Path.GetTempPath(), "mcp_invalid_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "mcp.json");
        try
        {
            File.WriteAllText(path, original);
            byte[] originalBytes = File.ReadAllBytes(path);

            Assert.NotNull(Record.Exception(() => UpdateOrWriteMcpConfig(path, directory)));

            Assert.Equal(originalBytes, File.ReadAllBytes(path));
            Assert.Equal(new[] { path }, Directory.GetFiles(directory));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void FindRepositoryRoot_WhenInsideNestedFolder_FindsGitDirectory()
    {
        string tempBase = Path.Combine(Path.GetTempPath(), "test_git_root_" + Guid.NewGuid().ToString("N"));
        try
        {
            string gitDir = Path.Combine(tempBase, ".git");
            Directory.CreateDirectory(gitDir);

            string assetsDir = Path.Combine(tempBase, "unity_project", "Assets");
            Directory.CreateDirectory(assetsDir);

            string detectedRoot = FindRepositoryRoot(assetsDir);
            Assert.Equal(Path.GetFullPath(tempBase), Path.GetFullPath(detectedRoot));
        }
        finally
        {
            if (Directory.Exists(tempBase)) Directory.Delete(tempBase, true);
        }
    }

    [Fact]
    public void FindRepositoryRoot_WhenGitIsAFile_Worktree_FindsDirectory()
    {
        string tempBase = Path.Combine(Path.GetTempPath(), "test_git_worktree_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempBase);
            string gitFile = Path.Combine(tempBase, ".git");
            File.WriteAllText(gitFile, "gitdir: /path/to/main/repo/.git/worktrees/branch");

            string assetsDir = Path.Combine(tempBase, "unity_project", "Assets");
            Directory.CreateDirectory(assetsDir);

            string detectedRoot = FindRepositoryRoot(assetsDir);
            Assert.Equal(Path.GetFullPath(tempBase), Path.GetFullPath(detectedRoot));
        }
        finally
        {
            if (Directory.Exists(tempBase)) Directory.Delete(tempBase, true);
        }
    }

    [Fact]
    public void FindRepositoryRoot_WhenNoGitDirectory_FallsBackToParent()
    {
        string tempBase = Path.Combine(Path.GetTempPath(), "test_no_git_" + Guid.NewGuid().ToString("N"));
        try
        {
            string assetsDir = Path.Combine(tempBase, "unity_project", "Assets");
            Directory.CreateDirectory(assetsDir);

            string detectedRoot = FindRepositoryRoot(assetsDir);
            string expectedParent = Path.GetFullPath(Path.Combine(assetsDir, ".."));
            Assert.Equal(expectedParent, Path.GetFullPath(detectedRoot));
        }
        finally
        {
            if (Directory.Exists(tempBase)) Directory.Delete(tempBase, true);
        }
    }

    [Fact]
    public void UpdateOrWriteMcpConfig_WhenFileDoesNotExist_CreatesNewFileWithMcpServers()
    {
        string tempBase = Path.Combine(Path.GetTempPath(), "test_mcp_config_" + Guid.NewGuid().ToString("N"));
        try
        {
            string configFile = Path.Combine(tempBase, ".cursor", "mcp.json");
            string mcpDir = "C:/MyPackage/MCP~/";

            UpdateOrWriteMcpConfig(configFile, mcpDir);

            Assert.True(File.Exists(configFile));
            string content = File.ReadAllText(configFile);
            using var doc = JsonDocument.Parse(content);
            var servers = doc.RootElement.GetProperty("mcpServers");
            var server = servers.GetProperty("unity-lean-mcp");

            Assert.Equal("dotnet", server.GetProperty("command").GetString());
            var args = server.GetProperty("args").EnumerateArray().Select(a => a.GetString()).ToList();
            Assert.Equal(new[] { "UnityLeanMcp.Mcp.dll" }, args);
            Assert.Equal(mcpDir, server.GetProperty("cwd").GetString());
        }
        finally
        {
            if (Directory.Exists(tempBase)) Directory.Delete(tempBase, true);
        }
    }

    [Fact]
    public void UpdateOrWriteMcpConfig_WhenVsCodeConfigDoesNotExist_CreatesNewFileWithServers()
    {
        string tempBase = Path.Combine(Path.GetTempPath(), "test_mcp_config_" + Guid.NewGuid().ToString("N"));
        try
        {
            string configFile = Path.Combine(tempBase, ".vscode", "mcp.json");
            string mcpDir = "C:/MyPackage/MCP~/";

            UpdateOrWriteMcpConfig(configFile, mcpDir);

            Assert.True(File.Exists(configFile));
            string content = File.ReadAllText(configFile);
            using var doc = JsonDocument.Parse(content);
            var servers = doc.RootElement.GetProperty("servers");
            var server = servers.GetProperty("unity-lean-mcp");

            Assert.Equal("dotnet", server.GetProperty("command").GetString());
            var args = server.GetProperty("args").EnumerateArray().Select(a => a.GetString()).ToList();
            Assert.Equal(new[] { "UnityLeanMcp.Mcp.dll" }, args);
            Assert.Equal(mcpDir, server.GetProperty("cwd").GetString());
        }
        finally
        {
            if (Directory.Exists(tempBase)) Directory.Delete(tempBase, true);
        }
    }

    [Fact]
    public void UpdateOrWriteMcpConfig_WhenFileIsEmpty_OverwritesWithNewContent()
    {
        string tempBase = Path.Combine(Path.GetTempPath(), "test_mcp_empty_" + Guid.NewGuid().ToString("N"));
        try
        {
            string configFile = Path.Combine(tempBase, ".cursor", "mcp.json");
            Directory.CreateDirectory(Path.GetDirectoryName(configFile)!);
            File.WriteAllText(configFile, "   \n\t  ");

            string mcpDir = "C:/MyPackage/MCP~/";
            UpdateOrWriteMcpConfig(configFile, mcpDir);

            string content = File.ReadAllText(configFile);
            using var doc = JsonDocument.Parse(content);
            var servers = doc.RootElement.GetProperty("mcpServers");
            Assert.True(servers.TryGetProperty("unity-lean-mcp", out _));
        }
        finally
        {
            if (Directory.Exists(tempBase)) Directory.Delete(tempBase, true);
        }
    }

    [Fact]
    public void UpdateOrWriteMcpConfig_WhenFileDoesNotContainMcpServers_OverwritesWithNewContent()
    {
        string tempBase = Path.Combine(Path.GetTempPath(), "test_mcp_other_" + Guid.NewGuid().ToString("N"));
        try
        {
            string configFile = Path.Combine(tempBase, ".cursor", "mcp.json");
            Directory.CreateDirectory(Path.GetDirectoryName(configFile)!);
            File.WriteAllText(configFile, "{\n  \"otherConfig\": true\n}");

            string mcpDir = "C:/MyPackage/MCP~/";
            UpdateOrWriteMcpConfig(configFile, mcpDir);

            string content = File.ReadAllText(configFile);
            using var doc = JsonDocument.Parse(content);
            var servers = doc.RootElement.GetProperty("mcpServers");
            Assert.True(servers.TryGetProperty("unity-lean-mcp", out _));
        }
        finally
        {
            if (Directory.Exists(tempBase)) Directory.Delete(tempBase, true);
        }
    }

    [Fact]
    public void UpdateOrWriteMcpConfig_WhenFileContainsMcpServersWithoutUnity_InsertsUnityLeanMcpPreservingExisting()
    {
        string tempBase = Path.Combine(Path.GetTempPath(), "test_mcp_insert_" + Guid.NewGuid().ToString("N"));
        try
        {
            string configFile = Path.Combine(tempBase, ".cursor", "mcp.json");
            Directory.CreateDirectory(Path.GetDirectoryName(configFile)!);
            string existingContent =
                "{\n" +
                "  \"mcpServers\": {\n" +
                "    \"github\": {\n" +
                "      \"command\": \"gh\"\n" +
                "    }\n" +
                "  }\n" +
                "}";
            File.WriteAllText(configFile, existingContent);

            string mcpDir = "C:/MyPackage/MCP~/";
            UpdateOrWriteMcpConfig(configFile, mcpDir);

            string content = File.ReadAllText(configFile);
            using var doc = JsonDocument.Parse(content);
            var servers = doc.RootElement.GetProperty("mcpServers");

            Assert.True(servers.TryGetProperty("github", out _));
            Assert.True(servers.TryGetProperty("unity-lean-mcp", out var server));
            Assert.Equal("dotnet", server.GetProperty("command").GetString());
            Assert.Equal(mcpDir, server.GetProperty("cwd").GetString());
        }
        finally
        {
            if (Directory.Exists(tempBase)) Directory.Delete(tempBase, true);
        }
    }

    [Fact]
    public void UpdateOrWriteMcpConfig_WhenFileContainsServersWithoutUnity_InsertsUnityLeanMcpPreservingExisting()
    {
        string tempBase = Path.Combine(Path.GetTempPath(), "test_mcp_insert_vscode_" + Guid.NewGuid().ToString("N"));
        try
        {
            string configFile = Path.Combine(tempBase, ".vscode", "mcp.json");
            Directory.CreateDirectory(Path.GetDirectoryName(configFile)!);
            string existingContent =
                "{\n" +
                "  \"servers\": {\n" +
                "    \"custom-server\": {\n" +
                "      \"command\": \"custom\"\n" +
                "    }\n" +
                "  }\n" +
                "}";
            File.WriteAllText(configFile, existingContent);

            string mcpDir = "C:/MyPackage/MCP~/";
            UpdateOrWriteMcpConfig(configFile, mcpDir);

            string content = File.ReadAllText(configFile);
            using var doc = JsonDocument.Parse(content);
            var servers = doc.RootElement.GetProperty("servers");

            Assert.True(servers.TryGetProperty("custom-server", out _));
            Assert.True(servers.TryGetProperty("unity-lean-mcp", out var server));
            Assert.Equal("dotnet", server.GetProperty("command").GetString());
            Assert.Equal(mcpDir, server.GetProperty("cwd").GetString());
        }
        finally
        {
            if (Directory.Exists(tempBase)) Directory.Delete(tempBase, true);
        }
    }

    [Fact]
    public void UpdateOrWriteMcpConfig_WhenFileContainsExistingUnityLeanMcp_ReplacesBlockPreservingOtherServers()
    {
        string tempBase = Path.Combine(Path.GetTempPath(), "test_mcp_replace_" + Guid.NewGuid().ToString("N"));
        try
        {
            string configFile = Path.Combine(tempBase, ".cursor", "mcp.json");
            Directory.CreateDirectory(Path.GetDirectoryName(configFile)!);

            string existingContent =
                "{\n" +
                "  \"mcpServers\": {\n" +
                "    \"unity-lean-mcp\": {\n" +
                "      \"command\": \"dotnet\",\n" +
                "      \"args\": [\"old_path/UnityLeanMcp.Mcp.dll\"],\n" +
                "      \"cwd\": \"C:/OldPackagePath/MCP~/\"\n" +
                "    },\n" +
                "    \"github\": {\n" +
                "      \"command\": \"gh\"\n" +
                "    }\n" +
                "  }\n" +
                "}";
            File.WriteAllText(configFile, existingContent);

            string mcpDir = "C:/NewPackagePath/MCP~/";
            UpdateOrWriteMcpConfig(configFile, mcpDir);

            string content = File.ReadAllText(configFile);
            using var doc = JsonDocument.Parse(content);
            var servers = doc.RootElement.GetProperty("mcpServers");

            Assert.True(servers.TryGetProperty("github", out _));
            Assert.True(servers.TryGetProperty("unity-lean-mcp", out var server));
            Assert.Equal(mcpDir, server.GetProperty("cwd").GetString());
            Assert.Equal("UnityLeanMcp.Mcp.dll", server.GetProperty("args")[0].GetString());
        }
        finally
        {
            if (Directory.Exists(tempBase)) Directory.Delete(tempBase, true);
        }
    }


    [Fact]
    public void AppendCodexMcpConfig_WhenFileDoesNotExist_CreatesFile()
    {
        string tempBase = Path.Combine(Path.GetTempPath(), "test_codex_" + Guid.NewGuid().ToString("N"));
        try
        {
            string configFile = Path.Combine(tempBase, ".codex", "config.toml");
            string mcpDir = "C:/Packages/UnityLeanMcp/MCP~/";

            AppendCodexMcpConfig(configFile, mcpDir);

            Assert.True(File.Exists(configFile));
            string content = File.ReadAllText(configFile);
            Assert.Contains("[mcp_servers.unity-lean-mcp]", content);
            Assert.Contains("command = \"dotnet\"", content);
            Assert.Contains("args = [\"UnityLeanMcp.Mcp.dll\"]", content);
            Assert.Contains($"cwd = \"{mcpDir}\"", content);
        }
        finally
        {
            if (Directory.Exists(tempBase)) Directory.Delete(tempBase, true);
        }
    }

    [Fact]
    public void AppendCodexMcpConfig_WhenFileExists_AppendsAtEnd()
    {
        string tempBase = Path.Combine(Path.GetTempPath(), "test_codex_" + Guid.NewGuid().ToString("N"));
        try
        {
            string configFile = Path.Combine(tempBase, ".codex", "config.toml");
            Directory.CreateDirectory(Path.GetDirectoryName(configFile)!);
            File.WriteAllText(configFile, "[model]\nname = \"o3-mini\"\n");

            string mcpDir = "C:/Packages/UnityLeanMcp/MCP~/";
            AppendCodexMcpConfig(configFile, mcpDir);

            string content = File.ReadAllText(configFile);
            Assert.StartsWith("[model]\nname = \"o3-mini\"", content);
            Assert.Contains("[mcp_servers.unity-lean-mcp]", content);
            Assert.Contains($"cwd = \"{mcpDir}\"", content);
        }
        finally
        {
            if (Directory.Exists(tempBase)) Directory.Delete(tempBase, true);
        }
    }


    [Fact]
    public void AppendCodexMcpConfig_WhenExactSameConfig_LeavesContentUnchanged()
    {
        string tempBase = Path.Combine(Path.GetTempPath(), "test_codex_" + Guid.NewGuid().ToString("N"));
        try
        {
            string configFile = Path.Combine(tempBase, ".codex", "config.toml");
            Directory.CreateDirectory(Path.GetDirectoryName(configFile)!);
            string mcpDir = "C:/SamePath/MCP~/";
            AppendCodexMcpConfig(configFile, mcpDir);
            string original = File.ReadAllText(configFile);

            // Re-run with same configuration
            AppendCodexMcpConfig(configFile, mcpDir);
            string second = File.ReadAllText(configFile);

            Assert.Equal(original, second);
        }
        finally
        {
            if (Directory.Exists(tempBase)) Directory.Delete(tempBase, true);
        }
    }

    [Fact]
    public void TrackedMcpConfig_VsCode_UsesWorkspaceFolderVariableAndMcpDll()
    {
        string repositoryRoot = FindRepositoryRoot(AppContext.BaseDirectory);
        string configPath = Path.Combine(repositoryRoot, ".vscode", "mcp.json");

        Assert.True(File.Exists(configPath), $"Expected tracked MCP config at {configPath}");

        using var document = JsonDocument.Parse(File.ReadAllText(configPath));
        JsonElement server = document.RootElement
            .GetProperty("servers")
            .GetProperty("unity-lean-mcp");

        Assert.Equal("dotnet", server.GetProperty("command").GetString());
        string[] args = server.GetProperty("args").EnumerateArray().Select(value => value.GetString()!).ToArray();
        Assert.Equal(new[] { "UnityLeanMcp.Mcp.dll" }, args);
        Assert.True(server.TryGetProperty("cwd", out JsonElement cwdElement));
        string cwd = cwdElement.GetString()!;
        Assert.StartsWith("${workspaceFolder}/", cwd);
        Assert.EndsWith("/MCP~/", cwd);
        Assert.DoesNotContain("C:/Users/perev/", File.ReadAllText(configPath), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TrackedMcpConfig_Claude_UsesClaudeProjectDirVariableAndMcpDll()
    {
        string repositoryRoot = FindRepositoryRoot(AppContext.BaseDirectory);
        string configPath = Path.Combine(repositoryRoot, ".mcp.json");

        Assert.True(File.Exists(configPath), $"Expected tracked MCP config at {configPath}");

        using var document = JsonDocument.Parse(File.ReadAllText(configPath));
        JsonElement server = document.RootElement
            .GetProperty("mcpServers")
            .GetProperty("unity-lean-mcp");

        Assert.Equal("dotnet", server.GetProperty("command").GetString());
        string[] args = server.GetProperty("args").EnumerateArray().Select(value => value.GetString()!).ToArray();
        Assert.Equal(new[] { "UnityLeanMcp.Mcp.dll" }, args);
        Assert.True(server.TryGetProperty("cwd", out JsonElement cwdElement));
        string cwd = cwdElement.GetString()!;
        Assert.StartsWith("${CLAUDE_PROJECT_DIR:-.}/", cwd);
        Assert.EndsWith("/MCP~/", cwd);
        Assert.DoesNotContain("C:/Users/perev/", File.ReadAllText(configPath), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GeneratedMcpConfig_Cursor_UsesAbsoluteCwdAndMcpDll()
    {
        string tempBase = Path.Combine(Path.GetTempPath(), "cursor_config_" + Guid.NewGuid().ToString("N"));
        string repositoryRoot = Path.Combine(tempBase, "repository");
        string mcpDirectory = Path.Combine(repositoryRoot, "src", "UnityLeanMcp.Unity3d", "Packages", "com.example.mcp", "MCP~");
        string configPath = Path.Combine(repositoryRoot, ".cursor", "mcp.json");

        Directory.CreateDirectory(mcpDirectory);
        try
        {
            UpdateOrWriteMcpConfig(configPath, mcpDirectory);

            using var document = JsonDocument.Parse(File.ReadAllText(configPath));
            JsonElement server = document.RootElement
                .GetProperty("mcpServers")
                .GetProperty("unity-lean-mcp");

            Assert.Equal("dotnet", server.GetProperty("command").GetString());
            string[] args = server.GetProperty("args").EnumerateArray().Select(value => value.GetString()!).ToArray();
            Assert.Equal(new[] { "UnityLeanMcp.Mcp.dll" }, args);
            Assert.True(server.TryGetProperty("cwd", out JsonElement cwdElement));
            string cwd = cwdElement.GetString()!;
            Assert.True(Path.IsPathRooted(cwd));
            Assert.EndsWith("/MCP~/", cwd);
            Assert.True(Directory.Exists(cwd.TrimEnd('/')));
        }
        finally
        {
            if (Directory.Exists(tempBase)) Directory.Delete(tempBase, true);
        }
    }

    [Fact]
    public void TrackedMcpConfig_Codex_UsesAbsoluteCwdAndTimeout()
    {
        string repositoryRoot = FindRepositoryRoot(AppContext.BaseDirectory);
        string configPath = Path.Combine(repositoryRoot, ".codex", "config.toml");
        if (!File.Exists(configPath))
        {
            return;
        }

        string content = File.ReadAllText(configPath);
        Assert.Contains("[mcp_servers.unity-lean-mcp]", content);
        Assert.Contains("command = \"dotnet\"", content);
        Assert.Contains("args = [\"UnityLeanMcp.Mcp.dll\"]", content);
        Assert.Contains("tool_timeout_sec = 1800", content);
        Assert.Contains("cwd = ", content);
    }

    [Fact]
    public void McpConfigurationPaths_TryGetWorkspaceRootVariable_IdentifiesSupportedClients()
    {
        Assert.True(UnityLeanMcp.McpConfigurationPaths.TryGetWorkspaceRootVariable(".vscode/mcp.json", out string vsVar));
        Assert.Equal("${workspaceFolder}", vsVar);

        Assert.True(UnityLeanMcp.McpConfigurationPaths.TryGetWorkspaceRootVariable("C:/repo/.vscode/mcp.json", out string vsVarAbs));
        Assert.Equal("${workspaceFolder}", vsVarAbs);

        Assert.True(UnityLeanMcp.McpConfigurationPaths.TryGetWorkspaceRootVariable(".mcp.json", out string claudeVar));
        Assert.Equal("${CLAUDE_PROJECT_DIR:-.}", claudeVar);

        Assert.True(UnityLeanMcp.McpConfigurationPaths.TryGetWorkspaceRootVariable("C:/repo/.mcp.json", out string claudeVarAbs));
        Assert.Equal("${CLAUDE_PROJECT_DIR:-.}", claudeVarAbs);

        Assert.False(UnityLeanMcp.McpConfigurationPaths.TryGetWorkspaceRootVariable(".cursor/mcp.json", out _));
        Assert.False(UnityLeanMcp.McpConfigurationPaths.TryGetWorkspaceRootVariable(".agents/plugins/unity-lean-mcp/mcp_config.json", out _));
        Assert.False(UnityLeanMcp.McpConfigurationPaths.TryGetWorkspaceRootVariable(".codex/config.toml", out _));
    }

    [Fact]
    public void McpConfigurationPaths_TryGetWorkspaceRelativeMcpPath_WhenInsideRepository_ReturnsFormattedVariablePath()
    {
        string repositoryRoot = Path.Combine(Path.GetTempPath(), "mcp_paths_" + Guid.NewGuid().ToString("N"));
        string mcpDirectory = Path.Combine(repositoryRoot, "src", "UnityProject", "Packages", "com.example.mcp", "MCP~") + "/";

        try
        {
            bool vsSuccess = UnityLeanMcp.McpConfigurationPaths.TryGetWorkspaceRelativeMcpPath(
                repositoryRoot,
                mcpDirectory,
                Path.Combine(repositoryRoot, ".vscode", "mcp.json"),
                out string vsPath);
            Assert.True(vsSuccess);
            Assert.Equal("${workspaceFolder}/src/UnityProject/Packages/com.example.mcp/MCP~/", vsPath);

            bool claudeSuccess = UnityLeanMcp.McpConfigurationPaths.TryGetWorkspaceRelativeMcpPath(
                repositoryRoot,
                mcpDirectory,
                Path.Combine(repositoryRoot, ".mcp.json"),
                out string claudePath);
            Assert.True(claudeSuccess);
            Assert.Equal("${CLAUDE_PROJECT_DIR:-.}/src/UnityProject/Packages/com.example.mcp/MCP~/", claudePath);

            bool cursorSuccess = UnityLeanMcp.McpConfigurationPaths.TryGetWorkspaceRelativeMcpPath(
                repositoryRoot,
                mcpDirectory,
                Path.Combine(repositoryRoot, ".cursor", "mcp.json"),
                out _);
            Assert.False(cursorSuccess);
        }
        finally
        {
            if (Directory.Exists(repositoryRoot)) Directory.Delete(repositoryRoot, true);
        }
    }

    [Fact]
    public void McpConfigurationPaths_TryGetWorkspaceRelativeMcpPath_WhenOutsideRepository_ReturnsFalse()
    {
        string repositoryRoot = Path.Combine(Path.GetTempPath(), "mcp_paths_" + Guid.NewGuid().ToString("N"));
        string externalPackage = Path.Combine(Path.GetTempPath(), "mcp_package_" + Guid.NewGuid().ToString("N"), "MCP~") + "/";

        bool success = UnityLeanMcp.McpConfigurationPaths.TryGetWorkspaceRelativeMcpPath(
            repositoryRoot,
            externalPackage,
            Path.Combine(repositoryRoot, ".vscode", "mcp.json"),
            out _);

        Assert.False(success);
    }

    [Fact]
    public void McpConfigurationPaths_WhenPackageIsInsideRepository_ReturnsPortablePaths()
    {
        string repositoryRoot = Path.Combine(Path.GetTempPath(), "mcp_paths_" + Guid.NewGuid().ToString("N"));
        string projectRoot = Path.Combine(repositoryRoot, "src", "UnityProject");
        string mcpDirectory = Path.Combine(projectRoot, "Packages", "com.example.mcp", "MCP~");

        try
        {
            bool portable = UnityLeanMcp.McpConfigurationPaths.TryGetPortablePaths(
                repositoryRoot,
                mcpDirectory,
                projectRoot,
                out string relativeMcpDll,
                out string relativeProjectRoot);

            Assert.True(portable);
            Assert.Equal("src/UnityProject/Packages/com.example.mcp/MCP~/UnityLeanMcp.Mcp.dll", relativeMcpDll);
            Assert.Equal("src/UnityProject", relativeProjectRoot);
        }
        finally
        {
            if (Directory.Exists(repositoryRoot)) Directory.Delete(repositoryRoot, true);
        }
    }

    [Fact]
    public void McpConfigurationPaths_WhenProjectIsRepositoryRoot_UsesDotPath()
    {
        string repositoryRoot = Path.Combine(Path.GetTempPath(), "mcp_paths_" + Guid.NewGuid().ToString("N"));
        string mcpDirectory = Path.Combine(repositoryRoot, "Packages", "com.example.mcp", "MCP~");

        bool portable = UnityLeanMcp.McpConfigurationPaths.TryGetPortablePaths(
            repositoryRoot,
            mcpDirectory,
            repositoryRoot,
            out string relativeMcpDll,
            out string relativeProjectRoot);

        Assert.True(portable);
        Assert.Equal("Packages/com.example.mcp/MCP~/UnityLeanMcp.Mcp.dll", relativeMcpDll);
        Assert.Equal(".", relativeProjectRoot);
    }

    [Fact]
    public void McpConfigurationPaths_WhenPackageIsOutsideRepository_RequestsInstallerSpecificPaths()
    {
        string repositoryRoot = Path.Combine(Path.GetTempPath(), "mcp_paths_" + Guid.NewGuid().ToString("N"));
        string externalPackage = Path.Combine(Path.GetTempPath(), "mcp_package_" + Guid.NewGuid().ToString("N"), "MCP~");

        bool portable = UnityLeanMcp.McpConfigurationPaths.TryGetPortablePaths(
            repositoryRoot,
            externalPackage,
            repositoryRoot,
            out _,
            out _);

        Assert.False(portable);
    }

    [Fact]
    public void FindMcpDirectory_WhenMcpUpperExists_ReturnsUpperMcp()
    {
        string tempPackage = Path.Combine(Path.GetTempPath(), "test_pkg_" + Guid.NewGuid().ToString("N"));
        string mcpDir = Path.Combine(tempPackage, "MCP~");
        Directory.CreateDirectory(mcpDir);
        try
        {
            string resolved = UnityLeanMcp.McpConfigurationPaths.FindMcpDirectory(tempPackage);
            Assert.Equal(mcpDir.Replace('\\', '/') + "/", resolved);
        }
        finally
        {
            if (Directory.Exists(tempPackage)) Directory.Delete(tempPackage, true);
        }
    }

    [Fact]
    public void FindMcpDirectory_WhenOnlyMcpLowerExists_ReturnsLowerMcp()
    {
        string tempPackage = Path.Combine(Path.GetTempPath(), "test_pkg_" + Guid.NewGuid().ToString("N"));
        string mcpLowerDir = Path.Combine(tempPackage, "mcp~");
        Directory.CreateDirectory(mcpLowerDir);
        try
        {
            string resolved = UnityLeanMcp.McpConfigurationPaths.FindMcpDirectory(tempPackage);
            // On a case-insensitive filesystem the preferred uppercase candidate
            // resolves to this same directory. Detect filesystem behavior, not OS name.
            string candidate = Directory.Exists(Path.Combine(tempPackage, "MCP~"))
                ? Path.Combine(tempPackage, "MCP~") : mcpLowerDir;
            string expected = UnityLeanMcp.McpConfigurationPaths.NormalizeDirectoryPath(candidate);
            Assert.Equal(expected, resolved);
        }
        finally
        {
            if (Directory.Exists(tempPackage)) Directory.Delete(tempPackage, true);
        }
    }

    [Fact]
    public void FindMcpDirectory_WhenNeitherExists_ReturnsNormalizedDefaultMcp()
    {
        string tempPackage = Path.Combine(Path.GetTempPath(), "test_pkg_" + Guid.NewGuid().ToString("N"));
        string expected = Path.Combine(tempPackage, "MCP~").Replace('\\', '/') + "/";

        string resolved = UnityLeanMcp.McpConfigurationPaths.FindMcpDirectory(tempPackage);
        Assert.Equal(expected, resolved);
    }

    [Fact]
    public void FindMcpDirectory_WhenNullOrEmpty_ReturnsNull()
    {
        Assert.Null(UnityLeanMcp.McpConfigurationPaths.FindMcpDirectory(null!));
        Assert.Null(UnityLeanMcp.McpConfigurationPaths.FindMcpDirectory("   "));
    }
}
