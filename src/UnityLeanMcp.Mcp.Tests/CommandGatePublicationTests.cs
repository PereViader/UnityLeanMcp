using System.Reflection;
using UnityEditor;
using UnityLeanMcp;

namespace UnityLeanMcp.Mcp.Tests;

[Trait("Category", "Unit")]
[Collection("CommandGate")]
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
        RunTestsHandler.Active = false;
        RunTestsHandler.CancelledRunIds.Clear();
        CancelOperationHandler.CancelRouteHook = null;
        UnityCommandGate.WriteAtomicHook = null;
        UnityLeanMcpDispatcher.Clear();
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
        UnityCommandGate.WriteAtomicHook = (_, json, _) => published = json;
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompletedTestsRestoreCoverageBeforePublicationAndRetainPendingPayloadAcrossReload(bool previouslyEnabled)
    {
        const string runId = "coverage-complete";
        const string payload = "exact test result";
        string path = Path.Combine(_root, "coverage-result.json");
        UnityEngine.TestTools.Coverage.enabled = previouslyEnabled;
        Assert.Equal(BeginGateResult.Started, UnityCommandGate.TryBegin("test", runId, "running", out _));
        RunTestsHandler.InitializeCoverage(runId);
        UnityCommandGate.WriteAtomicHook = (_, _, _) =>
        {
            Assert.Equal(previouslyEnabled, UnityEngine.TestTools.Coverage.enabled);
            Assert.NotEmpty(SessionState.GetString(GetCoverageHandler.CapturedCoverageKey, "")!);
            throw new IOException("locked result");
        };

        UnityCommandGate.PublishResult("test", runId, path, payload);

        Assert.Equal(previouslyEnabled, UnityEngine.TestTools.Coverage.enabled);
        Assert.Equal("", SessionState.GetString("UnityLeanMcp_CoverageOwner", ""));
        Assert.True(UnityCommandGate.IsOwnedBy(runId, "test"));
        Assert.True(UnityCommandGate.HasPendingResult(runId));
        string captured = SessionState.GetString(GetCoverageHandler.CapturedCoverageKey, "")!;
        typeof(UnityCommandGate).GetField("s_PendingResult", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, null);
        typeof(UnityCommandGate).GetField("s_Snapshot", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, null);
        UnityCommandGate.InitializeMainThread();
        Assert.Equal(captured, SessionState.GetString(GetCoverageHandler.CapturedCoverageKey, "")!);
        Assert.True(UnityCommandGate.IsOwnedBy(runId, "test"));
        UnityCommandGate.WriteAtomicHook = (p, json, _) => File.WriteAllText(p, json);
        EditorApplication.Tick();
        Assert.Equal(payload, File.ReadAllText(path));
        Assert.Null(UnityCommandGate.ReadSnapshot());
    }

    [Fact]
    public void CoverageCaptureFailureRestoresProfilerAndDoesNotLosePendingTestResult()
    {
        const string runId = "capture-error";
        UnityEngine.TestTools.Coverage.enabled = false;
        UnityCommandGate.TryBegin("test", runId, "running", out _);
        RunTestsHandler.InitializeCoverage(runId);
        UnityEditor.Compilation.CompilationPipeline.GetAssembliesFunc = () => throw new InvalidOperationException("capture failed");
        string path = Path.Combine(_root, "capture-error.json");
        UnityCommandGate.PublishResult("test", runId, path, "terminal payload");
        Assert.False(UnityEngine.TestTools.Coverage.enabled);
        Assert.True(UnityCommandGate.HasPendingResult(runId));
        Assert.True(UnityCommandGate.IsOwnedBy(runId, "test"));
        UnityCommandGate.WriteAtomicHook = (p, json, _) => File.WriteAllText(p, json);
        EditorApplication.Tick();
        Assert.Equal("terminal payload", File.ReadAllText(path));
        Assert.Null(UnityCommandGate.ReadSnapshot());
    }

    [Fact]
    public void CoverageCaptureAndQueryPreserveCaseDistinctSourcePathsOnUnix()
    {
        var method = typeof(CommandGatePublicationTests).GetMethod(nameof(CoverageCaptureAndQueryPreserveCaseDistinctSourcePathsOnUnix))!;
        UnityEditor.Compilation.CompilationPipeline.GetAssembliesFunc = () => new[]
        {
            new UnityEditor.Compilation.Assembly
            {
                name = typeof(CommandGatePublicationTests).Assembly.GetName().Name!,
                sourceFiles = new[] { "Assets/Foo.cs", "Assets/foo.cs", "Assets/Empty.cs", "Assets/empty.cs" }
            }
        };
        UnityEngine.TestTools.Coverage.enabled = true;
        UnityEngine.TestTools.Coverage.GetSequencePointsForFunc = m => m == method ? new[]
        {
            new UnityEngine.TestTools.CoveredSequencePoint { filename = "Assets/Foo.cs", line = 1, hitCount = 1 },
            new UnityEngine.TestTools.CoveredSequencePoint { filename = "Assets/foo.cs", line = 2, hitCount = 0 }
        } : Array.Empty<UnityEngine.TestTools.CoveredSequencePoint>();
        GetCoverageHandler.ClearCapturedCoverage();
        GetCoverageHandler.CaptureCoverage("case-distinct");
        using var document = System.Text.Json.JsonDocument.Parse(SessionState.GetString(GetCoverageHandler.CapturedCoverageKey, "")!);
        var reports = document.RootElement.GetProperty("files").EnumerateArray().ToArray();
        bool windows = OperatingSystem.IsWindows();
        Assert.Equal(windows ? 2 : 4, reports.Length);
        if (!windows)
        {
            Assert.Equal(1, reports.Single(r => r.GetProperty("path").GetString() == "Assets/Foo.cs").GetProperty("coveredPoints").GetInt32());
            Assert.Equal(0, reports.Single(r => r.GetProperty("path").GetString() == "Assets/foo.cs").GetProperty("coveredPoints").GetInt32());
        }
        Assert.Equal(windows, GetCoverageHandler.MatchesAnyFilter("Assets/foo.cs", new List<string> { "Assets/Foo.cs" }));
        Assert.Equal(windows, GetCoverageHandler.MatchesAnyFilter("Assets/child/Foo.cs", new List<string> { "Assets/Child/" }));
        using var stream = new MemoryStream();
        using var writer = new StreamWriter(stream) { AutoFlush = true };
        GetCoverageHandler.WriteCoverage(new List<string> { "Assets/Foo.cs" }, _root, writer);
        string response = System.Text.Encoding.UTF8.GetString(stream.ToArray()).TrimStart('\uFEFF').TrimEnd();
        using var query = System.Text.Json.JsonDocument.Parse(ProtocolCodec.UnescapeLine(response.Substring("SUCCESS ".Length)));
        Assert.Single(query.RootElement.GetProperty("files").EnumerateArray());
    }

    [Fact]
    public void FailedPublicationWritesWarningToProjectDiagnosticsLog()
    {
        string operationId = Guid.NewGuid().ToString("N");
        UnityCommandGate.WriteAtomicHook = (_, _, _) => throw new IOException("locked result");
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
        UnityCommandGate.WriteAtomicHook = (_, _, _) => { attempts++; throw new IOException("locked"); };
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
        UnityCommandGate.WriteAtomicHook = (p, json, _) => File.WriteAllText(p, json);
        EditorApplication.Tick();
        Assert.Equal("completed payload", File.ReadAllText(path));
        Assert.Null(UnityCommandGate.ReadSnapshot());
        Assert.Equal(BeginGateResult.Started, UnityCommandGate.TryBegin("eval", "next", "running", out _));
    }

    [Fact]
    public void ReloadWaitsForTestFrameworkInitializerToRestoreRunningJobs()
    {
        string? published = null;
        UnityCommandGate.WriteAtomicHook = (_, json, _) => published = json;
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
        UnityCommandGate.WriteAtomicHook = (_, json, _) => published = json;
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
        UnityCommandGate.WriteAtomicHook = (_, _, _) => throw new InvalidOperationException("Must not publish");
        UnityCommandGate.TryBegin("test", "run", "running", out _);
        RunTestsHandler.ActiveStateKnown = false;
        UnityCommandGate.InitializeMainThread();
        Assert.True(UnityCommandGate.IsOwnedBy("run", "test"));
        string? published = null;
        UnityCommandGate.WriteAtomicHook = (_, json, _) => published = json;
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
        UnityCommandGate.WriteAtomicHook = (_, _, _) => { attempts++; throw new IOException("locked"); };
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

    [Fact]
    public void UnityCommandGate_DoesNotCarryInitializeOnLoadAttributes()
    {
        Assert.Null(typeof(UnityCommandGate).GetCustomAttribute<InitializeOnLoadAttribute>());
        foreach (var method in typeof(UnityCommandGate).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
        {
            Assert.Null(method.GetCustomAttribute<InitializeOnLoadMethodAttribute>());
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReloadPublishesCancelledWhenTestRunWasCancelledBeforeReload(bool viaCancellingStatus)
    {
        string? published = null;
        UnityCommandGate.WriteAtomicHook = (_, json, _) => published = json;
        string initialStatus = viaCancellingStatus ? OperationStatus.Cancelling : OperationStatus.Running;
        if (!viaCancellingStatus)
        {
            RunTestsHandler.CancelledRunIds.Add("cancelled-run");
        }

        Assert.Equal(BeginGateResult.Started, UnityCommandGate.TryBegin("test", "cancelled-run", initialStatus, out _));
        UnityCommandGate.InitializeMainThread();
        Assert.Null(published);

        EditorApplication.Tick();
        Assert.NotNull(published);
        using var document = System.Text.Json.JsonDocument.Parse(published);
        Assert.Equal("cancelled-run", document.RootElement.GetProperty("operationId").GetString());
        Assert.Equal(OperationStatus.Cancelled, document.RootElement.GetProperty("resultState").GetString());
        Assert.True(document.RootElement.GetProperty("interrupted").GetBoolean());
        Assert.Null(UnityCommandGate.ReadSnapshot());
    }

    [Fact]
    public void WriteInterruptedResultEscapesSpecialCharactersAndSetsInterruptedStatus()
    {
        string? published = null;
        string? observedStatusDuringWrite = null;
        UnityCommandGate.WriteAtomicHook = (_, json, _) =>
        {
            observedStatusDuringWrite = UnityCommandGate.ReadSnapshot()?.Status;
            published = json;
        };

        Assert.Equal(BeginGateResult.Started, UnityCommandGate.TryBegin("eval", "op-escape", OperationStatus.Running, out _));
        var writeInterrupted = typeof(UnityCommandGate).GetMethod("WriteInterruptedResult", BindingFlags.NonPublic | BindingFlags.Static)!;
        string specialMessage = "Line 1 \"quoted\"\nLine 2 \\backslash\tTab";
        writeInterrupted.Invoke(null, new object[] { "eval", "op-escape", specialMessage });

        Assert.Equal(OperationStatus.Interrupted, observedStatusDuringWrite);
        Assert.NotNull(published);
        using var document = System.Text.Json.JsonDocument.Parse(published);
        Assert.Equal("op-escape", document.RootElement.GetProperty("operationId").GetString());
        Assert.Equal(specialMessage, document.RootElement.GetProperty("message").GetString());
        Assert.True(document.RootElement.GetProperty("interrupted").GetBoolean());
        Assert.False(document.RootElement.GetProperty("success").GetBoolean());
    }

    [Theory]
    [InlineData("{malformed json")]
    [InlineData("{\"Kind\":\"refresh\",\"OperationId\":\"active-op\",\"Path\":\"\",\"Json\":\"{}\"}")]
    [InlineData("{\"Kind\":\"\",\"OperationId\":\"active-op\",\"Path\":\"dummy.json\",\"Json\":\"{}\"}")]
    public void InitializeMainThreadIgnoresAndErasesMalformedOrIncompletePendingResult(string invalidPendingJson)
    {
        Assert.Equal(BeginGateResult.Started, UnityCommandGate.TryBegin("refresh", "active-op", OperationStatus.Refreshing, out _));
        SessionState.SetString("UnityLeanMcp_PendingResult", invalidPendingJson);

        UnityCommandGate.InitializeMainThread();

        Assert.False(UnityCommandGate.HasPendingResult("active-op"));
        Assert.Null(SessionState.GetString("UnityLeanMcp_PendingResult", null));
        Assert.True(UnityCommandGate.IsOwnedBy("active-op", "refresh"));
    }

    [Fact]
    public void CompleteClearsPendingResultUnderLockBeforeReleasingOwnership()
    {
        string path = Path.Combine(_root, "result_complete.json");
        UnityCommandGate.WriteAtomicHook = (_, _, _) => throw new IOException("locked");
        Assert.Equal(BeginGateResult.Started, UnityCommandGate.TryBegin("eval", "owner-complete", OperationStatus.Running, out _));
        UnityCommandGate.PublishResult("eval", "owner-complete", path, "{}");
        Assert.True(UnityCommandGate.HasPendingResult("owner-complete"));

        UnityCommandGate.Complete("owner-complete");

        Assert.False(UnityCommandGate.HasPendingResult("owner-complete"));
        Assert.Null(SessionState.GetString("UnityLeanMcp_PendingResult", null));
        Assert.Null(UnityCommandGate.ReadSnapshot());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void CancelOperationHandler_EmptyOrWhitespacePayload_ReturnsNoOperationEvenWhenForeignOperationActive(string payload)
    {
        Assert.Equal(BeginGateResult.Started, UnityCommandGate.TryBegin("eval", "foreign-active-op", OperationStatus.Running, out _));
        using var stream = new MemoryStream();
        using var writer = new StreamWriter(stream);
        var handler = new CancelOperationHandler();

        handler.Handle(payload, writer);

        string response = System.Text.Encoding.UTF8.GetString(stream.ToArray()).Trim();
        Assert.Equal("NO_OPERATION", response);
        Assert.True(UnityCommandGate.IsOwnedBy("foreign-active-op", "eval"));
    }

    [Fact]
    public void UnityLeanMcpDispatcher_Clear_DrainsQueuedCallbacksBeforeUpdate()
    {
        int executed = 0;
        UnityLeanMcpDispatcher.Enqueue(() => executed++);
        UnityLeanMcpDispatcher.Enqueue(() => executed++);

        UnityLeanMcpDispatcher.Clear();
        UnityLeanMcpDispatcher.Update();

        Assert.Equal(0, executed);
    }

    [Fact]
    public void EvalHandler_WhenRefreshRequiredIsTrue_RejectsInEditModeAndBypassesInPlayMode()
    {
        UnityLeanMcpCompilationTracker.IsCompiling = false;
        UnityLeanMcpCompilationTracker.RefreshPending = false;
        UnityLeanMcpCompilationTracker.ScriptCompilationFailed = false;
        UnityLeanMcpCompilationTracker.RefreshRequired = true;

        var handler = new EvalHandler();

        // In EditMode, RefreshRequired must reject with BUSY compile
        UnityLeanMcpServer.IsPlayModeActive = false;
        using (var editStream = new MemoryStream())
        using (var editWriter = new StreamWriter(editStream) { AutoFlush = true })
        {
            handler.Handle("eval-edit return 1;", editWriter);
            string editResponse = System.Text.Encoding.UTF8.GetString(editStream.ToArray()).Trim();
            Assert.Equal("BUSY compile", editResponse);
            Assert.Null(UnityCommandGate.ReadSnapshot());
        }

        // In PlayMode, RefreshRequired is bypassed so PlayMode state is not interrupted
        UnityLeanMcpServer.IsPlayModeActive = true;
        using (var playStream = new MemoryStream())
        using (var playWriter = new StreamWriter(playStream) { AutoFlush = true })
        {
            handler.Handle("eval-play return 1;", playWriter);
            string playResponse = System.Text.Encoding.UTF8.GetString(playStream.ToArray()).Trim();
            Assert.NotEqual("BUSY compile", playResponse);
        }

        UnityCommandGate.ForceReset();

        // Even in PlayMode, active compilation or pending refresh must still reject with BUSY compile
        UnityLeanMcpCompilationTracker.IsCompiling = true;
        using (var compilingStream = new MemoryStream())
        using (var compilingWriter = new StreamWriter(compilingStream) { AutoFlush = true })
        {
            handler.Handle("eval-play-compiling return 1;", compilingWriter);
            string compilingResponse = System.Text.Encoding.UTF8.GetString(compilingStream.ToArray()).Trim();
            Assert.Equal("BUSY compile", compilingResponse);
        }
    }

    [Fact]
    public void ObserveOperationUntilSettled_DoesNotRewriteSessionStateWhenAlreadyWaitingForUnity()
    {
        Assert.Equal(BeginGateResult.Started, UnityCommandGate.TryBegin(OperationKinds.Refresh, "refresh-op-1", OperationStatus.Refreshing, out _));
        UnityLeanMcpCompilationTracker.RefreshPending = true;

        int countBeforeFirstTick = SessionState.SetStringCount;
        UnityLeanMcpCompilationTracker.ObserveOperationUntilSettled();

        var firstSnapshot = UnityCommandGate.ReadSnapshot();
        Assert.NotNull(firstSnapshot);
        Assert.Equal(OperationStatus.WaitingForUnity, firstSnapshot.Status);
        Assert.Equal(countBeforeFirstTick + 1, SessionState.SetStringCount);

        // Subsequent busy ticks must not rewrite SessionState or allocate a new snapshot
        UnityLeanMcpCompilationTracker.ObserveOperationUntilSettled();
        UnityLeanMcpCompilationTracker.ObserveOperationUntilSettled();
        UnityLeanMcpCompilationTracker.ObserveOperationUntilSettled();

        Assert.Equal(countBeforeFirstTick + 1, SessionState.SetStringCount);
        Assert.Same(firstSnapshot, UnityCommandGate.ReadSnapshot());
    }

    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(FileNotFoundException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    public void PollRefreshHandler_WhenDiagnosticsFileThrowsContentionException_ReturnsCompiling(Type exceptionType)
    {
        UnityLeanMcpCompilationTracker.ScriptCompilationFailed = true;
        string diagnosticsPath = UnityLeanMcpPaths.DiagnosticsFile;
        Directory.CreateDirectory(Path.GetDirectoryName(diagnosticsPath)!);
        File.WriteAllText(diagnosticsPath, "Assets/Broken.cs(1,1): error CS1002: ; expected");

        var handler = new PollRefreshHandler();

        // Without contention, non-empty diagnostics file returns COMPILATION_ERROR
        PollRefreshHandler.DiagnosticsFileLengthProvider = null!;
        using (var okStream = new MemoryStream())
        using (var okWriter = new StreamWriter(okStream) { AutoFlush = true })
        {
            handler.Handle("", okWriter);
            Assert.Equal("COMPILATION_ERROR", System.Text.Encoding.UTF8.GetString(okStream.ToArray()).Trim());
        }

        // With transient file contention / deletion race on FileInfo.Length, returns COMPILING instead of throwing
        PollRefreshHandler.DiagnosticsFileLengthProvider = _ =>
            throw (Exception)Activator.CreateInstance(exceptionType, "Simulated diagnostics file contention")!;

        using (var contendedStream = new MemoryStream())
        using (var contendedWriter = new StreamWriter(contendedStream) { AutoFlush = true })
        {
            handler.Handle("", contendedWriter);
            Assert.Equal("COMPILING", System.Text.Encoding.UTF8.GetString(contendedStream.ToArray()).Trim());
        }
    }

    [Fact]
    public void FormatDiagnostics_WhenLineOrColumnOverflowsInt32_FallsBackToZeroWithoutThrowing()
    {
        string overflowCompilerMsg = "Assets/Scripts/Test.cs(99999999999999999999,88888888888888888888): error CS1002: ; expected";
        string formattedCompiler = UnityLeanMcpCompilationTracker.FormatCompilerDiagnostic(overflowCompilerMsg, "", 0, 0, isError: true);
        Assert.Equal("Assets/Scripts/Test.cs: error CS1002: ; expected", formattedCompiler);

        string formattedLeadingOp = UnityLeanMcpCompilationTracker.FormatOperationDiagnostic(overflowCompilerMsg, "", isError: true);
        Assert.Equal("Assets/Scripts/Test.cs: error CS1002: ; expected", formattedLeadingOp);

        string overflowShaderMsg = "Shader error in 'Custom/Water': undeclared identifier 'foo' at Assets/Shaders/Water.shader(99999999999999999999,88888888888888888888)";
        string formattedShaderOp = UnityLeanMcpCompilationTracker.FormatOperationDiagnostic(overflowShaderMsg, "", isError: true);
        Assert.Equal("Assets/Shaders/Water.shader: error Shader error in 'Custom/Water': undeclared identifier 'foo' at Assets/Shaders/Water.shader(99999999999999999999,88888888888888888888)", formattedShaderOp);
    }

    [Fact]
    public void CaptureOperationError_BoundsScopedDiagnosticsAtMaxCount()
    {
        Assert.Equal(BeginGateResult.Started, UnityCommandGate.TryBegin(OperationKinds.Refresh, "op-flood", OperationStatus.Refreshing, out _));
        UnityLeanMcpCompilationTracker.ClearCapturedDiagnostics();

        int setStringsBefore = SessionState.SetStringCount;
        int totalToEmit = UnityLeanMcpCompilationTracker.MaxScopedDiagnosticsCount + 50;
        for (int i = 0; i < totalToEmit; i++)
        {
            UnityLeanMcpCompilationTracker.CaptureOperationError($"Unique postprocessor error {i}", "", UnityEngine.LogType.Error);
        }

        int setStringsAfter = SessionState.SetStringCount;
        Assert.Equal(UnityLeanMcpCompilationTracker.MaxScopedDiagnosticsCount, setStringsAfter - setStringsBefore);

        var readMethod = typeof(UnityLeanMcpCompilationTracker).GetMethod("GetCapturedDiagnosticsSnapshot", BindingFlags.NonPublic | BindingFlags.Static)!;
        var captured = (List<string>)readMethod.Invoke(null, null)!;
        Assert.Equal(UnityLeanMcpCompilationTracker.MaxScopedDiagnosticsCount, captured.Count);
    }

    [Theory]
    [InlineData(true, "NO_OPERATION")]
    [InlineData(false, "NOT_CANCELABLE")]
    public void CancelOperationHandler_WhenActiveOperationFinishesDuringCancelCall_RechecksTerminalInterruptedResult(
        bool returnNotFound,
        string expectedWhenNotInterrupted)
    {
        var registryReturn = returnNotFound ? OperationCancelResult.NotFound : OperationCancelResult.NotCancelable;
        var handler = new CancelOperationHandler();

        // Case 1: Operation finishes as Cancelled during the race window -> returns CANCELLED
        Assert.Equal(BeginGateResult.Started, UnityCommandGate.TryBegin(OperationKinds.Test, "op-race-cancelled", OperationStatus.Running, out _));
        CancelOperationHandler.CancelRouteHook = (_, id) =>
        {
            File.WriteAllText(
                UnityLeanMcpPaths.GetTestResultsFile(id),
                "{\"runId\":\"" + id + "\",\"success\":false,\"resultState\":\"Cancelled\"}");
            UnityCommandGate.Complete(id);
            return registryReturn;
        };

        using (var stream = new MemoryStream())
        using (var writer = new StreamWriter(stream) { AutoFlush = true })
        {
            handler.Handle("op-race-cancelled", writer);
            Assert.Equal("CANCELLED", System.Text.Encoding.UTF8.GetString(stream.ToArray()).Trim());
        }

        // Case 2: Operation finishes as Passed (not interrupted/cancelled) during the race window -> returns expectedWhenNotInterrupted
        Assert.Equal(BeginGateResult.Started, UnityCommandGate.TryBegin(OperationKinds.Test, "op-race-passed", OperationStatus.Running, out _));
        CancelOperationHandler.CancelRouteHook = (_, id) =>
        {
            File.WriteAllText(
                UnityLeanMcpPaths.GetTestResultsFile(id),
                "{\"runId\":\"" + id + "\",\"success\":true,\"resultState\":\"Passed\"}");
            UnityCommandGate.Complete(id);
            return registryReturn;
        };

        using (var stream = new MemoryStream())
        using (var writer = new StreamWriter(stream) { AutoFlush = true })
        {
            handler.Handle("op-race-passed", writer);
            Assert.Equal(expectedWhenNotInterrupted, System.Text.Encoding.UTF8.GetString(stream.ToArray()).Trim());
        }
    }

    [Fact]
    public void RunTestsHandler_CancelActiveTestRun_UsesWorkerSnapshotAndChecksTerminalStatus()
    {
        RunTestsHandler.ClearCachedRunState();

        // Passed result -> NotFound (must not falsely report Cancelled)
        File.WriteAllText(
            UnityLeanMcpPaths.GetTestResultsFile("op-passed"),
            "{\"runId\":\"op-passed\",\"success\":true,\"resultState\":\"Passed\"}");
        Assert.Equal(OperationCancelResult.NotFound, RunTestsHandler.CancelActiveTestRun("op-passed"));

        // Failed result -> NotFound (must not falsely report Cancelled)
        File.WriteAllText(
            UnityLeanMcpPaths.GetTestResultsFile("op-failed"),
            "{\"runId\":\"op-failed\",\"success\":false,\"resultState\":\"Failed\"}");
        Assert.Equal(OperationCancelResult.NotFound, RunTestsHandler.CancelActiveTestRun("op-failed"));

        // Cancelled result -> Cancelled
        File.WriteAllText(
            UnityLeanMcpPaths.GetTestResultsFile("op-cancelled"),
            "{\"runId\":\"op-cancelled\",\"success\":false,\"resultState\":\"Cancelled\"}");
        Assert.Equal(OperationCancelResult.Cancelled, RunTestsHandler.CancelActiveTestRun("op-cancelled"));

        // Interrupted result -> Cancelled
        File.WriteAllText(
            UnityLeanMcpPaths.GetTestResultsFile("op-interrupted"),
            "{\"runId\":\"op-interrupted\",\"success\":false,\"resultState\":\"Interrupted\"}");
        Assert.Equal(OperationCancelResult.Cancelled, RunTestsHandler.CancelActiveTestRun("op-interrupted"));
    }

    [Fact]
    public void GetCoverageHandler_ProcessMethod_IgnoresZeroHiddenAndIntMaxOverflowLinesAndUsesPathCache()
    {
        var sampleMethod = typeof(CommandGatePublicationTests).GetMethod(
            nameof(GetCoverageHandler_ProcessMethod_IgnoresZeroHiddenAndIntMaxOverflowLinesAndUsesPathCache))!;
        string rawFile = Path.Combine(_root, "Assets", "Scripts", "Sample.cs");

        UnityEngine.TestTools.Coverage.GetSequencePointsForFunc = _ => new[]
        {
            new UnityEngine.TestTools.CoveredSequencePoint { method = sampleMethod, filename = rawFile, line = 0u, hitCount = 1u },
            new UnityEngine.TestTools.CoveredSequencePoint { method = sampleMethod, filename = rawFile, line = 0xfeefeeu, hitCount = 1u },
            new UnityEngine.TestTools.CoveredSequencePoint { method = sampleMethod, filename = rawFile, line = (uint)int.MaxValue + 1u, hitCount = 1u },
            new UnityEngine.TestTools.CoveredSequencePoint { method = sampleMethod, filename = rawFile, line = uint.MaxValue, hitCount = 1u },
            new UnityEngine.TestTools.CoveredSequencePoint { method = sampleMethod, filename = rawFile, line = 42u, hitCount = 1u },
            new UnityEngine.TestTools.CoveredSequencePoint { method = sampleMethod, filename = rawFile, line = 43u, hitCount = 0u }
        };

        var pointsByFile = new Dictionary<string, List<UnityEngine.TestTools.CoveredSequencePoint>>(StringComparer.OrdinalIgnoreCase);
        var pathCache = new Dictionary<string, string>(StringComparer.Ordinal);
        var filters = new List<string> { "" };

        GetCoverageHandler.ProcessMethod(sampleMethod, _root, filters, pointsByFile, pathCache);

        Assert.Single(pointsByFile);
        Assert.True(pointsByFile.TryGetValue("Assets/Scripts/Sample.cs", out var recordedPoints));
        Assert.NotNull(recordedPoints);
        Assert.Equal(new uint[] { 42u, 43u }, recordedPoints!.Select(p => p.line).ToArray());
        Assert.True(pathCache.TryGetValue(rawFile, out string? cachedRelPath));
        Assert.Equal("Assets/Scripts/Sample.cs", cachedRelPath);

        // Verify pre-populated cache entry is honored directly without re-normalizing
        var pointsWithCustomCache = new Dictionary<string, List<UnityEngine.TestTools.CoveredSequencePoint>>(StringComparer.OrdinalIgnoreCase);
        var customCache = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [rawFile] = "Assets/Cached/CustomMapped.cs"
        };
        GetCoverageHandler.ProcessMethod(sampleMethod, _root, filters, pointsWithCustomCache, customCache);
        Assert.True(pointsWithCustomCache.ContainsKey("Assets/Cached/CustomMapped.cs"));
    }

    [Fact]
    public void RunTestsHandler_FailedOnlyZeroMatchFastPath_PublishesPassedResultAndWritesRunning()
    {
        UnityLeanMcpCompilationTracker.IsCompiling = false;
        UnityLeanMcpCompilationTracker.RefreshPending = false;
        UnityLeanMcpCompilationTracker.RefreshRequired = false;
        UnityLeanMcpCompilationTracker.ScriptCompilationFailed = false;

        var handler = new RunTestsHandler();
        using var stream = new MemoryStream();
        using var writer = new StreamWriter(stream) { AutoFlush = true };

        handler.Handle("op-empty-failed {\"mode\":\"editmode\",\"failedOnly\":true}", writer);

        string response = System.Text.Encoding.UTF8.GetString(stream.ToArray()).Trim();
        Assert.Equal("RUNNING", response);
        Assert.Null(UnityCommandGate.ReadSnapshot());

        string resultPath = UnityLeanMcpPaths.GetTestResultsFile("op-empty-failed");
        Assert.True(File.Exists(resultPath));
        var published = UnityEngine.JsonUtility.FromJson<UnityLeanMcp.UnityTestRunResult>(File.ReadAllText(resultPath));
        Assert.NotNull(published);
        Assert.Equal("op-empty-failed", published!.runId);
        Assert.True(published.success);
        Assert.Equal("Passed", published.resultState);
        Assert.Equal("No previously failed tests found.", published.message);
    }

    [Fact]
    public void MyTestCallbacks_RunFinished_MarksRunCancelledWhenCancellationRequestedBeforeStatusUpdate()
    {
        const string runId = "op-pending-cancel";
        Assert.Equal(BeginGateResult.Started, UnityCommandGate.TryBegin(OperationKinds.Test, runId, OperationStatus.Running, out _));
        var state = new UnityLeanMcp.UnityTestRunState { runId = runId, status = OperationStatus.Running, totalTests = 1 };
        File.WriteAllText(UnityLeanMcpPaths.TestRunningFile, UnityEngine.JsonUtility.ToJson(state));
        RunTestsHandler.ClearCachedRunState();

        // Worker thread wrote the cancellation request file, but main thread has not updated runState.status yet
        Assert.True(WorkerThreadSnapshots.TryWriteTestCancellationRequest(UnityLeanMcpPaths.TestCancellationFile, runId));

        var callbacks = new MyTestCallbacks();
        callbacks.BindRun(runId);
        callbacks.RunFinished(new StubTestResultAdaptor
        {
            Name = "Suite",
            FullName = "Suite",
            ResultState = "Passed",
            TestStatus = UnityEditor.TestTools.TestRunner.Api.TestStatus.Passed,
            PassCount = 1,
            FailCount = 0
        });

        string resultPath = UnityLeanMcpPaths.GetTestResultsFile(runId);
        Assert.True(File.Exists(resultPath));
        var published = UnityEngine.JsonUtility.FromJson<UnityLeanMcp.UnityTestRunResult>(File.ReadAllText(resultPath));
        Assert.NotNull(published);
        Assert.False(published!.success);
        Assert.Equal(OperationStatus.Cancelled, published.resultState);
    }

    [Fact]
    public void MyTestCallbacks_RunFinished_RecordsSuiteFailureWhenParentSuiteFailsWithNoChildFailures()
    {
        const string runId = "op-suite-failure";
        Assert.Equal(BeginGateResult.Started, UnityCommandGate.TryBegin(OperationKinds.Test, runId, OperationStatus.Running, out _));
        var state = new UnityLeanMcp.UnityTestRunState { runId = runId, status = OperationStatus.Running, totalTests = 1 };
        File.WriteAllText(UnityLeanMcpPaths.TestRunningFile, UnityEngine.JsonUtility.ToJson(state));
        RunTestsHandler.ClearCachedRunState();

        var skippedChild = new StubTestResultAdaptor
        {
            Name = "ChildTest",
            FullName = "MyNamespace.OneTimeSetUpSuite.ChildTest",
            ResultState = "Skipped",
            TestStatus = UnityEditor.TestTools.TestRunner.Api.TestStatus.Skipped
        };
        var failedSuite = new StubTestResultAdaptor
        {
            Name = "OneTimeSetUpSuite",
            FullName = "MyNamespace.OneTimeSetUpSuite",
            ResultState = "Failed: Error",
            TestStatus = UnityEditor.TestTools.TestRunner.Api.TestStatus.Failed,
            FailCount = 1,
            Message = "OneTimeSetUp: System.InvalidOperationException : fixture setup boom",
            StackTrace = "at MyNamespace.OneTimeSetUpSuite.SetUp() in Suite.cs:line 10",
            Children = new[] { skippedChild }
        };

        var callbacks = new MyTestCallbacks();
        callbacks.BindRun(runId);
        callbacks.RunFinished(failedSuite);

        string resultPath = UnityLeanMcpPaths.GetTestResultsFile(runId);
        Assert.True(File.Exists(resultPath));
        var published = UnityEngine.JsonUtility.FromJson<UnityLeanMcp.UnityTestRunResult>(File.ReadAllText(resultPath));
        Assert.NotNull(published);
        Assert.False(published!.success);
        Assert.Single(published.failedTests);
        Assert.Equal("MyNamespace.OneTimeSetUpSuite", published.failedTests[0].fullName);
        Assert.Equal("OneTimeSetUp: System.InvalidOperationException : fixture setup boom", published.failedTests[0].message);
    }

    [Theory]
    [InlineData("OnError")]
    [InlineData("OnRunCancelled")]
    [InlineData("OnRunInterrupted")]
    [InlineData("OnInfrastructureFailure")]
    public void MyTestCallbacks_TestFinishedGuardsUnboundRunId_AndErrorCallbacksPreservePassAndSkipCounts(string callbackKind)
    {
        string runId = "op-counts-" + callbackKind;
        Assert.Equal(BeginGateResult.Started, UnityCommandGate.TryBegin(OperationKinds.Test, runId, OperationStatus.Running, out _));
        var state = new UnityLeanMcp.UnityTestRunState { runId = runId, status = OperationStatus.Running, totalTests = 3 };
        File.WriteAllText(UnityLeanMcpPaths.TestRunningFile, UnityEngine.JsonUtility.ToJson(state));
        RunTestsHandler.ClearCachedRunState();

        var callbacks = new MyTestCallbacks();

        // Unbound TestFinished (m_RunId is null) must be ignored
        callbacks.TestFinished(new StubTestResultAdaptor
        {
            Name = "UnboundPass",
            FullName = "Suite.UnboundPass",
            TestStatus = UnityEditor.TestTools.TestRunner.Api.TestStatus.Passed
        });

        callbacks.BindRun(runId);
        callbacks.TestFinished(new StubTestResultAdaptor
        {
            Name = "Pass1",
            FullName = "Suite.Pass1",
            TestStatus = UnityEditor.TestTools.TestRunner.Api.TestStatus.Passed
        });
        callbacks.TestFinished(new StubTestResultAdaptor
        {
            Name = "Pass2",
            FullName = "Suite.Pass2",
            TestStatus = UnityEditor.TestTools.TestRunner.Api.TestStatus.Passed
        });
        callbacks.TestFinished(new StubTestResultAdaptor
        {
            Name = "Skip1",
            FullName = "Suite.Skip1",
            TestStatus = UnityEditor.TestTools.TestRunner.Api.TestStatus.Skipped
        });

        switch (callbackKind)
        {
            case "OnError":
                callbacks.OnError("Runner error");
                break;
            case "OnRunCancelled":
                callbacks.OnRunCancelled("Cancelled mid-run");
                break;
            case "OnRunInterrupted":
                callbacks.OnRunInterrupted("Interrupted mid-run");
                break;
            case "OnInfrastructureFailure":
                callbacks.OnInfrastructureFailure("Infrastructure failure mid-run");
                break;
        }

        string resultPath = UnityLeanMcpPaths.GetTestResultsFile(runId);
        Assert.True(File.Exists(resultPath));
        var published = UnityEngine.JsonUtility.FromJson<UnityLeanMcp.UnityTestRunResult>(File.ReadAllText(resultPath));
        Assert.NotNull(published);
        Assert.False(published!.success);
        Assert.Equal(2, published.passCount);
        Assert.Equal(1, published.skipCount);
    }

    private sealed class StubTestResultAdaptor : UnityEditor.TestTools.TestRunner.Api.ITestResultAdaptor
    {
        public UnityEditor.TestTools.TestRunner.Api.ITestAdaptor? Test => null;
        public string Name { get; set; } = "";
        public string FullName { get; set; } = "";
        public string ResultState { get; set; } = "";
        public UnityEditor.TestTools.TestRunner.Api.TestStatus TestStatus { get; set; }
        public double Duration { get; set; } = 0.01;
        public string Message { get; set; } = "";
        public string StackTrace { get; set; } = "";
        public int FailCount { get; set; }
        public int PassCount { get; set; }
        public int SkipCount { get; set; }
        public bool HasChildren => Children != null && Children.Any();
        public IEnumerable<UnityEditor.TestTools.TestRunner.Api.ITestResultAdaptor>? Children { get; set; }
    }

    public void Dispose()
    {
        UnityCommandGate.ForceReset();
        UnityCommandGate.RetryThrottleMs = 100;
        SessionState.EraseString("UnityLeanMcp_PendingResult");
        SessionState.EraseString("UnityLeanMcp_RefreshDiagnostics");
        RunTestsHandler.ActiveStateKnown = true;
        RunTestsHandler.Active = false;
        RunTestsHandler.CancelledRunIds.Clear();
        RunTestsHandler.ClearCachedRunState();
        UnityEngine.TestTools.Coverage.enabled = false;
        SessionState.EraseString("UnityLeanMcp_CoverageOwner");
        GetCoverageHandler.ClearCapturedCoverage();
        UnityEngine.TestTools.Coverage.GetSequencePointsForFunc = null;
        UnityEditor.Compilation.CompilationPipeline.GetAssembliesFunc = null;
        CancelOperationHandler.CancelRouteHook = null;
        UnityCommandGate.WriteAtomicHook = null;
        UnityLeanMcpDispatcher.Clear();
        PollRefreshHandler.DiagnosticsFileLengthProvider = null!;
        UnityLeanMcpServer.IsPlayModeActive = false;
        UnityLeanMcpServer.IsPlayModeChanging = false;
        UnityLeanMcpCompilationTracker.IsCompiling = false;
        UnityLeanMcpCompilationTracker.IsUpdating = false;
        UnityLeanMcpCompilationTracker.ScriptCompilationFailed = false;
        UnityLeanMcpCompilationTracker.RefreshRequired = true;
        UnityLeanMcpCompilationTracker.RefreshPending = false;
        UnityLeanMcpCompilationTracker.CompilationRequested = false;
        UnityLeanMcpCompilationTracker.ClearCapturedDiagnostics();
        UnityLeanMcpPaths.ProjectRoot = _previousProjectRoot;
        Directory.Delete(_root, true);
    }
}
