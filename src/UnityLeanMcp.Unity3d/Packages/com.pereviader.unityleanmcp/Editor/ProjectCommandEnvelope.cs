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

        private static string Normalize(string path)
        {
            string full = Path.GetFullPath(path);
            string root = Path.GetPathRoot(full);
            return full.Length == root.Length ? full : full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }
}
