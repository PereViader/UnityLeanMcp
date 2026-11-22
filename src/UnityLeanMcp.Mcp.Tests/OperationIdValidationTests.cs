using System.Reflection;
using System.Text;
using UnityEditor;
using UnityLeanMcp;

namespace UnityLeanMcp.Mcp.Tests;

[Trait("Category", "Unit")]
[Collection("CommandGate")]
public sealed class OperationIdValidationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    private readonly string _previousProjectRoot = UnityLeanMcpPaths.ProjectRoot;

    public OperationIdValidationTests()
    {
        Directory.CreateDirectory(_root);
        UnityLeanMcpPaths.ProjectRoot = _root;
        Directory.CreateDirectory(UnityLeanMcpPaths.TempDir);
        UnityCommandGate.ForceReset();
        UnityCommandGate.RetryThrottleMs = 0;
        SessionState.EraseString("UnityLeanMcp_PendingResult");
        RunTestsHandler.ActiveStateKnown = true;
        RunTestsHandler.Active = false;
        CancelOperationHandler.CancelRouteHook = null;
        UnityCommandGate.WriteAtomicHook = null;
        UnityLeanMcpDispatcher.Clear();
    }

    public static IEnumerable<object[]> InvalidOperationIds()
    {
        yield return new object[] { ".." };
        yield return new object[] { "../escape" };
        yield return new object[] { "a..b" };
        yield return new object[] { "a/b" };
        yield return new object[] { "a\\b" };
        yield return new object[] { "op:1" };
        yield return new object[] { "op*" };
        yield return new object[] { new string('a', UnityLeanMcpPaths.MaxOperationIdLength + 1) };
        yield return new object[] { "op\tid" };
        yield return new object[] { "op\u00e9" };
    }

    [Theory]
    [InlineData("a")]
    [InlineData("op_1-2.3")]
    [InlineData("0123456789abcdef0123456789abcdef")]
    public void IsValidOperationId_AcceptsValidIds(string operationId)
    {
        Assert.True(UnityLeanMcpPaths.IsValidOperationId(operationId));
    }

    [Fact]
    public void IsValidOperationId_AcceptsMaxLength128()
    {
        Assert.True(UnityLeanMcpPaths.IsValidOperationId(new string('a', UnityLeanMcpPaths.MaxOperationIdLength)));
    }

    [Theory]
    [MemberData(nameof(InvalidOperationIds))]
    [InlineData("op id")]
    public void IsValidOperationId_RejectsInvalidIds(string operationId)
    {
        Assert.False(UnityLeanMcpPaths.IsValidOperationId(operationId));
    }

    [Theory]
    [MemberData(nameof(InvalidOperationIds))]
    public void UnityLeanMcpPaths_GetResultFileMethods_ThrowArgumentExceptionForInvalidOperationId(string operationId)
    {
        Assert.Throws<ArgumentException>(() => UnityLeanMcpPaths.GetRefreshResultFile(operationId));
        Assert.Throws<ArgumentException>(() => UnityLeanMcpPaths.GetEvalResultFile(operationId));
        Assert.Throws<ArgumentException>(() => UnityLeanMcpPaths.GetTestResultsFile(operationId));
        Assert.Throws<ArgumentException>(() => UnityLeanMcpPaths.GetResultFilePath(OperationKinds.Refresh, operationId));
        Assert.Throws<ArgumentException>(() => UnityLeanMcpPaths.GetResultFilePath(OperationKinds.Recompile, operationId));
        Assert.Throws<ArgumentException>(() => UnityLeanMcpPaths.GetResultFilePath(OperationKinds.Eval, operationId));
        Assert.Throws<ArgumentException>(() => UnityLeanMcpPaths.GetResultFilePath(OperationKinds.Test, operationId));
    }

    [Theory]
    [MemberData(nameof(InvalidOperationIds))]
    public void UnityCommandGate_TryBegin_ReturnsInvalidBeforeAdmission(string operationId)
    {
        var result = UnityCommandGate.TryBegin(OperationKinds.Eval, operationId, OperationStatus.Requested, out string busyReason);

        Assert.Equal(BeginGateResult.Invalid, result);
        Assert.Equal("invalid", busyReason);
        Assert.Null(UnityCommandGate.ReadSnapshot());
    }

    [Fact]
    public void UnityCommandGate_InitializeMainThread_DiscardsInvalidPersistedPendingResultInsteadOfLooping()
    {
        SessionState.SetString("UnityLeanMcp_ActiveOp", "{\"Kind\":\"eval\",\"OperationId\":\"../evil\",\"Status\":\"Completed\"}");
        SessionState.SetString("UnityLeanMcp_PendingResult", "{\"Kind\":\"eval\",\"OperationId\":\"../evil\",\"Path\":\"C:/bad/path.json\",\"Json\":\"{}\"}");

        UnityCommandGate.InitializeMainThread();

        Assert.Null(UnityCommandGate.ReadSnapshot());
        Assert.False(UnityCommandGate.HasPendingResult("../evil"));
        Assert.True(string.IsNullOrEmpty(SessionState.GetString("UnityLeanMcp_ActiveOp", "")));
        Assert.True(string.IsNullOrEmpty(SessionState.GetString("UnityLeanMcp_PendingResult", "")));
    }

    [Fact]
    public void UnityCommandGate_RetryPendingResult_DiscardsInvalidPendingResultInsteadOfLooping()
    {
        Type pendingType = typeof(UnityCommandGate).GetNestedType("PendingResult", BindingFlags.NonPublic)!;
        object pending = Activator.CreateInstance(pendingType)!;
        pendingType.GetField("Kind")!.SetValue(pending, OperationKinds.Eval);
        pendingType.GetField("OperationId")!.SetValue(pending, "../evil");
        pendingType.GetField("Path")!.SetValue(pending, Path.Combine(UnityLeanMcpPaths.TempDir, "evil.json"));
        pendingType.GetField("Json")!.SetValue(pending, "{}");
        typeof(UnityCommandGate).GetField("s_PendingResult", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, pending);

        MethodInfo retryMethod = typeof(UnityCommandGate).GetMethod("RetryPendingResult", BindingFlags.NonPublic | BindingFlags.Static)!;
        retryMethod.Invoke(null, null);

        Assert.False(UnityCommandGate.HasPendingResult("../evil"));
        Assert.Null(UnityCommandGate.ReadSnapshot());
    }

    [Theory]
    [MemberData(nameof(InvalidOperationIds))]
    public void CompilationCommandHandlerBase_RejectsInvalidOperationIdBeforePreAdmissionProbe(string operationId)
    {
        var handler = new CompilationProbe(OperationKinds.Refresh);
        using var stream = new MemoryStream();
        using var writer = new StreamWriter(stream) { AutoFlush = true };

        handler.Handle(operationId, writer);

        string response = Encoding.UTF8.GetString(stream.ToArray()).Trim();
        Assert.Equal("ERROR: Missing or invalid operation id", response);
        Assert.Equal(0, handler.Executions);
        Assert.Null(UnityCommandGate.ReadSnapshot());
    }

    [Theory]
    [MemberData(nameof(InvalidOperationIds))]
    public void EvalHandler_RejectsInvalidOperationIdBeforePreAdmissionProbe(string operationId)
    {
        var handler = new EvalHandler();
        using var stream = new MemoryStream();
        using var writer = new StreamWriter(stream) { AutoFlush = true };

        handler.Handle($"{operationId} 1+1", writer);

        string response = Encoding.UTF8.GetString(stream.ToArray()).Trim();
        Assert.Equal("ERROR: Missing or invalid operation id", response);
        Assert.Null(UnityCommandGate.ReadSnapshot());
    }

    [Theory]
    [MemberData(nameof(InvalidOperationIds))]
    public void RunTestsHandler_RejectsInvalidOperationIdBeforePreAdmissionProbe(string operationId)
    {
        var handler = new RunTestsHandler();
        using var stream = new MemoryStream();
        using var writer = new StreamWriter(stream) { AutoFlush = true };

        handler.Handle($"{operationId} {{\"mode\":\"editmode\"}}", writer);

        string response = Encoding.UTF8.GetString(stream.ToArray()).Trim();
        Assert.Equal("ERROR: Missing or invalid operation id", response);
        Assert.Null(UnityCommandGate.ReadSnapshot());
    }

    [Theory]
    [MemberData(nameof(InvalidOperationIds))]
    public void PollHandlers_RejectInvalidOperationIdWithoutPathTraversal(string operationId)
    {
        using (var evalStream = new MemoryStream())
        using (var evalWriter = new StreamWriter(evalStream) { AutoFlush = true })
        {
            new PollEvalHandler().Handle(operationId, evalWriter);
            Assert.Equal("ERROR: Missing or invalid operation id", Encoding.UTF8.GetString(evalStream.ToArray()).Trim());
        }

        using (var testStream = new MemoryStream())
        using (var testWriter = new StreamWriter(testStream) { AutoFlush = true })
        {
            new PollTestsHandler().Handle(operationId, testWriter);
            Assert.Equal("ERROR: Missing or invalid operation id", Encoding.UTF8.GetString(testStream.ToArray()).Trim());
        }

        using (var refreshStream = new MemoryStream())
        using (var refreshWriter = new StreamWriter(refreshStream) { AutoFlush = true })
        {
            new PollRefreshHandler().Handle(operationId, refreshWriter);
            Assert.Equal("ERROR Invalid refresh operation id.", Encoding.UTF8.GetString(refreshStream.ToArray()).Trim());
        }

        Assert.False(UnityLeanMcpCompilationTracker.TryReadRefreshResultThreadSafe(operationId, out _));
    }

    [Theory]
    [MemberData(nameof(InvalidOperationIds))]
    public void CancelOperationHandler_RejectsInvalidOperationIdWithoutPathTraversal(string operationId)
    {
        Assert.Equal(BeginGateResult.Started, UnityCommandGate.TryBegin(OperationKinds.Eval, "valid-active-op", OperationStatus.Running, out _));
        using var stream = new MemoryStream();
        using var writer = new StreamWriter(stream) { AutoFlush = true };

        new CancelOperationHandler().Handle(operationId, writer);

        string response = Encoding.UTF8.GetString(stream.ToArray()).Trim();
        Assert.Equal("ERROR: Missing or invalid operation id", response);
        Assert.False(CancelOperationHandler.IsTerminalInterruptedResult(operationId));
        Assert.False(UnityCommandGate.IsCancellationRequested(operationId));
        Assert.True(UnityCommandGate.IsOwnedBy("valid-active-op", OperationKinds.Eval));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("op-1", "op-1")]
    [InlineData("  op-1  ", "op-1")]
    [InlineData("op-1 1+1", "op-1")]
    [InlineData("  op-1 1+1", "op-1")]
    [InlineData("   op-1   1+1   ", "op-1")]
    [InlineData("\top-1 1+1\t", "op-1")]
    public void CommandHelper_ExtractOperationId_TrimsWhitespaceBeforeSplitting(string? payload, string? expected)
    {
        Assert.Equal(expected, CommandHelper.ExtractOperationId(payload!));
    }

    [Theory]
    [InlineData("  eval-op-1 1+1")]
    [InlineData("   eval-op-1   1+1   ")]
    [InlineData("\teval-op-1 1+1\t")]
    public void EvalHandler_Handle_TrimsLeadingAndTrailingWhitespaceBeforeSplitting(string payload)
    {
        Assert.Equal(BeginGateResult.Started, UnityCommandGate.TryBegin(OperationKinds.Eval, "eval-op-1", OperationStatus.Running, out _));
        var handler = new EvalHandler();
        using var stream = new MemoryStream();
        using var writer = new StreamWriter(stream) { AutoFlush = true };

        handler.Handle(payload, writer);

        string response = Encoding.UTF8.GetString(stream.ToArray()).Trim();
        Assert.Equal("RUNNING", response);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("  eval-op-1   ")]
    public void EvalHandler_Handle_RejectsMissingCodeSnippetAfterTrimming(string? payload)
    {
        var handler = new EvalHandler();
        using var stream = new MemoryStream();
        using var writer = new StreamWriter(stream) { AutoFlush = true };

        handler.Handle(payload!, writer);

        string response = Encoding.UTF8.GetString(stream.ToArray()).Trim();
        Assert.Equal("ERROR: Missing operation id or code snippet", response);
    }

    [Theory]
    [InlineData("  test-op-1 {\"mode\":\"editmode\"}")]
    [InlineData("   test-op-1   {\"mode\":\"editmode\"}   ")]
    public void RunTestsHandler_Handle_TrimsLeadingAndTrailingWhitespaceBeforeSplitting(string payload)
    {
        Assert.Equal(BeginGateResult.Started, UnityCommandGate.TryBegin(OperationKinds.Test, "test-op-1", OperationStatus.Running, out _));
        var handler = new RunTestsHandler();
        using var stream = new MemoryStream();
        using var writer = new StreamWriter(stream) { AutoFlush = true };

        handler.Handle(payload, writer);

        string response = Encoding.UTF8.GetString(stream.ToArray()).Trim();
        Assert.Equal("RUNNING", response);
    }

    [Theory]
    [InlineData("   ")]
    [InlineData("  test-op-1   ")]
    public void RunTestsHandler_Handle_RejectsMissingTestParametersAfterTrimming(string payload)
    {
        var handler = new RunTestsHandler();
        using var stream = new MemoryStream();
        using var writer = new StreamWriter(stream) { AutoFlush = true };

        handler.Handle(payload, writer);

        string response = Encoding.UTF8.GetString(stream.ToArray()).Trim();
        Assert.Equal("ERROR: Missing operation id or test parameters", response);
    }

    private sealed class CompilationProbe(string kind) : CompilationCommandHandlerBase
    {
        public int Executions { get; private set; }
        protected override string OperationKind => kind;
        protected override string StatusWord => "REFRESHING";
        protected override void ExecuteCompilation() => Executions++;
    }

    public void Dispose()
    {
        UnityCommandGate.ForceReset();
        SessionState.EraseString("UnityLeanMcp_PendingResult");
        RunTestsHandler.Active = false;
        CancelOperationHandler.CancelRouteHook = null;
        UnityCommandGate.WriteAtomicHook = null;
        UnityLeanMcpDispatcher.Clear();
        UnityLeanMcpPaths.ProjectRoot = _previousProjectRoot;
        Directory.Delete(_root, true);
    }
}
