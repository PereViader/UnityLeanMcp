using System;
using System.IO;
using System.Text;
using System.Threading;

namespace UnityLeanMcp.Mcp;

/// <summary>
/// Shared file I/O helpers with bounded contention retries, atomic text
/// replacement, offset-based reading, and OS lock probing.
/// </summary>
internal static class FileIoRetryHelper
{
    internal static string ReadFileWithRetry(
        string path,
        int maxRetries = 5,
        int delayMs = 100,
        long fromOffset = 0)
    {
        return ReadFileWithRetry(path, maxRetries, delayMs, fromOffset, ReadFileFromOffset);
    }

    internal static string ReadFileWithRetry(
        string path,
        int maxRetries,
        int delayMs,
        long fromOffset,
        Func<string, long, string> reader)
    {
        for (int i = 0; i < maxRetries; i++)
        {
            try
            {
                return reader(path, fromOffset);
            }
            catch (IOException) when (i < maxRetries - 1)
            {
                Thread.Sleep(delayMs);
            }
            catch (UnauthorizedAccessException) when (i < maxRetries - 1)
            {
                Thread.Sleep(delayMs);
            }
        }

        return reader(path, fromOffset);
    }

    internal static string ReadFileFromOffset(string path, long fromOffset = 0)
    {
        using var fs = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        if (fromOffset > 0)
        {
            fs.Seek(fs.Length < fromOffset ? 0 : fromOffset, SeekOrigin.Begin);
        }

        using var reader = new StreamReader(fs, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    internal static void WriteTextAtomically(string path, string content)
    {
        string? directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new ArgumentException("The target path must include a directory.", nameof(path));
        }

        Directory.CreateDirectory(directory);
        string temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                options: FileOptions.SequentialScan))
            {
                byte[] bytes = Encoding.UTF8.GetBytes(content);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }

            // Holding the startup lock prevents concurrent publication races while
            // replacing any stale PID pointer or identity record from a prior run.
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporaryPath); } catch { }
        }
    }

    internal static bool IsFileLocked(string filePath)
    {
        try
        {
            using var stream = File.Open(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            // Windows can report an existing FileShare.None claim as access
            // denied. Preserve the lockfile rather than treating an
            // unprobeable file as stale and deleting it.
            return true;
        }
        catch
        {
            return false;
        }
    }
}
