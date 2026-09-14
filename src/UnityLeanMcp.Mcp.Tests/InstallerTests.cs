using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace UnityLeanMcp.Mcp.Tests;

[Trait("Category", "Unit")]
public class InstallerTests
{
    private static string FindRepositoryRoot(string assetsPath)
    {
        var dir = new DirectoryInfo(assetsPath);
        while (dir != null)
        {
            string gitDir = Path.Combine(dir.FullName, ".git");
            if (Directory.Exists(gitDir) || File.Exists(gitDir))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }

        return Path.GetFullPath(Path.Combine(assetsPath, ".."));
    }

    private static void UpdateOrWriteMcpConfig(string configPath, string mcpDir, string rootKey = "mcpServers")
    {
        string dir = Path.GetDirectoryName(configPath)!;
        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string formattedMcpDir = mcpDir.Replace('\\', '/');
        if (!formattedMcpDir.EndsWith("/"))
        {
            formattedMcpDir += "/";
        }

        string serverJsonSnippet =
            "    \"unity-lean-mcp\": {\n" +
            "      \"command\": \"dotnet\",\n" +
            "      \"args\": [\n" +
            "        \"UnityLeanMcp.Mcp.dll\"\n" +
            "      ],\n" +
            $"      \"cwd\": \"{formattedMcpDir}\"\n" +
            "    }";

        string normalizedPath = configPath.Replace('\\', '/');
        string effectiveRootKey = rootKey;
        if (normalizedPath.EndsWith("/.vscode/mcp.json"))
        {
            effectiveRootKey = "servers";
        }

        if (!File.Exists(configPath))
        {
            string newContent =
                "{\n" +
                $"  \"{effectiveRootKey}\": {{\n" +
                serverJsonSnippet.TrimStart() + "\n" +
                "  }\n" +
                "}\n";
            File.WriteAllText(configPath, newContent, System.Text.Encoding.UTF8);
            return;
        }

        string existing = File.ReadAllText(configPath, System.Text.Encoding.UTF8).Trim();
        if (string.IsNullOrWhiteSpace(existing))
        {
            string newContent =
                "{\n" +
                $"  \"{effectiveRootKey}\": {{\n" +
                serverJsonSnippet.TrimStart() + "\n" +
                "  }\n" +
                "}\n";
            File.WriteAllText(configPath, newContent, System.Text.Encoding.UTF8);
            return;
        }

        // If unity-lean-mcp already exists, replace its block
        const string targetServerKey = "\"unity-lean-mcp\"";

        if (existing.Contains(targetServerKey))
        {
            int unityIndex = existing.IndexOf(targetServerKey, StringComparison.Ordinal);
            int openBrace = existing.IndexOf('{', unityIndex);
            if (openBrace != -1)
            {
                int depth = 1;
                int closeBrace = -1;
                for (int i = openBrace + 1; i < existing.Length; i++)
                {
                    if (existing[i] == '{') depth++;
                    else if (existing[i] == '}')
                    {
                        depth--;
                        if (depth == 0)
                        {
                            closeBrace = i;
                            break;
                        }
                    }
                }

                if (closeBrace != -1)
                {
                    string before = existing.Substring(0, unityIndex);
                    string after = existing.Substring(closeBrace + 1);
                    string updated = before + serverJsonSnippet.TrimStart() + after;
                    File.WriteAllText(configPath, updated, System.Text.Encoding.UTF8);
                    return;
                }
            }
        }

        int sectionIndex = existing.IndexOf($"\"{effectiveRootKey}\"", StringComparison.Ordinal);
        if (sectionIndex == -1 && effectiveRootKey == "servers")
        {
            sectionIndex = existing.IndexOf("\"mcpServers\"", StringComparison.Ordinal);
        }
        else if (sectionIndex == -1 && effectiveRootKey == "mcpServers")
        {
            sectionIndex = existing.IndexOf("\"servers\"", StringComparison.Ordinal);
        }

        if (sectionIndex != -1)
        {
            int openBrace = existing.IndexOf('{', sectionIndex);
            if (openBrace != -1)
            {
                string before = existing.Substring(0, openBrace + 1);
                string after = existing.Substring(openBrace + 1);
                string separator = after.TrimStart().StartsWith("}") ? "\n" : ",\n";
                string updated = before + "\n" + serverJsonSnippet + separator + after.TrimStart();
                File.WriteAllText(configPath, updated, System.Text.Encoding.UTF8);
                return;
            }
        }

        string fallbackContent =
            "{\n" +
            $"  \"{effectiveRootKey}\": {{\n" +
            serverJsonSnippet.TrimStart() + "\n" +
            "  }\n" +
            "}\n";
        File.WriteAllText(configPath, fallbackContent, System.Text.Encoding.UTF8);
    }

    private static void AppendCodexMcpConfig(string configPath, string mcpDir)
    {
        string dir = Path.GetDirectoryName(configPath)!;
        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string formattedMcpDir = mcpDir.Replace('\\', '/');
        if (!formattedMcpDir.EndsWith("/"))
        {
            formattedMcpDir += "/";
        }

        string codexTomlSnippet =
            "[mcp_servers.unity-lean-mcp]\n" +
            "command = \"dotnet\"\n" +
            "args = [\"UnityLeanMcp.Mcp.dll\"]\n" +
            $"cwd = \"{formattedMcpDir}\"\n";

        if (!File.Exists(configPath))
        {
            File.WriteAllText(configPath, codexTomlSnippet, System.Text.Encoding.UTF8);
            return;
        }

        string existing = File.ReadAllText(configPath, System.Text.Encoding.UTF8);
        const string targetHeader = "[mcp_servers.unity-lean-mcp]";
        int headerIndex = existing.IndexOf(targetHeader, StringComparison.Ordinal);

        if (headerIndex != -1)
        {
            int nextSectionIndex = -1;
            var nextMatch = System.Text.RegularExpressions.Regex.Match(
                existing.Substring(headerIndex + targetHeader.Length),
                @"(?m)^\[");
            if (nextMatch.Success)
            {
                nextSectionIndex = headerIndex + targetHeader.Length + nextMatch.Index;
            }

            string before = existing.Substring(0, headerIndex);
            string after = nextSectionIndex != -1 ? existing.Substring(nextSectionIndex) : string.Empty;

            var sbReplace = new System.Text.StringBuilder();
            sbReplace.Append(before);
            sbReplace.Append(codexTomlSnippet);
            if (!string.IsNullOrEmpty(after))
            {
                if (!sbReplace.ToString().EndsWith("\n\n"))
                {
                    sbReplace.AppendLine();
                }
                sbReplace.Append(after.TrimStart('\r', '\n'));
            }

            string newText = sbReplace.ToString();
            if (newText != existing)
            {
                File.WriteAllText(configPath, newText, System.Text.Encoding.UTF8);
            }
            return;
        }

        var sb = new System.Text.StringBuilder();
        sb.Append(existing);
        if (!existing.EndsWith("\n"))
        {
            sb.AppendLine();
        }
        sb.AppendLine();
        sb.Append(codexTomlSnippet);
        File.WriteAllText(configPath, sb.ToString(), System.Text.Encoding.UTF8);
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

    [Theory]
    [InlineData(".mcp.json", "mcpServers")]
    [InlineData(".cursor/mcp.json", "mcpServers")]
    [InlineData(".vscode/mcp.json", "servers")]
    public void TrackedMcpConfig_UsesCheckoutRelativeServerAndProjectPaths(string relativeConfigPath, string rootKey)
    {
        string repositoryRoot = FindRepositoryRoot(AppContext.BaseDirectory);
        string configPath = Path.Combine(repositoryRoot, relativeConfigPath.Replace('/', Path.DirectorySeparatorChar));

        Assert.True(File.Exists(configPath), $"Expected tracked MCP config at {configPath}");

        using var document = JsonDocument.Parse(File.ReadAllText(configPath));
        JsonElement server = document.RootElement
            .GetProperty(rootKey)
            .GetProperty("unity-lean-mcp");

        Assert.Equal("dotnet", server.GetProperty("command").GetString());
        string[] args = server.GetProperty("args").EnumerateArray().Select(value => value.GetString()!).ToArray();
        Assert.Equal(
            new[]
            {
                "src/UnityLeanMcp.Unity3d/Packages/com.pereviader.unityleanmcp/MCP~/UnityLeanMcp.Mcp.dll",
                "--project",
                "src/UnityLeanMcp.Unity3d"
            },
            args);
        Assert.False(server.TryGetProperty("cwd", out _));
        Assert.DoesNotContain("C:/Users/perev/", File.ReadAllText(configPath), StringComparison.OrdinalIgnoreCase);
        Assert.False(Path.IsPathRooted(args[0]));
        Assert.EndsWith("/MCP~/UnityLeanMcp.Mcp.dll", args[0], StringComparison.Ordinal);
        Assert.True(Directory.Exists(Path.Combine(repositoryRoot, args[2].Replace('/', Path.DirectorySeparatorChar))));
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
}
