using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace UnityLeanMcp.Mcp.Tests;

[Trait("Category", "Unit")]
public sealed class WorkerDiagnosticsLoggerTests
{
    [Fact]
    public void ConcurrentWorkersAppendBoundedEntries()
    {
        string directory = CreateTempDirectory();
        string path = Path.Combine(directory, "worker.log");
        try
        {
            Parallel.For(0, 64, index =>
            {
                WorkerDiagnosticsLogger.Error(path, "worker " + index + " " + new string('x', 2000));
            });

            byte[] bytes = File.ReadAllBytes(path);
            Assert.InRange(bytes.Length, 1, WorkerDiagnosticsLogger.MaxFileBytes);

            string content = Encoding.UTF8.GetString(bytes);
            string[] lines = content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            Assert.NotEmpty(lines);
            Assert.All(lines, line =>
                Assert.InRange(Encoding.UTF8.GetByteCount(line + Environment.NewLine), 1, WorkerDiagnosticsLogger.MaxEntryBytes));
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public void NormalizesNewlinesAndTruncatesOversizedMessages()
    {
        string directory = CreateTempDirectory();
        string path = Path.Combine(directory, "worker.log");
        try
        {
            WorkerDiagnosticsLogger.Info(path, "first\r\nsecond " + new string('x', 20_000));

            byte[] bytes = File.ReadAllBytes(path);
            string content = Encoding.UTF8.GetString(bytes);
            Assert.InRange(bytes.Length, 1, WorkerDiagnosticsLogger.MaxFileBytes);
            Assert.Contains("first\\r\\nsecond", content, StringComparison.Ordinal);
            Assert.Contains("...(truncated)", content, StringComparison.Ordinal);
            Assert.DoesNotContain("first\r\nsecond", content, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    private static string CreateTempDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "unity_worker_log_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void DeleteTempDirectory(string directory)
    {
        try
        {
            Directory.Delete(directory, true);
        }
        catch
        {
        }
    }
}
