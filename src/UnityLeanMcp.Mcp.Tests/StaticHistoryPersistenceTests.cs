using System;
using System.IO;
using Xunit;

namespace UnityLeanMcp.Mcp.Tests;

[Trait("Category", "Unit")]
public sealed class StaticHistoryPersistenceTests
{
    [Fact]
    public void TryWrite_ReplacesExistingHistoryWithoutUsingStrictReplaceFileSemantics()
    {
        string directory = CreateTempDirectory();
        string path = Path.Combine(directory, "unity_refresh_result.json");

        try
        {
            File.WriteAllText(path, "old history");

            bool written = UnityLeanMcpStaticHistoryWriter.TryWrite(path, "new history", out var failure);

            Assert.True(written, failure?.ToString());
            Assert.Equal("new history", File.ReadAllText(path));
            Assert.Empty(Directory.GetFiles(directory, "unity_refresh_result.json.*.history.tmp"));
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public void TryWrite_WhenExistingHistoryIsReadLocked_DoesNotOverwriteOrThrow()
    {
        string directory = CreateTempDirectory();
        string path = Path.Combine(directory, "unity_test_results.json");

        try
        {
            File.WriteAllText(path, "old history");

            using (var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                bool written = UnityLeanMcpStaticHistoryWriter.TryWrite(path, "new history", out var failure);

                if (OperatingSystem.IsWindows())
                {
                    Assert.False(written);
                    Assert.NotNull(failure);
                    Assert.Equal("old history", File.ReadAllText(path));
                }
                else
                {
                    Assert.True(written, failure?.ToString());
                    Assert.Equal("new history", File.ReadAllText(path));
                    reader.Position = 0;
                    using var streamReader = new StreamReader(reader, leaveOpen: true);
                    Assert.Equal("old history", streamReader.ReadToEnd());
                }
            }

            Assert.Empty(Directory.GetFiles(directory, "unity_test_results.json.*.history.tmp"));
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    private static string CreateTempDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "unity_static_history_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void DeleteTempDirectory(string directory)
    {
        try { Directory.Delete(directory, true); } catch { }
    }
}
