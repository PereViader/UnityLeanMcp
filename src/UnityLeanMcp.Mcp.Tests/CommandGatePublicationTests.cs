using System.Reflection;
using UnityEditor;
using UnityLeanMcp;

namespace UnityLeanMcp.Mcp.Tests;

public sealed class CommandGatePublicationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    private readonly string _previousProjectRoot = UnityLeanMcpPaths.ProjectRoot;
    public CommandGatePublicationTests()
    {
        Directory.CreateDirectory(_root);
        UnityLeanMcpPaths.ProjectRoot = _root;
        UnityCommandGate.ForceReset();
        UnityCommandGate.RetryThrottleMs = 0;
        SessionState.EraseString("UnityLeanMcp_PendingResult");
        RunTestsHandler.ActiveStateKnown = true;
    }

    [Theory]
    [InlineData("refresh")]
    [InlineData("recompile")]
    public void BrokenCompilationAcknowledgementDoesNotSkipAcceptedWork(string kind)
    {
        string operationId = Guid.NewGuid().ToString("N");
        var handler = new CompilationProbe(kind);
        using var stream = new MemoryStream();
        using var writer = new BrokenAcknowledgementWriter(stream);
        // The network writer fails after admission. The real handler still owns
        // the operation and must trigger its backing work exactly once.
        try { handler.Handle(operationId, writer); }
        catch (IOException) { }
        Assert.Equal(1, handler.Executions);
        Assert.True(UnityCommandGate.IsOwnedBy(operationId, kind));
    }

    [Theory]
    [InlineData("refresh")]
    [InlineData("recompile")]
    public void RequestedCompilationReloadPublishesInterruptionInsteadOfRemainingOwned(string kind)
    {
        string? published = null;
        UnityLeanMcpOperationStore.Write = (_, json, _) => published = json;
        Assert.Equal(BeginGateResult.Started, UnityCommandGate.TryBegin(kind, "not-started", "Requested", out _));
        typeof(UnityCommandGate).GetField("s_Snapshot", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, null);

        UnityCommandGate.InitializeMainThread();

        Assert.NotNull(published);
        using var document = System.Text.Json.JsonDocument.Parse(published);
        Assert.Equal("not-started", document.RootElement.GetProperty("operationId").GetString());
        Assert.True(document.RootElement.GetProperty("interrupted").GetBoolean());
        Assert.False(document.RootElement.GetProperty("success").GetBoolean());
        Assert.Null(UnityCommandGate.ReadSnapshot());
    }

    private sealed class CompilationProbe(string kind) : PereViader.UnityLeanMcp.Editor.CompilationCommandHandlerBase
    {
        public int Executions { get; private set; }
        protected override string OperationKind => kind;
        protected override string StatusWord => "REFRESHING";
        protected override void ExecuteCompilation() => Executions++;
    }
    private sealed class BrokenAcknowledgementWriter(Stream stream) : StreamWriter(stream)
    {
        public override void Flush() => throw new IOException("socket closed before acknowledgement");
    }

    [Fact]
    public void FailedPublicationWritesWarningToProjectDiagnosticsLog()
    {
        string operationId = Guid.NewGuid().ToString("N");
        UnityLeanMcpOperationStore.Write = (_, _, _) => throw new IOException("locked result");
        Assert.Equal(BeginGateResult.Started, UnityCommandGate.TryBegin("eval", operationId, "running", out _));

        UnityCommandGate.PublishResult("eval", operationId, Path.Combine(_root, "result.json"), "completed payload");

        string diagnostics = File.ReadAllText(UnityLeanMcpPaths.LogFile);
        Assert.Contains("[WARNING]", diagnostics);
        Assert.Contains("Terminal result publication will retry for eval " + operationId, diagnostics);
        Assert.Contains("locked result", diagnostics);
        Assert.True(UnityCommandGate.HasPendingResult(operationId));
        Assert.True(UnityCommandGate.IsOwnedBy(operationId, "eval"));
    }

    [Fact]
    public void FailedPublicationHoldsOwnershipAndRetriesExactResultAfterReload()
    {
        string path = Path.Combine(_root, "result.json");
        int attempts = 0;
        UnityLeanMcpOperationStore.Write = (_, _, _) => { attempts++; throw new IOException("locked"); };
        Assert.Equal(BeginGateResult.Started, UnityCommandGate.TryBegin("eval", "owner", "running", out _));
        UnityCommandGate.PublishResult("eval", "owner", path, "completed payload");
        Assert.Equal(BeginGateResult.Busy, UnityCommandGate.TryBegin("eval", "next", "running", out _));
        EditorApplication.Tick();
        Assert.Equal(2, attempts);

        // Simulate lost managed state while native SessionState survives.
        typeof(UnityCommandGate).GetField("s_PendingResult", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, null);
        typeof(UnityCommandGate).GetField("s_Snapshot", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, null);
        UnityCommandGate.InitializeMainThread();
        Assert.True(UnityCommandGate.IsOwnedBy("owner", "eval"));
        UnityLeanMcpOperationStore.Write = (p, json, _) => File.WriteAllText(p, json);
        EditorApplication.Tick();
        Assert.Equal("completed payload", File.ReadAllText(path));
        Assert.Null(UnityCommandGate.ReadSnapshot());
        Assert.Equal(BeginGateResult.Started, UnityCommandGate.TryBegin("eval", "next", "running", out _));
    }

    [Fact]
    public void ReloadWaitsForTestFrameworkInitializerToRestoreRunningJobs()
    {
        string? published = null;
        UnityLeanMcpOperationStore.Write = (_, json, _) => published = json;
        UnityCommandGate.TryBegin("test", "resuming-run", "running", out _);

        // IsRunActive reports false until the framework's load initializer
        // rebuilds its nonserialized runner registry from its persisted jobs.
        RunTestsHandler.Active = false;
        UnityCommandGate.InitializeMainThread();
        Assert.Null(published);
        Assert.True(UnityCommandGate.IsOwnedBy("resuming-run", "test"));

        RunTestsHandler.Active = true;
        EditorApplication.Tick();
        Assert.Null(published);
        Assert.True(UnityCommandGate.IsOwnedBy("resuming-run", "test"));
    }

    [Fact]
    public void ReloadPublishesInterruptionWhenFrameworkHasNoSurvivingRun()
    {
        string? published = null;
        UnityLeanMcpOperationStore.Write = (_, json, _) => published = json;
        UnityCommandGate.TryBegin("test", "lost-run", "running", out _);
        UnityCommandGate.InitializeMainThread();
        Assert.Null(published);

        EditorApplication.Tick();
        Assert.Contains("Interrupted", published);
        Assert.Null(UnityCommandGate.ReadSnapshot());
    }

    [Fact]
    public void ReloadDoesNotDeclareUnknownTestRunnerStateInterrupted()
    {
        UnityLeanMcpOperationStore.Write = (_, _, _) => throw new InvalidOperationException("Must not publish");
        UnityCommandGate.TryBegin("test", "run", "running", out _);
        RunTestsHandler.ActiveStateKnown = false;
        UnityCommandGate.InitializeMainThread();
        Assert.True(UnityCommandGate.IsOwnedBy("run", "test"));
        string? published = null;
        UnityLeanMcpOperationStore.Write = (_, json, _) => published = json;
        RunTestsHandler.ActiveStateKnown = true;
        EditorApplication.Tick();
        Assert.Contains("Interrupted", published);
        Assert.Null(UnityCommandGate.ReadSnapshot());
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void ExternalOrUnknownTestRunBlocksAdmission(bool known, bool active)
    {
        RunTestsHandler.ActiveStateKnown = known;
        RunTestsHandler.Active = active;
        Assert.Equal(BeginGateResult.Busy, UnityCommandGate.TryBegin("eval", "next", "running", out var reason));
        Assert.Equal("test", reason);
        RunTestsHandler.ActiveStateKnown = true;
        RunTestsHandler.Active = false;
        Assert.Equal(BeginGateResult.Started, UnityCommandGate.TryBegin("eval", "next", "running", out _));
    }

    [Fact]
    public void RetryPendingResultPacesRetriesWhenThrottled()
    {
        string path = Path.Combine(_root, "result_paced.json");
        int attempts = 0;
        UnityLeanMcpOperationStore.Write = (_, _, _) => { attempts++; throw new IOException("locked"); };
        UnityCommandGate.RetryThrottleMs = 5000;

        Assert.Equal(BeginGateResult.Started, UnityCommandGate.TryBegin("eval", "paced_owner", "running", out _));
        UnityCommandGate.PublishResult("eval", "paced_owner", path, "payload");
        // First attempt ran immediately during PublishResult
        Assert.Equal(1, attempts);

        // Immediate tick should be throttled because 5000ms has not elapsed
        EditorApplication.Tick();
        Assert.Equal(1, attempts);

        // With RetryThrottleMs = 0, next tick executes immediately
        UnityCommandGate.RetryThrottleMs = 0;
        EditorApplication.Tick();
        Assert.Equal(2, attempts);
    }

    [Fact]
    public void InitializeMainThreadErasesPendingResultOnNullOrInvalidSession()
    {
        // Case A: No active session in SessionState, but stale pending result exists
        SessionState.EraseString("UnityLeanMcp_ActiveOp");
        SessionState.SetString("UnityLeanMcp_PendingResult", "{\"Kind\":\"eval\",\"OperationId\":\"stale\",\"Path\":\"dummy\",\"Json\":\"{}\"}");
        UnityCommandGate.InitializeMainThread();
        Assert.Null(SessionState.GetString("UnityLeanMcp_PendingResult", null));

        // Case B: Invalid/empty session in SessionState, but stale pending result exists
        SessionState.SetString("UnityLeanMcp_ActiveOp", "{\"Kind\":\"\",\"OperationId\":\"\"}");
        SessionState.SetString("UnityLeanMcp_PendingResult", "{\"Kind\":\"eval\",\"OperationId\":\"stale2\",\"Path\":\"dummy\",\"Json\":\"{}\"}");
        UnityCommandGate.InitializeMainThread();
        Assert.Null(SessionState.GetString("UnityLeanMcp_ActiveOp", null));
        Assert.Null(SessionState.GetString("UnityLeanMcp_PendingResult", null));
    }

    public void Dispose()
    {
        UnityCommandGate.ForceReset();
        UnityCommandGate.RetryThrottleMs = 100;
        SessionState.EraseString("UnityLeanMcp_PendingResult");
        RunTestsHandler.ActiveStateKnown = true;
        RunTestsHandler.Active = false;
        UnityLeanMcpCompilationTracker.RefreshPending = false;
        UnityLeanMcpCompilationTracker.CompilationRequested = false;
        UnityLeanMcpPaths.ProjectRoot = _previousProjectRoot;
        Directory.Delete(_root, true);
    }
}
