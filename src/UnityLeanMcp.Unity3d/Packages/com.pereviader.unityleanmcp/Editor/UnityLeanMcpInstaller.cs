using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace UnityLeanMcp
{
    public static class UnityLeanMcpInstaller
    {
        [MenuItem("Tools/UnityLeanMcp/Install MCP Configurations")]
        private static void InstallFromMenu()
        {
            Install(true);
        }

        public static bool Install(bool showDialog)
        {
            try
            {
                string packagePath = FindPackagePath();
                if (string.IsNullOrEmpty(packagePath))
                {
                    Debug.LogError("[UnityLeanMcp] Could not find package information for assembly. Installation aborted.");
                    if (showDialog && !Application.isBatchMode)
                    {
                        EditorUtility.DisplayDialog("UnityLeanMcp Error", "Could not find package information for assembly. Installation aborted.", "OK");
                    }
                    return false;
                }

                string mcpDir = McpConfigurationPaths.FindMcpDirectory(packagePath);
                string dllPath = Path.Combine(mcpDir, "UnityLeanMcp.Mcp.dll");
                if (!File.Exists(dllPath))
                {
                    Debug.LogWarning($"[UnityLeanMcp] UnityLeanMcp.Mcp.dll was not found at '{dllPath}'. Ensure the MCP server binary is built or published.");
                }

                string assetsPath = Application.dataPath;
                string rootFolder = FindRepositoryRoot(assetsPath);

                // Ensure Antigravity plugin manifest
                string pluginJsonPath = Path.Combine(rootFolder, ".agents", "plugins", "unity-lean-mcp", "plugin.json");
                if (!File.Exists(pluginJsonPath))
                {
                    string pluginDir = Path.GetDirectoryName(pluginJsonPath);
                    if (!Directory.Exists(pluginDir)) Directory.CreateDirectory(pluginDir);
                    File.WriteAllText(pluginJsonPath, "{\n  \"name\": \"unity-lean-mcp\"\n}\n", Encoding.UTF8);
                }

                string[] targetConfigs = new[]
                {
                    Path.Combine(rootFolder, ".agents", "plugins", "unity-lean-mcp", "mcp_config.json"),
                    Path.Combine(rootFolder, ".vscode", "mcp.json"),
                    Path.Combine(rootFolder, ".cursor", "mcp.json"),
                    Path.Combine(rootFolder, ".mcp.json")
                };

                var sb = new StringBuilder();
                sb.AppendLine("Installed MCP configuration to:");

                foreach (string configPath in targetConfigs)
                {
                    UpdateOrWriteMcpConfig(configPath, mcpDir, "mcpServers", rootFolder);
                    sb.AppendLine($"• {MakeRelativePath(rootFolder, configPath).Replace('\\', '/')}");
                }

                string codexConfigPath = Path.Combine(rootFolder, ".codex", "config.toml");
                AppendCodexMcpConfig(codexConfigPath, mcpDir);
                sb.AppendLine($"• {MakeRelativePath(rootFolder, codexConfigPath).Replace('\\', '/')}");

                Debug.Log($"[UnityLeanMcp] {sb}");
                if (showDialog && !Application.isBatchMode)
                {
                    EditorUtility.DisplayDialog("UnityLeanMcp Success", sb.ToString(), "OK");
                }
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[UnityLeanMcp] Failed to install MCP configurations: {ex.Message}");
                Debug.LogException(ex);
                if (showDialog && !Application.isBatchMode)
                {
                    EditorUtility.DisplayDialog("UnityLeanMcp Error", $"Failed to install MCP configurations:\n{ex.Message}", "OK");
                }
                return false;
            }
        }

        public static string FindPackagePath()
        {
            // 1. Try PackageManager PackageInfo for the assembly (resolves absolute path regardless of location)
            try
            {
                var packageInfo = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(UnityLeanMcpInstaller).Assembly);
                if (packageInfo != null && !string.IsNullOrWhiteSpace(packageInfo.resolvedPath))
                {
                    return Path.GetFullPath(packageInfo.resolvedPath);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[UnityLeanMcp] PackageInfo.FindForAssembly failed: {ex.Message}");
            }

            // 2. Try locating via MonoScript asset (handles cases where assembly is not recognized as package)
            try
            {
                string[] guids = AssetDatabase.FindAssets("UnityLeanMcpInstaller t:MonoScript");
                if (guids == null || guids.Length == 0)
                {
                    guids = AssetDatabase.FindAssets("UnityLeanMcpInstaller");
                }

                if (guids != null)
                {
                    foreach (string guid in guids)
                    {
                        string assetPath = AssetDatabase.GUIDToAssetPath(guid);
                        if (!string.IsNullOrEmpty(assetPath) && assetPath.EndsWith("UnityLeanMcpInstaller.cs", StringComparison.OrdinalIgnoreCase))
                        {
                            string fullPath = Path.GetFullPath(assetPath);
                            var dir = new DirectoryInfo(Path.GetDirectoryName(fullPath));
                            while (dir != null)
                            {
                                if (Directory.Exists(Path.Combine(dir.FullName, "MCP~")) ||
                                    Directory.Exists(Path.Combine(dir.FullName, "mcp~")) ||
                                    File.Exists(Path.Combine(dir.FullName, "package.json")))
                                {
                                    return dir.FullName;
                                }
                                dir = dir.Parent;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[UnityLeanMcp] AssetDatabase search for package path failed: {ex.Message}");
            }

            // 3. Fallback: probe standard embedded package location relative to Assets
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string standardEmbedded = Path.Combine(projectRoot, "Packages", "com.pereviader.unityleanmcp");
            if (Directory.Exists(standardEmbedded))
            {
                return standardEmbedded;
            }

            // 4. Fallback: probe Library/PackageCache
            string packageCache = Path.Combine(projectRoot, "Library", "PackageCache");
            if (Directory.Exists(packageCache))
            {
                string[] matches = Directory.GetDirectories(packageCache, "com.pereviader.unityleanmcp*");
                if (matches != null && matches.Length > 0)
                {
                    return Path.GetFullPath(matches[0]);
                }
            }

            return null;
        }

        public static string FindRepositoryRoot(string assetsPath)
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

        public static void UpdateOrWriteMcpConfig(
            string configPath,
            string mcpDir,
            string rootKey = "mcpServers",
            string repositoryRoot = null)
        {
            string dir = Path.GetDirectoryName(configPath);
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string serverJsonSnippet = BuildMcpServerJsonSnippet(configPath, mcpDir, repositoryRoot);

            string normalizedPath = configPath.Replace('\\', '/');
            string effectiveRootKey = rootKey;
            if (normalizedPath.EndsWith("/.vscode/mcp.json", StringComparison.OrdinalIgnoreCase) ||
                normalizedPath.Equals(".vscode/mcp.json", StringComparison.OrdinalIgnoreCase))
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
                File.WriteAllText(configPath, newContent, Encoding.UTF8);
                return;
            }

            string existing = File.ReadAllText(configPath, Encoding.UTF8).Trim();
            if (string.IsNullOrWhiteSpace(existing))
            {
                string newContent =
                    "{\n" +
                    $"  \"{effectiveRootKey}\": {{\n" +
                    serverJsonSnippet.TrimStart() + "\n" +
                    "  }\n" +
                    "}\n";
                File.WriteAllText(configPath, newContent, Encoding.UTF8);
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
                        File.WriteAllText(configPath, updated, Encoding.UTF8);
                        return;
                    }
                }
            }

            // Insert into root section (effectiveRootKey, or fallback to alternatives)
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
                    File.WriteAllText(configPath, updated, Encoding.UTF8);
                    return;
                }
            }

            string fallbackContent =
                "{\n" +
                $"  \"{effectiveRootKey}\": {{\n" +
                serverJsonSnippet.TrimStart() + "\n" +
                "  }\n" +
                "}\n";
            File.WriteAllText(configPath, fallbackContent, Encoding.UTF8);
        }

        private static string BuildMcpServerJsonSnippet(string configPath, string mcpDir, string repositoryRoot)
        {
            string formattedMcpDir = mcpDir.Replace('\\', '/');
            if (!formattedMcpDir.EndsWith("/"))
            {
                formattedMcpDir += "/";
            }

            string effectiveCwd = formattedMcpDir;
            if (McpConfigurationPaths.TryGetWorkspaceRelativeMcpPath(
                    repositoryRoot,
                    formattedMcpDir,
                    configPath,
                    out string workspaceRelativePath))
            {
                effectiveCwd = workspaceRelativePath;
            }

            return
                "    \"unity-lean-mcp\": {\n" +
                "      \"command\": \"dotnet\",\n" +
                "      \"args\": [\n" +
                "        \"UnityLeanMcp.Mcp.dll\"\n" +
                "      ],\n" +
                $"      \"cwd\": \"{EscapeJsonString(effectiveCwd)}\"\n" +
                "    }";
        }

        private static string EscapeJsonString(string value)
        {
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        public static void AppendCodexMcpConfig(string configPath, string mcpDir)
        {
            string dir = Path.GetDirectoryName(configPath);
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
                $"cwd = \"{formattedMcpDir}\"\n" +
                "tool_timeout_sec = 1800\n";

            if (!File.Exists(configPath))
            {
                File.WriteAllText(configPath, codexTomlSnippet, Encoding.UTF8);
                return;
            }

            string existing = File.ReadAllText(configPath, Encoding.UTF8);
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

                var sbReplace = new StringBuilder();
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
                    File.WriteAllText(configPath, newText, Encoding.UTF8);
                }
                return;
            }

            var sb = new StringBuilder();
            sb.Append(existing);
            if (!existing.EndsWith("\n"))
            {
                sb.AppendLine();
            }
            sb.AppendLine();
            sb.Append(codexTomlSnippet);
            File.WriteAllText(configPath, sb.ToString(), Encoding.UTF8);
        }

        private static string MakeRelativePath(string fromPath, string toPath)
        {
            var fromUri = new Uri(AppendSlash(Path.GetFullPath(fromPath)));
            var toUri = new Uri(Path.GetFullPath(toPath));
            if (fromUri.Scheme != toUri.Scheme)
            {
                return toPath;
            }
            var relativeUri = fromUri.MakeRelativeUri(toUri);
            string relPath = Uri.UnescapeDataString(relativeUri.ToString());
            return relPath.Replace('\\', '/');
        }

        private static string AppendSlash(string path)
        {
            return path.EndsWith(Path.DirectorySeparatorChar.ToString())
                ? path
                : path + Path.DirectorySeparatorChar;
        }
    }
}
