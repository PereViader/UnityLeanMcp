using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace UnityLeanMcp
{
    /// <summary>
    /// Publishes a non-authoritative history snapshot without making the
    /// operation result depend on a shared file being replaceable.
    /// </summary>
    internal static class UnityLeanMcpStaticHistoryWriter
    {
        private const int MoveFileReplaceExisting = 1;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool MoveFileEx(string existingFileName, string newFileName, int flags);

        [DllImport("libc", EntryPoint = "rename", CharSet = CharSet.Ansi, SetLastError = true)]
        private static extern int Rename(string oldPath, string newPath);

        internal static bool TryWrite(string path, string content, out Exception failure)
        {
            failure = null;
            string tempPath = path + "." + Guid.NewGuid().ToString("N") + ".history.tmp";

            try
            {
                string directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(tempPath, content, new UTF8Encoding(false));

                // Native move-overwrite APIs with replacement semantics avoid File.Replace's
                // stricter ReplaceFileW requirements on Windows. If a reader
                // has the existing history file open without delete sharing,
                // this single best-effort attempt fails immediately and the
                // previously published history remains intact.
                MoveWithOverwrite(tempPath, path);
                return true;
            }
            catch (Exception ex)
            {
                failure = ex;
                return false;
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    try { File.Delete(tempPath); } catch { }
                }
            }
        }

        private static void MoveWithOverwrite(string sourcePath, string destinationPath)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                if (!MoveFileEx(sourcePath, destinationPath, MoveFileReplaceExisting))
                {
                    throw new IOException(
                        $"MoveFileEx failed to replace '{destinationPath}' (error {Marshal.GetLastWin32Error()}).");
                }

                return;
            }

            if (Rename(sourcePath, destinationPath) != 0)
            {
                throw new IOException(
                    $"rename failed to replace '{destinationPath}' (error {Marshal.GetLastWin32Error()}).");
            }
        }
    }
}
