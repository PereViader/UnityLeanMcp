using System;
using System.IO;
using Xunit;

namespace UnityLeanMcp.Mcp.Tests;

[Trait("Category", "Unit")]
public class WorkerThreadSnapshotsTests
{
    [Fact]
    public void ReadsOperationStateWithoutUnitySerialization()
    {
        string path = CreateTempFile("{\"operationId\":\"op-1\",\"kind\":\"eval\",\"status\":\"Executing\",\"editorSessionId\":\"session\",\"startedUtc\":\"now\",\"updatedUtc\":\"later\"}");
        try
        {
            Assert.True(WorkerThreadSnapshots.TryReadOperationState(path, out var snapshot));
            Assert.Equal("op-1", snapshot.OperationId);
            Assert.Equal("eval", snapshot.Kind);
            Assert.Equal("Executing", snapshot.Status);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadsOperationResultAndPreservesEscapedPayload()
    {
        string path = CreateTempFile("{\"operationId\":\"op-2\",\"success\":true,\"payload\":\"line 1\\nline 2\\\\path\",\"failedTests\":[{\"message\":\"ignored\"}]}");
        try
        {
            Assert.True(WorkerThreadSnapshots.TryReadOperationResult(path, out var snapshot));
            Assert.Equal("op-2", snapshot.OperationId);
            Assert.True(snapshot.Success);
            Assert.Equal("line 1\nline 2\\path", snapshot.Payload);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadsInterruptedTestResultFromResultState()
    {
        string path = CreateTempFile("{\"runId\":\"run-1\",\"success\":false,\"resultState\":\"Interrupted\",\"message\":\"reloaded\",\"failCount\":2,\"passCount\":3,\"skipCount\":1}");
        try
        {
            Assert.True(WorkerThreadSnapshots.TryReadOperationResult(path, out var snapshot));
            Assert.Equal("run-1", snapshot.OperationId);
            Assert.True(snapshot.Interrupted);
            Assert.Equal(2, snapshot.FailCount);
            Assert.Equal(3, snapshot.PassCount);
            Assert.Equal(1, snapshot.SkipCount);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadsInterruptedEvalResultWithCancelledStatus()
    {
        string path = CreateTempFile("{\"operationId\":\"eval-1\",\"success\":false,\"interrupted\":true,\"resultState\":\"Cancelled\",\"message\":\"Operation was cancelled\"}");
        try
        {
            Assert.True(WorkerThreadSnapshots.TryReadOperationResult(path, out var snapshot));
            Assert.Equal("eval-1", snapshot.OperationId);
            Assert.False(snapshot.Success);
            Assert.True(snapshot.Interrupted);
            Assert.Equal("Cancelled", snapshot.ResultState);
            Assert.Equal("Operation was cancelled", snapshot.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadsInterruptedExecuteResult()
    {
        string path = CreateTempFile("{\"operationId\":\"exec-1\",\"success\":false,\"interrupted\":true,\"resultState\":\"Interrupted\",\"message\":\"Domain reload during execution\"}");
        try
        {
            Assert.True(WorkerThreadSnapshots.TryReadOperationResult(path, out var snapshot));
            Assert.Equal("exec-1", snapshot.OperationId);
            Assert.False(snapshot.Success);
            Assert.True(snapshot.Interrupted);
            Assert.Equal("Interrupted", snapshot.ResultState);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadsSuccessfulExecuteResultWithoutInterrupted()
    {
        string path = CreateTempFile("{\"operationId\":\"exec-2\",\"success\":true,\"interrupted\":false,\"resultState\":\"Success\",\"payload\":\"done\"}");
        try
        {
            Assert.True(WorkerThreadSnapshots.TryReadOperationResult(path, out var snapshot));
            Assert.Equal("exec-2", snapshot.OperationId);
            Assert.True(snapshot.Success);
            Assert.False(snapshot.Interrupted);
            Assert.Equal("Success", snapshot.ResultState);
            Assert.Equal("done", snapshot.Payload);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RejectsMalformedJsonWithoutLoggingThroughUnity()
    {
        string path = CreateTempFile("{\"operationId\":");
        try
        {
            Assert.False(WorkerThreadSnapshots.TryReadOperationState(path, out _));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TestCancellationRequest_IsAtomicAndIdempotentForOneRun()
    {
        string directory = Path.Combine(Path.GetTempPath(), "unity_worker_cancel_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "unity_test_cancellation.txt");
        try
        {
            Assert.True(WorkerThreadSnapshots.TryWriteTestCancellationRequest(path, "run-1"));
            Assert.True(WorkerThreadSnapshots.TryWriteTestCancellationRequest(path, "run-1"));
            Assert.True(WorkerThreadSnapshots.TryReadTestCancellationRequest(path, "run-1"));
            Assert.True(WorkerThreadSnapshots.TryWriteTestCancellationRequest(path, "run-2"));
            Assert.True(WorkerThreadSnapshots.TryReadTestCancellationRequest(path, "run-2"));
            Assert.False(WorkerThreadSnapshots.TryReadTestCancellationRequest(path, "run-1"));
        }
        finally
        {
            try { Directory.Delete(directory, true); } catch { }
        }
    }

    private static string CreateTempFile(string content)
    {
        string path = Path.Combine(Path.GetTempPath(), "unity_worker_snapshot_" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, content);
        return path;
    }
}
