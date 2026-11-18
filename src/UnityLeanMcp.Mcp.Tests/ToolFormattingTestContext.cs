using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace UnityLeanMcp.Mcp.Tests;

internal sealed class FakeUnityProcessManager : UnityProcessManager
{
    public bool Running { get; set; }
    public int? Pid { get; set; }
    public string Mode { get; set; } = "Batchmode";
    public bool StopSuccess { get; set; } = true;

    public FakeUnityProcessManager(IUnityPathResolver pathResolver)
        : base(pathResolver, NullLogger<UnityProcessManager>.Instance)
    {
    }

    public FakeUnityProcessManager(string projectRoot)
        : this(new UnityPathResolver(projectRoot))
    {
    }

    public override bool IsUnityRunning(out int? processId)
    {
        processId = Running ? Pid : null;
        return Running;
    }

    public override string GetUnityMode(int? pid = null) => Mode;

    public override Task<bool> StopUnityAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        if (Mode == "GUI" && !force)
        {
            return Task.FromResult(false);
        }

        if (StopSuccess)
        {
            Running = false;
        }
        return Task.FromResult(StopSuccess);
    }
}

internal sealed class FakeUnityClient : UnityClient
{
    public UnityRefreshResult RefreshResultToReturn { get; set; } = new();
    public UnityEvalResult EvalResultToReturn { get; set; } = new();
    public UnityTestRunResult TestRunResultToReturn { get; set; } = new();

    public FakeUnityClient(UnityProcessManager pm, IUnityPathResolver pathResolver)
        : base(pm, pathResolver, NullLogger<UnityClient>.Instance)
    {
    }

    public FakeUnityClient(UnityProcessManager pm)
        : this(pm, pm.PathResolver)
    {
    }

    public override Task<UnityRefreshResult> RefreshAsync(
        bool isRecompile = false,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(RefreshResultToReturn);

    public override Task<UnityEvalResult> EvalAsync(
        string code,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(EvalResultToReturn);

    public string[]? LastTestNames { get; private set; }
    public string[]? LastGroupNames { get; private set; }
    public string[]? LastCategoryNames { get; private set; }
    public string[]? LastAssemblyNames { get; private set; }
    public string? LastMode { get; private set; }
    public bool LastFailedOnly { get; private set; }
    public bool LastCoverage { get; private set; }
    public CoverageResult CoverageResultToReturn { get; set; } = new() { Success = true };
    public string[]? LastCoveragePaths { get; private set; }
    public int RunTestsCallCount { get; private set; }

    public override Task<UnityTestRunResult> RunTestsAsync(
        string[]? testNames,
        string[]? groupNames,
        string[]? categoryNames,
        string[]? assemblyNames,
        string? mode,
        bool failedOnly = false,
        bool coverage = false,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default)
    {
        RunTestsCallCount++;
        LastTestNames = testNames;
        LastGroupNames = groupNames;
        LastCategoryNames = categoryNames;
        LastAssemblyNames = assemblyNames;
        LastMode = mode;
        LastFailedOnly = failedOnly;
        LastCoverage = coverage;
        return Task.FromResult(TestRunResultToReturn);
    }

    public override Task<CoverageResult> GetCoverageAsync(
        string[] paths,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default)
    {
        LastCoveragePaths = paths;
        return Task.FromResult(CoverageResultToReturn);
    }
}

internal sealed class ToolFormattingTestContext : IDisposable
{
    public string TempDir { get; }
    public UnityPathResolver PathResolver { get; }
    public FakeUnityProcessManager ProcessManager { get; }
    public FakeUnityClient Client { get; }
    public UnityTools Tools { get; }

    public ToolFormattingTestContext()
    {
        TempDir = Path.Combine(Path.GetTempPath(), "unity_fmt_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(TempDir, "Temp"));
        Directory.CreateDirectory(Path.Combine(TempDir, "ProjectSettings"));

        PathResolver = new UnityPathResolver(TempDir);
        ProcessManager = new FakeUnityProcessManager(PathResolver);
        Client = new FakeUnityClient(ProcessManager, PathResolver);
        Tools = new UnityTools(Client, PathResolver);
    }

    public static string GetResultText(CallToolResult result)
    {
        return result.Content.Count > 0 && result.Content[0] is TextContentBlock textBlock
            ? textBlock.Text
            : "";
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(TempDir, true);
        }
        catch
        {
        }
    }
}

internal sealed class CountingDiagnosticFormatter : IDiagnosticFormatter
{
    private readonly IDiagnosticFormatter _inner = DiagnosticFormatter.Default;
    public int ParseCallCount { get; private set; }
    public string? LastParsedInput { get; private set; }

    public (string? filePath, int? lineNumber, string? fileUri) ExtractSourceLocation(string? stackTrace, string? projectRoot)
        => _inner.ExtractSourceLocation(stackTrace, projectRoot);

    public string SanitizeTestStackTrace(string? stackTrace)
        => _inner.SanitizeTestStackTrace(stackTrace);

    public string CompactTestStackTrace(string? stackTrace, int maxLines = 3)
        => _inner.CompactTestStackTrace(stackTrace, maxLines);

    public List<StructuredCompilerDiagnostic> ParseCompilerDiagnostics(string? diagnosticText)
    {
        ParseCallCount++;
        LastParsedInput = diagnosticText;
        return _inner.ParseCompilerDiagnostics(diagnosticText);
    }

    public string FormatCompilerDiagnostics(
        string? diagnosticText,
        string? projectRoot,
        string? successTrailer = null,
        string? failureTrailer = null,
        bool isSuccess = false,
        int maxWarnings = DiagnosticFormatter.DefaultMaxWarnings,
        bool isEval = false)
        => _inner.FormatCompilerDiagnostics(diagnosticText, projectRoot, successTrailer, failureTrailer, isSuccess, maxWarnings, isEval);

    public string FormatCompilerDiagnostics(
        IReadOnlyList<StructuredCompilerDiagnostic> diagnostics,
        string? rawDiagnosticText,
        string? projectRoot,
        string? successTrailer = null,
        string? failureTrailer = null,
        bool isSuccess = false,
        int maxWarnings = DiagnosticFormatter.DefaultMaxWarnings,
        bool isEval = false)
        => _inner.FormatCompilerDiagnostics(diagnostics, rawDiagnosticText, projectRoot, successTrailer, failureTrailer, isSuccess, maxWarnings, isEval);

    public string FormatDiagnostic(StructuredCompilerDiagnostic diagnostic, string? projectRoot, bool isEval = false)
        => _inner.FormatDiagnostic(diagnostic, projectRoot, isEval);
}
