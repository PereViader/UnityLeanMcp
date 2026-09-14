using System;
using System.IO;

namespace UnityLeanMcp
{
    /// <summary>
    /// Resolves MCP server and Unity project paths relative to a checkout when possible.
    /// This class intentionally has no Unity dependencies so the path contract can be tested
    /// without starting the Editor.
    /// </summary>
    public static class McpConfigurationPaths
    {
        public static bool TryGetPortablePaths(
            string repositoryRoot,
            string mcpDirectory,
            string projectRoot,
            out string relativeMcpDll,
            out string relativeProjectRoot)
        {
            relativeMcpDll = null;
            relativeProjectRoot = null;

            if (string.IsNullOrWhiteSpace(repositoryRoot) ||
                string.IsNullOrWhiteSpace(mcpDirectory) ||
                string.IsNullOrWhiteSpace(projectRoot))
            {
                return false;
            }

            if (!TryGetRepositoryRelativePath(
                    repositoryRoot,
                    Path.Combine(mcpDirectory, "UnityLeanMcp.Mcp.dll"),
                    out relativeMcpDll) ||
                !TryGetRepositoryRelativePath(repositoryRoot, projectRoot, out relativeProjectRoot))
            {
                relativeMcpDll = null;
                relativeProjectRoot = null;
                return false;
            }

            return true;
        }

        private static bool TryGetRepositoryRelativePath(string repositoryRoot, string targetPath, out string relativePath)
        {
            relativePath = null;
            if (string.IsNullOrWhiteSpace(repositoryRoot) || string.IsNullOrWhiteSpace(targetPath))
            {
                return false;
            }

            try
            {
                string fullRepositoryRoot = Path.GetFullPath(repositoryRoot);
                string fullTargetPath = Path.GetFullPath(targetPath);
                string candidate = PathsEqual(fullRepositoryRoot, fullTargetPath)
                    ? "."
                    : MakeRelativePath(fullRepositoryRoot, fullTargetPath).Replace('\\', '/');

                if (string.IsNullOrWhiteSpace(candidate) ||
                    IsAbsolutePath(candidate) ||
                    candidate == ".." ||
                    candidate.StartsWith("../", StringComparison.Ordinal))
                {
                    return false;
                }

                relativePath = candidate;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool PathsEqual(string left, string right)
        {
            StringComparison comparison =
                Environment.OSVersion.Platform == PlatformID.Win32NT
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal;
            return string.Equals(
                left.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                right.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                comparison);
        }

        private static bool IsAbsolutePath(string path)
        {
            return Path.IsPathRooted(path) ||
                path.StartsWith("//", StringComparison.Ordinal) ||
                path.StartsWith("\\\\", StringComparison.Ordinal) ||
                (path.Length >= 3 && char.IsLetter(path[0]) && path[1] == ':' && (path[2] == '/' || path[2] == '\\'));
        }

        private static string MakeRelativePath(string fromPath, string toPath)
        {
            var fromUri = new Uri(AppendSlash(Path.GetFullPath(fromPath)));
            var toUri = new Uri(Path.GetFullPath(toPath));
            if (fromUri.Scheme != toUri.Scheme)
            {
                return toPath;
            }

            string relative = Uri.UnescapeDataString(fromUri.MakeRelativeUri(toUri).ToString());
            return relative.Replace('\\', '/');
        }

        private static string AppendSlash(string path)
        {
            return path.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal) ||
                path.EndsWith(Path.AltDirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? path
                : path + Path.DirectorySeparatorChar;
        }
    }
}
