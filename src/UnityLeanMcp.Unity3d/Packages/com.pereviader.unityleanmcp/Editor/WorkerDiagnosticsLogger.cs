using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace UnityLeanMcp
{
    /// <summary>
    /// Best-effort diagnostics for socket worker threads. This type deliberately
    /// has no Unity dependencies and never lets a logging failure escape into
    /// the worker that is handling a client connection.
    /// </summary>
    internal static class WorkerDiagnosticsLogger
    {
        internal const int MaxFileBytes = 64 * 1024;
        internal const int MaxEntryBytes = 8 * 1024;

        private const string TruncationMarker = "...(truncated)";
        private static readonly object s_FileLock = new object();
        private static readonly UTF8Encoding s_Utf8 = new UTF8Encoding(false);

        internal static void Info(string path, string message)
        {
            Append(path, "INFO", message);
        }

        internal static void Warning(string path, string message)
        {
            Append(path, "WARNING", message);
        }

        internal static void Error(string path, string message)
        {
            Append(path, "ERROR", message);
        }

        internal static void Append(string path, string level, string message)
        {
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            try
            {
                byte[] entry = CreateEntry(level, message);
                string directory = Path.GetDirectoryName(path) ?? string.Empty;

                lock (s_FileLock)
                {
                    if (!string.IsNullOrEmpty(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }

                    using (var stream = new FileStream(
                        path,
                        FileMode.OpenOrCreate,
                        FileAccess.ReadWrite,
                        FileShare.ReadWrite | FileShare.Delete))
                    {
                        if (stream.Length + entry.Length > MaxFileBytes)
                        {
                            // Truncating in place avoids File.Replace and its
                            // Windows sharing requirements for a static log.
                            stream.SetLength(0);
                        }

                        stream.Seek(0, SeekOrigin.End);
                        stream.Write(entry, 0, entry.Length);
                        stream.Flush();
                    }
                }
            }
            catch (ThreadAbortException)
            {
                throw;
            }
            catch (Exception)
            {
                // Diagnostics must never turn a transport failure into a
                // second worker exception, especially during domain reload.
            }
        }

        private static byte[] CreateEntry(string level, string message)
        {
            string safeLevel = string.IsNullOrEmpty(level) ? "INFO" : level;
            string safeMessage = (message ?? string.Empty)
                .Replace("\r", "\\r")
                .Replace("\n", "\\n");
            string prefix = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)
                + " [" + safeLevel + "] [thread " + Thread.CurrentThread.ManagedThreadId + "] ";
            string suffix = Environment.NewLine;
            int messageBudget = MaxEntryBytes - s_Utf8.GetByteCount(prefix) - s_Utf8.GetByteCount(suffix);
            safeMessage = TruncateToUtf8(safeMessage, Math.Max(0, messageBudget));

            return s_Utf8.GetBytes(prefix + safeMessage + suffix);
        }

        private static string TruncateToUtf8(string value, int maxBytes)
        {
            if (maxBytes <= 0)
            {
                return string.Empty;
            }

            if (s_Utf8.GetByteCount(value) <= maxBytes)
            {
                return value;
            }

            int markerBytes = s_Utf8.GetByteCount(TruncationMarker);
            int valueBudget = Math.Max(0, maxBytes - markerBytes);
            int prefixLength = GetUtf8PrefixLength(value, valueBudget);
            return value.Substring(0, prefixLength) + TruncationMarker;
        }

        private static int GetUtf8PrefixLength(string value, int maxBytes)
        {
            int byteCount = 0;
            int charIndex = 0;
            while (charIndex < value.Length)
            {
                int charLength = char.IsHighSurrogate(value[charIndex]) && charIndex + 1 < value.Length
                    && char.IsLowSurrogate(value[charIndex + 1]) ? 2 : 1;
                int nextBytes = s_Utf8.GetByteCount(value, charIndex, charLength);
                if (byteCount + nextBytes > maxBytes)
                {
                    break;
                }

                byteCount += nextBytes;
                charIndex += charLength;
            }

            return charIndex;
        }
    }
}
