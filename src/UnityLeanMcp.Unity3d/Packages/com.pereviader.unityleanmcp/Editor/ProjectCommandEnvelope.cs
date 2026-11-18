using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

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
        private static readonly AsyncLocal<Func<string, string?>?> s_CustomLinkResolver = new AsyncLocal<Func<string, string?>?>();

        internal static Func<string, string>? CustomRealPathResolver;

        internal static Func<string, string?>? CustomLinkResolver
        {
            get => s_CustomLinkResolver.Value;
            set => s_CustomLinkResolver.Value = value;
        }
#nullable restore

        private static string Normalize(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return path ?? string.Empty;
            string full = CustomRealPathResolver != null ? CustomRealPathResolver(path) : ResolveRealPath(path);
            return TrimTrailingSeparators(full);
        }

        private static string TrimTrailingSeparators(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return path;
            }

            string root = Path.GetPathRoot(path);
            if (!string.IsNullOrEmpty(root) && path.Length <= root.Length)
            {
                return path;
            }

            string trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!string.IsNullOrEmpty(root) && trimmed.Length < root.Length)
            {
                return root;
            }

            return trimmed;
        }

        internal static string ResolveRealPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return path ?? string.Empty;
            }

            try
            {
                string current = TrimTrailingSeparators(Path.GetFullPath(path));

                int loopCount = 0;
                const int maxHops = 32;

                while (loopCount++ < maxHops)
                {
                    string finalTarget = ResolveLink(current);
                    if (!string.IsNullOrWhiteSpace(finalTarget))
                    {
                        string targetFull = TrimTrailingSeparators(Path.GetFullPath(finalTarget));
                        if (!string.Equals(targetFull, current, StringComparison.Ordinal))
                        {
                            current = targetFull;
                            continue;
                        }
                    }

                    string resolvedAncestor = ResolveAncestors(current);
                    if (resolvedAncestor != null)
                    {
                        string ancestorFull = TrimTrailingSeparators(Path.GetFullPath(resolvedAncestor));
                        if (!string.Equals(ancestorFull, current, StringComparison.Ordinal))
                        {
                            current = ancestorFull;
                            continue;
                        }
                    }

                    break;
                }

                return CanonicalizeExistingPathCasing(current);
            }
            catch
            {
                return TrimTrailingSeparators(path);
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

            string current = root;
            for (int i = 0; i < parts.Length; i++)
            {
                current = Path.Combine(current, parts[i]);
                string target = ResolveLink(current);
                if (!string.IsNullOrWhiteSpace(target))
                {
                    string targetPath = TrimTrailingSeparators(Path.GetFullPath(target));
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
                string trimmed = TrimTrailingSeparators(path);
                var customResolver = CustomLinkResolver;
                if (customResolver != null)
                {
                    string customTarget = customResolver(trimmed) ?? customResolver(trimmed.Replace('\\', '/'));
                    if (!string.IsNullOrWhiteSpace(customTarget))
                    {
                        if (File.Exists(customTarget))
                        {
                            return new FileInfo(customTarget).FullName;
                        }

                        if (Directory.Exists(customTarget))
                        {
                            return new DirectoryInfo(customTarget).FullName;
                        }

                        return customTarget;
                    }
                }

                if (File.Exists(trimmed))
                {
                    return ResolveOsLinkTarget(typeof(File), trimmed);
                }

                if (Directory.Exists(trimmed))
                {
                    return ResolveOsLinkTarget(typeof(Directory), trimmed);
                }
            }
            catch
            {
            }

            return null;
        }

        private static string ResolveOsLinkTarget(Type fileSystemType, string path)
        {
            MethodInfo resolveMethod = fileSystemType.GetMethod("ResolveLinkTarget", BindingFlags.Public | BindingFlags.Static);
            if (resolveMethod == null)
            {
                return null;
            }

            var info = resolveMethod.Invoke(null, new object[] { path, true }) as FileSystemInfo;
            return info != null && !string.IsNullOrWhiteSpace(info.FullName) ? info.FullName : null;
        }

        private static string CanonicalizeExistingPathCasing(string path)
        {
            string root = Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root))
            {
                return path;
            }

            string relative = path.Substring(root.Length);
            if (string.IsNullOrEmpty(relative))
            {
                return path;
            }

            string[] parts = relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
            string current = root;

            for (int i = 0; i < parts.Length; i++)
            {
                string segment = parts[i];
                string candidate = Path.Combine(current, segment);
                if (!File.Exists(candidate) && !Directory.Exists(candidate))
                {
                    for (int j = i; j < parts.Length; j++)
                    {
                        current = Path.Combine(current, parts[j]);
                    }

                    return current;
                }

                current = Path.Combine(current, ResolveEntryCasing(current, segment));
            }

            return current;
        }

        private static string ResolveEntryCasing(string parentDir, string segment)
        {
            try
            {
                var entries = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                    ? Directory.EnumerateFileSystemEntries(parentDir, segment)
                    : Directory.EnumerateFileSystemEntries(parentDir);

                string caseInsensitiveMatch = null;
                foreach (string entry in entries)
                {
                    string name = Path.GetFileName(entry);
                    if (string.Equals(name, segment, StringComparison.Ordinal))
                    {
                        return segment;
                    }

                    if (caseInsensitiveMatch == null &&
                        string.Equals(name, segment, StringComparison.OrdinalIgnoreCase))
                    {
                        caseInsensitiveMatch = name;
                    }
                }

                return caseInsensitiveMatch ?? segment;
            }
            catch
            {
                return segment;
            }
        }
    }
}
