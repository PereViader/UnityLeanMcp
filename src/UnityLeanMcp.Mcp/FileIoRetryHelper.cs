using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

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
        return RetryIo(() => reader(path, fromOffset), maxRetries, delayMs);
    }

    internal static Task<string> ReadFileWithRetryAsync(
        string path,
        int maxRetries = 5,
        int delayMs = 100,
        long fromOffset = 0,
        CancellationToken cancellationToken = default)
    {
        return ReadFileWithRetryAsync(
            path,
            maxRetries,
            delayMs,
            fromOffset,
            ReadFileFromOffsetAsync,
            cancellationToken);
    }

    internal static Task<string> ReadFileWithRetryAsync(
        string path,
        int maxRetries,
        int delayMs,
        long fromOffset,
        Func<string, long, CancellationToken, Task<string>> reader,
        CancellationToken cancellationToken = default)
    {
        return RetryIoAsync(
            ct => reader(path, fromOffset, ct),
            maxRetries,
            delayMs,
            cancellationToken);
    }

    internal static (string Text, long NextOffset) ReadFileDeltaWithRetry(
        string path,
        long fromOffset = 0,
        int maxRetries = 5,
        int delayMs = 100)
    {
        return RetryIo(() => ReadFileDeltaFromOffset(path, fromOffset), maxRetries, delayMs);
    }

    internal static Task<(string Text, long NextOffset)> ReadFileDeltaWithRetryAsync(
        string path,
        long fromOffset = 0,
        int maxRetries = 5,
        int delayMs = 100,
        CancellationToken cancellationToken = default)
    {
        return ReadFileDeltaWithRetryAsync(
            path,
            fromOffset,
            maxRetries,
            delayMs,
            ReadFileDeltaFromOffsetAsync,
            cancellationToken);
    }

    internal static Task<(string Text, long NextOffset)> ReadFileDeltaWithRetryAsync(
        string path,
        long fromOffset,
        int maxRetries,
        int delayMs,
        Func<string, long, CancellationToken, Task<(string Text, long NextOffset)>> reader,
        CancellationToken cancellationToken = default)
    {
        return RetryIoAsync(
            ct => reader(path, fromOffset, ct),
            maxRetries,
            delayMs,
            cancellationToken);
    }

    private static T RetryIo<T>(Func<T> action, int maxRetries, int delayMs)
    {
        ValueTask<T> task = RetryIoCore(
            _ => new ValueTask<T>(action()),
            _ =>
            {
                Thread.Sleep(delayMs);
                return ValueTask.CompletedTask;
            },
            maxRetries,
            CancellationToken.None);
        return task.GetAwaiter().GetResult();
    }

    private static Task<T> RetryIoAsync<T>(
        Func<CancellationToken, Task<T>> action,
        int maxRetries,
        int delayMs,
        CancellationToken cancellationToken)
    {
        return RetryIoCore(
            ct => new ValueTask<T>(action(ct)),
            ct => new ValueTask(Task.Delay(delayMs, ct)),
            maxRetries,
            cancellationToken).AsTask();
    }

    private static async ValueTask<T> RetryIoCore<T>(
        Func<CancellationToken, ValueTask<T>> action,
        Func<CancellationToken, ValueTask> delay,
        int maxRetries,
        CancellationToken cancellationToken)
    {
        for (int i = 0; i < maxRetries; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await action(cancellationToken).ConfigureAwait(false);
            }
            catch (IOException) when (i < maxRetries - 1)
            {
                await delay(cancellationToken).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException) when (i < maxRetries - 1)
            {
                await delay(cancellationToken).ConfigureAwait(false);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return await action(cancellationToken).ConfigureAwait(false);
    }

    internal static string ReadFileFromOffset(string path, long fromOffset = 0)
    {
        using var fs = OpenReadStream(path, useAsync: false);
        SeekFromOffset(fs, fromOffset);

        using var reader = new StreamReader(fs, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    internal static async Task<string> ReadFileFromOffsetAsync(
        string path,
        long fromOffset = 0,
        CancellationToken cancellationToken = default)
    {
        using var fs = OpenReadStream(path, useAsync: true);
        SeekFromOffset(fs, fromOffset);

        using var reader = new StreamReader(fs, Encoding.UTF8);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static (string Text, long NextOffset) ReadFileDeltaFromOffset(string path, long fromOffset = 0)
    {
        using var fs = OpenReadStream(path, useAsync: false);
        if (!TryPrepareDeltaRead(fs, fromOffset, out long effectiveOffset))
        {
            return (string.Empty, effectiveOffset);
        }

        using var reader = new StreamReader(fs, Encoding.UTF8);
        string text = reader.ReadToEnd();
        return (text, fs.Position);
    }

    internal static async Task<(string Text, long NextOffset)> ReadFileDeltaFromOffsetAsync(
        string path,
        long fromOffset = 0,
        CancellationToken cancellationToken = default)
    {
        using var fs = OpenReadStream(path, useAsync: true);
        if (!TryPrepareDeltaRead(fs, fromOffset, out long effectiveOffset))
        {
            return (string.Empty, effectiveOffset);
        }

        using var reader = new StreamReader(fs, Encoding.UTF8);
        string text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        return (text, fs.Position);
    }

    private static FileStream OpenReadStream(string path, bool useAsync)
    {
        return new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096,
            useAsync: useAsync);
    }

    private static void SeekFromOffset(FileStream fs, long fromOffset)
    {
        if (fromOffset > 0)
        {
            fs.Seek(fs.Length < fromOffset ? 0 : fromOffset, SeekOrigin.Begin);
        }
    }

    private static bool TryPrepareDeltaRead(FileStream fs, long fromOffset, out long effectiveOffset)
    {
        long length = fs.Length;
        effectiveOffset = fromOffset > 0 && length >= fromOffset ? fromOffset : 0;
        if (effectiveOffset == length)
        {
            return false;
        }

        if (effectiveOffset > 0)
        {
            fs.Seek(effectiveOffset, SeekOrigin.Begin);
        }

        return true;
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
