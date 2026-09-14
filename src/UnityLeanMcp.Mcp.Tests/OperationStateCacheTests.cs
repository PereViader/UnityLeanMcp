using System;
using System.IO;
using Xunit;

namespace UnityLeanMcp.Mcp.Tests;

[Trait("Category", "Unit")]
public sealed class OperationStateCacheTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-json")]
    [InlineData("{\"operationId\":\"active\"}")]
    public void Read_InvalidDurableState_ClearsCachedOperation(string content)
    {
        string path = CreateTempFile(content);
        try
        {
            var cache = new OperationStateCache();
            cache.Set(CreateState("cached"));

            var status = cache.Read(path, out var snapshot);

            Assert.Equal(OperationStateReadStatus.Invalid, status);
            Assert.Null(snapshot);
            Assert.Null(cache.GetCached());
        }
        finally
        {
            DeleteFile(path);
        }
    }

    [Fact]
    public void Read_MissingDurableState_ClearsCachedOperation()
    {
        string path = Path.Combine(Path.GetTempPath(), "unity_operation_cache_missing_" + Guid.NewGuid().ToString("N") + ".json");
        var cache = new OperationStateCache();
        cache.Set(CreateState("cached"));

        var status = cache.Read(path, out var snapshot);

        Assert.Equal(OperationStateReadStatus.Missing, status);
        Assert.Null(snapshot);
        Assert.Null(cache.GetCached());
    }

    [Fact]
    public void Read_ValidActiveRecord_ReplacesCacheAndPreservesOwnership()
    {
        string path = CreateTempFile(
            "{\"operationId\":\"active\",\"kind\":\"eval\",\"status\":\"Executing\",\"editorSessionId\":\"session\",\"startedUtc\":\"now\",\"updatedUtc\":\"later\"}");
        try
        {
            var cache = new OperationStateCache();
            cache.Set(CreateState("old"));

            var status = cache.Read(path, out var snapshot);

            Assert.Equal(OperationStateReadStatus.Valid, status);
            Assert.NotNull(snapshot);
            Assert.Equal("active", snapshot.OperationId);
            Assert.Equal("eval", snapshot.Kind);
            Assert.Equal("Executing", cache.GetCached().Status);
        }
        finally
        {
            DeleteFile(path);
        }
    }

    private static WorkerOperationStateSnapshot CreateState(string operationId)
    {
        return new WorkerOperationStateSnapshot(
            operationId,
            "eval",
            "Executing",
            "session",
            "now",
            "later");
    }

    private static string CreateTempFile(string content)
    {
        string path = Path.Combine(Path.GetTempPath(), "unity_operation_cache_" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, content);
        return path;
    }

    private static void DeleteFile(string path)
    {
        try { File.Delete(path); } catch { }
    }
}
