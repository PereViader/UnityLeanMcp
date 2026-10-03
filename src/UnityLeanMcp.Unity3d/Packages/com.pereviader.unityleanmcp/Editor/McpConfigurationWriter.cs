using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace UnityLeanMcp
{
    internal static class McpConfigurationWriter
    {
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

        public static void UpdateOrWriteMcpConfig(string configPath, string mcpDir,
            string rootKey = "mcpServers", string repositoryRoot = null)
        {
            string normalizedPath = configPath.Replace('\\', '/');
            if (normalizedPath.EndsWith("/.vscode/mcp.json", StringComparison.OrdinalIgnoreCase) ||
                normalizedPath.Equals(".vscode/mcp.json", StringComparison.OrdinalIgnoreCase))
                rootKey = "servers";

            string existing = File.Exists(configPath) ? File.ReadAllText(configPath, Encoding.UTF8) : null;
            var document = string.IsNullOrWhiteSpace(existing)
                ? new Dictionary<string, object>()
                : UnityLeanJson.DeserializeObject(existing);

            Dictionary<string, object> servers = null;
            if (document.TryGetValue(rootKey, out object serversObj))
            {
                servers = serversObj as Dictionary<string, object>;
                if (servers == null)
                    throw new FormatException("The MCP server section must be a JSON object.");
            }

            if (servers == null)
            {
                servers = new Dictionary<string, object>();
                document[rootKey] = servers;
            }

            string cwd = mcpDir.Replace('\\', '/').TrimEnd('/') + "/";
            if (McpConfigurationPaths.TryGetWorkspaceRelativeMcpPath(repositoryRoot, cwd, configPath, out string relative))
                cwd = relative;

            servers["unity-lean-mcp"] = new Dictionary<string, object>
            {
                ["command"] = "dotnet",
                ["args"] = new List<object> { "UnityLeanMcp.Mcp.dll" },
                ["cwd"] = cwd
            };
            WriteAtomic(configPath, UnityLeanJson.Serialize(document, prettyPrint: true));
        }

        private static void WriteAtomic(string path, string content)
        {
            string directory = Path.GetDirectoryName(Path.GetFullPath(path));
            Directory.CreateDirectory(directory);
            string staging = Path.Combine(directory, ".unity-mcp-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.WriteAllText(staging, content, new UTF8Encoding(false));
                UnityLeanMcpStaticHistoryWriter.MoveWithOverwrite(staging, path);
            }
            finally
            {
                if (File.Exists(staging)) File.Delete(staging);
            }
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
                WriteAtomic(configPath, codexTomlSnippet);
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
                    WriteAtomic(configPath, newText);
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
            WriteAtomic(configPath, sb.ToString());
        }

    }
}
