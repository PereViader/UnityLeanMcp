using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace UnityLeanMcp
{
    // Every request names its intended project on the same connection that
    // executes the command. A recycled port can never redirect a mutation.
    internal static class ProjectCommandEnvelope
    {
        private const string Prefix = "PROJECT ";

        public static string Encode(string projectRoot, string command)
        {
            return Prefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(Normalize(projectRoot))) + " " + command;
        }

        public static bool TryDecode(string line, string projectRoot, out string command)
        {
            command = null;
            if (string.IsNullOrEmpty(line) || !line.StartsWith(Prefix, StringComparison.Ordinal)) return false;
            int separator = line.IndexOf(' ', Prefix.Length);
            if (separator < 0 || separator == line.Length - 1) return false;
            try
            {
                string expected = Encoding.UTF8.GetString(Convert.FromBase64String(line.Substring(Prefix.Length, separator - Prefix.Length)));
                var comparison = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                if (!string.Equals(Normalize(expected), Normalize(projectRoot), comparison)) return false;
                command = line.Substring(separator + 1);
                return true;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is FormatException || ex is NotSupportedException || ex is IOException)
            {
                return false;
            }
        }

#nullable enable
        internal static Func<string, string>? CustomRealPathResolver;
#nullable restore

        private static string Normalize(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return path ?? string.Empty;
            string full = CustomRealPathResolver != null ? CustomRealPathResolver(path) : ResolveRealPath(path);
            string root = Path.GetPathRoot(full);
            return full.Length == root.Length ? full : full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        internal static string ResolveRealPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return path ?? string.Empty;
            }

            try
            {
                string fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                int loopCount = 0;
                const int maxHops = 32;
                string current = fullPath;

                while (loopCount++ < maxHops)
                {
                    string finalTarget = ResolveLink(current);
                    if (!string.IsNullOrWhiteSpace(finalTarget))
                    {
                        string targetFull = Path.GetFullPath(finalTarget).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                        if (!string.Equals(targetFull, current, StringComparison.Ordinal))
                        {
                            current = targetFull;
                            continue;
                        }
                    }

                    string resolvedAncestor = ResolveAncestors(current);
                    if (resolvedAncestor != null)
                    {
                        string ancestorFull = Path.GetFullPath(resolvedAncestor).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                        if (!string.Equals(ancestorFull, current, StringComparison.Ordinal))
                        {
                            current = ancestorFull;
                            continue;
                        }
                    }

                    break;
                }

                return current;
            }
            catch
            {
                return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
        }

        private static string ResolveAncestors(string path)
        {
            string root = Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root))
            {
                return null;
            }

            string relative = path.Substring(root.Length);
            string[] parts = relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);

            string current = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.IsNullOrEmpty(current) && root.StartsWith('/'))
            {
                current = "/";
            }

            for (int i = 0; i < parts.Length; i++)
            {
                current = Path.Combine(current, parts[i]);
                string target = ResolveLink(current);
                if (!string.IsNullOrWhiteSpace(target))
                {
                    string targetPath = Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    if (!string.Equals(targetPath, current, StringComparison.Ordinal))
                    {
                        if (i + 1 == parts.Length)
                        {
                            return targetPath;
                        }

                        string remainder = string.Join(Path.DirectorySeparatorChar.ToString(), parts, i + 1, parts.Length - (i + 1));
                        return Path.Combine(targetPath, remainder);
                    }
                }
            }

            return null;
        }

        private static string ResolveLink(string path)
        {
            try
            {
                string trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (File.Exists(trimmed) || Directory.Exists(trimmed))
                {
                    var resolveMethod = typeof(File).GetMethod("ResolveLinkTarget", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                    if (resolveMethod != null)
                    {
                        var info = resolveMethod.Invoke(null, new object[] { trimmed, true }) as FileSystemInfo;
                        if (info != null && !string.IsNullOrWhiteSpace(info.FullName))
                        {
                            return info.FullName;
                        }
                    }
                }
            }
            catch
            {
            }

            return null;
        }
    }
}
