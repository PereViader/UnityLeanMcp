using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;

namespace UnityLeanMcp.Mcp;

public class UnityClient : IUnityClient
{
    private static readonly JsonSerializerOptions s_RunArgsJsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly IUnityProcessManager _processManager;
    private readonly IUnityPathResolver _pathResolver;
    private readonly ILogger<UnityClient> _logger;
    private readonly IUnitySocketTransport _socketTransport;
    private readonly IOperationPoller _operationPoller;

    public UnityClient(
        IUnityProcessManager processManager,
        IUnityPathResolver pathResolver,
        ILogger<UnityClient> logger,
        IUnitySocketTransport? socketTransport = null,
        IOperationPoller? operationPoller = null,
        UnityClientOptions? options = null)
    {
        _processManager = processManager;
        _pathResolver = pathResolver;
        _logger = logger;
        _socketTransport = socketTransport ?? new UnitySocketTransport(logger);
        _operationPoller = operationPoller ?? new OperationPoller(_processManager, _pathResolver, _socketTransport, logger);
        if (options != null)
        {
            PollIntervalMs = options.PollIntervalMs;
            if (options.BusyGracePeriod.HasValue)
            {
                BusyGracePeriod = options.BusyGracePeriod.Value;
            }
        }
    }

    public UnityClient(
        IUnityProcessManager processManager,
        ILogger<UnityClient> logger,
        UnityClientOptions? options = null)
        : this(processManager, processManager.PathResolver, logger, options: options)
    {
    }

    /// <summary>
    /// Interval in milliseconds between polling checks during long-running operations. Defaults to 500ms.
    /// </summary>
    public int PollIntervalMs { get; init; } = 500;

    /// <summary>
    /// Grace period to wait when an active foreign mutating operation is detected before failing fast. Defaults to 3 seconds.
    /// </summary>
    public TimeSpan BusyGracePeriod { get; init; } = TimeSpan.FromSeconds(3);


    /// <summary>
    /// Returns current Editor connection state: Ready, Not Running, Compiling, Running Unreachable, Busy.
    /// </summary>
    public virtual async Task<string> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        int port = _processManager.ReadPortFile();
        if (port > 0)
        {
            // The project-scoped endpoint is the primary evidence of a live Editor.
            // Process discovery is a fallback only: macOS GUI Editor command lines
            // and zero-byte lockfiles are not reliably inspectable.
            string? pingResp = await SendCommandAsync("PING", 2, cancellationToken);
            if (pingResp == "PONG")
            {
                var op = TryReadJsonFile<UnityLeanMcpOperationState>(_pathResolver.OperationFile, _ => true);
                if (op != null)
                {
                    if (op.Status == "Compiling" || op.Status == "Reloading" || op.Status == "Refreshing" || op.Status == "Recompiling")
                    {
                        return "Compiling";
                    }
                    if (!string.IsNullOrEmpty(op.Kind))
                    {
                        return FormatBusyStatus(op);
                    }
                }

                string? pollResp = await SendCommandAsync("POLL_REFRESH", 2, cancellationToken);
                if (pollResp == "COMPILING" || pollResp == "UPDATING")
                {
                    return "Compiling";
                }
                if (pollResp != null && pollResp.StartsWith("BUSY ", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = pollResp.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2)
                    {
                        return $"Busy ({parts[1]})";
                    }
                }

                return "Ready";
            }
        }

        if (!_processManager.IsUnityRunning(out _))
        {
            return "Not Running";
        }

        var operation = TryReadJsonFile<UnityLeanMcpOperationState>(_pathResolver.OperationFile, _ => true);
        if (operation != null)
        {
            if (operation.Status == "Compiling" || operation.Status == "Reloading" || operation.Status == "Refreshing" || operation.Status == "Recompiling")
            {
                return "Compiling";
            }
            if (!string.IsNullOrEmpty(operation.Kind))
            {
                return FormatBusyStatus(operation);
            }
        }

        return "Running Unreachable";
    }

    private static string FormatBusyStatus(UnityLeanMcpOperationState op)
    {
        return $"Busy ({op.Kind})";
    }

    internal static (bool isBusy, bool isCompilation, string? kind, string? opId) ParseBusyResponse(string? response)
    {
        if (string.IsNullOrWhiteSpace(response))
        {
            return (false, false, null, null);
        }

        string trimmed = response.Trim();
        if (trimmed.Equals("COMPILING", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("UPDATING", StringComparison.OrdinalIgnoreCase))
        {
            return (true, true, "compile", null);
        }

        if (trimmed.StartsWith("BUSY", StringComparison.OrdinalIgnoreCase))
        {
            var parts = trimmed.Split(new[] { ' ', ':', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length <= 1)
            {
                return (true, false, "unknown", null);
            }

            string second = parts[1].ToLowerInvariant();
            if (second == "compile" || second == "refresh" || second == "recompile" || second == "reloading" || second == "updating")
            {
                return (true, true, second, parts.Length > 2 ? parts[2] : null);
            }

            return (true, false, parts[1], parts.Length > 2 ? parts[2] : null);
        }

        return (false, false, null, null);
    }

    internal static string FormatBusyExecutingMessage(string? kind, string? opId)
    {
        string kindStr = string.IsNullOrWhiteSpace(kind) ? "unknown" : kind;
        string idStr = string.IsNullOrWhiteSpace(opId) ? "" : $" (id: {opId})";
        return $"Unity is busy executing '{kindStr}'{idStr}. If this operation is hung, call unity_stop to recover.";
    }

    private async Task<bool> WaitForActiveOperationGracePeriodAsync(
        string? kind,
        string? opId,
        IProgress<ProgressNotificationValue>? progress,
        TimeSpan gracePeriod,
        CancellationToken cancellationToken)
    {
        DateTime deadline = DateTime.UtcNow + gracePeriod;
        _logger.LogInformation("Waiting grace period for active operation '{Kind}' (id: {OpId})...", kind, opId);

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            progress?.Report(new ProgressNotificationValue
            {
                Progress = 0,
                Message = $"Waiting for active '{kind ?? "operation"}' to complete..."
            });

            string? check = await SendCommandAsync("POLL_REFRESH", 2, cancellationToken);
            if (check != null)
            {
                var busy = ParseBusyResponse(check);
                if (!busy.isBusy || busy.isCompilation || (busy.kind != null && !busy.kind.Equals(kind, StringComparison.OrdinalIgnoreCase)) || (busy.opId != null && !busy.opId.Equals(opId, StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }
            else
            {
                var activeOp = TryReadJsonFile<UnityLeanMcpOperationState>(_pathResolver.OperationFile, _ => true);
                if (activeOp == null || !string.Equals(activeOp.Kind, kind, StringComparison.OrdinalIgnoreCase) || (!string.IsNullOrEmpty(opId) && !string.Equals(activeOp.OperationId, opId, StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }

            await Task.Delay(Math.Min(250, (int)Math.Max(10, (deadline - DateTime.UtcNow).TotalMilliseconds)), cancellationToken);
        }

        return false;
    }

    private Task CancelOperationAsync(string opId, string kind) =>
        _operationPoller.CancelOperationAsync(opId, kind);

    public Task<UnityRefreshResult> RefreshAsync(bool isRecompile, CancellationToken cancellationToken) =>
        RefreshAsync(isRecompile, null, cancellationToken);

    public Task<UnityRefreshResult> RefreshAsync(CancellationToken cancellationToken) =>
        RefreshAsync(false, null, cancellationToken);

    /// <summary>
    /// Refreshes AssetDatabase, waits for compilation, and returns diagnostics.
    /// If isRecompile is true, triggers clean script recompilation.
    /// </summary>
    public virtual async Task<UnityRefreshResult> RefreshAsync(
        bool isRecompile = false,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default)
    {
        string opId = Guid.NewGuid().ToString("N");

        progress?.Report(new ProgressNotificationValue
        {
            Progress = 0,
            Total = 100,
            Message = "Checking Unity Editor connection..."
        });

        try
        {
            await _processManager.EnsureUnityRunningAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is UnityCompilationException or FileNotFoundException or InvalidOperationException)
        {
            return new UnityRefreshResult
            {
                OperationId = opId,
                Success = false,
                Message = ex.Message
            };
        }

        string triggerCommand = isRecompile ? $"RECOMPILE {opId}" : $"REFRESH {opId}";

        _logger.LogInformation("Triggering {Kind} operation with id {OpId}...", isRecompile ? "recompile" : "refresh", opId);

        progress?.Report(new ProgressNotificationValue
        {
            Progress = 10,
            Total = 100,
            Message = isRecompile
                ? "Triggering clean script recompilation..."
                : "Triggering AssetDatabase refresh..."
        });

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string? initialResponse;
            try
            {
                initialResponse = await SendCommandAsync(triggerCommand, 10, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await CancelOperationAsync(opId, isRecompile ? "recompile" : "refresh");
                throw;
            }
            var initialTerminalResult = ProtocolCodec.TryCreateImmediateTerminalResult<UnityRefreshResult>(initialResponse, opId);
            if (initialTerminalResult != null)
            {
                return initialTerminalResult;
            }

            var initBusy = ParseBusyResponse(initialResponse);
            if (!initBusy.isBusy)
            {
                return await WaitForCompilationToSettleAsync(
                    opId,
                    isRecompile,
                    progress,
                    // A newly submitted operation must not infer success from an
                    // uncorrelated READY response. Only an explicitly observed active
                    // compilation may settle without this operation's result file.
                    acceptCurrentCompilationState: false,
                    cancellationToken: cancellationToken);
            }

            if (initBusy.isCompilation)
            {
                var waitResult = await WaitForCompilationToSettleAsync(
                    opId,
                    isRecompile,
                    progress,
                    acceptCurrentCompilationState: true,
                    cancellationToken: cancellationToken);
                if (!waitResult.Success)
                {
                    return waitResult;
                }

                // The request was rejected while another compilation was active.
                // Now that it has settled, submit our own operation so this caller
                // gets a correlated durable refresh result.
                continue;
            }

            bool cleared = await WaitForActiveOperationGracePeriodAsync(initBusy.kind, initBusy.opId, progress, BusyGracePeriod, cancellationToken);
            if (!cleared)
            {
                return new UnityRefreshResult
                {
                    OperationId = opId,
                    Success = false,
                    Message = FormatBusyExecutingMessage(initBusy.kind, initBusy.opId)
                };
            }
        }
    }

    private Task<UnityRefreshResult> RefreshBeforeUsingCompiledAssembliesAsync(
        IProgress<ProgressNotificationValue>? progress,
        CancellationToken cancellationToken) =>
        // A passive readiness observation cannot be a source-freshness
        // guarantee: Unity may not yet have received an external file-change
        // notification. Every compiled-code consumer therefore crosses the
        // correlated refresh barrier before dispatch.
        RefreshAsync(isRecompile: false, progress, cancellationToken);

    private async Task<UnityRefreshResult> WaitForCompilationToSettleAsync(
        string opId,
        bool isRecompile,
        IProgress<ProgressNotificationValue>? progress,
        bool acceptCurrentCompilationState,
        CancellationToken cancellationToken)
    {
        int compileProgress = 30;
        bool currentCompilationWasProven = acceptCurrentCompilationState;

        UnityRefreshResult CompleteRefreshResult(UnityRefreshResult result)
        {
            progress?.Report(new ProgressNotificationValue
            {
                Progress = 90,
                Total = 100,
                Message = "Compilation finished, waiting for Editor to settle..."
            });

            EnrichRefreshResultWithDiagnostics(result);

            if (result.Success)
            {
                progress?.Report(new ProgressNotificationValue
                {
                    Progress = 100,
                    Total = 100,
                    Message = isRecompile
                        ? "Clean script recompilation completed."
                        : "AssetDatabase refresh completed."
                });
            }

            return result;
        }

        OperationPollingSpec<UnityRefreshResult> spec = null!;
        spec = new OperationPollingSpec<UnityRefreshResult>
        {
            OperationId = opId,
            Kind = null,
            ShouldCancelOnAborted = false,
            RequireDurableResult = true,
            OperationDisplayName = "refresh operation",
            ResultFilePath = _pathResolver.GetResultFilePath(isRecompile ? UnityOperationKind.Recompile : UnityOperationKind.Refresh, opId),
            IsMatch = r => r.OperationId == opId,
            PollCommand = $"POLL_REFRESH {opId}",
            PollTimeoutSeconds = 2,
            PollIntervalMs = PollIntervalMs,
            OnResultFound = CompleteRefreshResult,
            CustomResponseHandler = async (pollResp, ct) =>
            {
                var busy = ParseBusyResponse(pollResp);
                if (busy.isCompilation)
                {
                    currentCompilationWasProven = true;
                    return null;
                }

                if (string.Equals(pollResp, "READY", StringComparison.OrdinalIgnoreCase))
                {
                    var result = TryReadJsonFile<UnityRefreshResult>(spec.ResultFilePath, r => r.OperationId == opId);
                    if (result != null)
                    {
                        return result;
                    }

                    if (!acceptCurrentCompilationState || !currentCompilationWasProven)
                    {
                        return null;
                    }

                    return new UnityRefreshResult
                    {
                        OperationId = opId,
                        Success = true,
                        Message = "The already-running compilation completed successfully."
                    };
                }

                if (string.Equals(pollResp, "COMPILATION_ERROR", StringComparison.OrdinalIgnoreCase))
                {
                    var result = TryReadJsonFile<UnityRefreshResult>(spec.ResultFilePath, r => r.OperationId == opId);
                    if (result != null)
                    {
                        return result;
                    }

                    if (!acceptCurrentCompilationState || !currentCompilationWasProven)
                    {
                        return null;
                    }

                    // Allow a short settle delay for diagnostics from the already
                    // observed compilation to become visible. This is not an
                    // operation timeout.
                    await Task.Delay(200, ct);
                    string diag = ReadCompilationErrors();
                    return new UnityRefreshResult
                    {
                        OperationId = opId,
                        Success = false,
                        Message = !string.IsNullOrWhiteSpace(diag) ? diag : "Unity script compilation failed."
                    };
                }

                return null;
            },
            OnAfterPoll = (pollResp, ct) =>
            {
                var opState = TryReadJsonFile<UnityLeanMcpOperationState>(_pathResolver.OperationFile, _ => true);
                bool isCompiling = (pollResp == "COMPILING" || pollResp == "UPDATING" || (pollResp != null && ParseBusyResponse(pollResp).isCompilation)) ||
                    (opState != null && (opState.Status == "Compiling" || opState.Status == "Reloading" || opState.Status == "Refreshing" || opState.Status == "Recompiling" || opState.Status == "WaitingForUnity"));

                if (isCompiling)
                {
                    progress?.Report(new ProgressNotificationValue
                    {
                        Progress = compileProgress,
                        Total = 100,
                        Message = "Compiling script assemblies..."
                    });

                    if (compileProgress < 80)
                    {
                        compileProgress = Math.Min(80, compileProgress + 10);
                    }
                }

                return Task.CompletedTask;
            }
        };

        return await PollOperationUntilTerminalAsync(spec, cancellationToken);
    }

    public Task<UnityEvalResult> EvalAsync(string code, CancellationToken cancellationToken) =>
        EvalAsync(code, null, cancellationToken);

    /// <summary>
    /// Evaluates dynamic C# snippet in-memory against active Editor/Play Mode.
    /// </summary>
    public virtual async Task<UnityEvalResult> EvalAsync(
        string code,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report(new ProgressNotificationValue
        {
            Progress = 0,
            Total = 100,
            Message = "Checking compilation and pending AssetDatabase changes before evaluation..."
        });

        IProgress<ProgressNotificationValue>? refreshProgress = progress == null ? null : new ProgressRelay(p =>
        {
            int scaled = (int)Math.Round((p.Progress / (double)(p.Total ?? 100)) * 40);
            progress.Report(new ProgressNotificationValue
            {
                Progress = scaled,
                Total = 100,
                Message = p.Message ?? "Checking compilation and pending AssetDatabase changes before evaluation..."
            });
        });

        var refreshResult = await RefreshBeforeUsingCompiledAssembliesAsync(refreshProgress, cancellationToken);
        if (!refreshResult.Success)
        {
            return new UnityEvalResult
            {
                Success = false,
                Interrupted = refreshResult.Interrupted,
                Message = refreshResult.Message
            };
        }

        progress?.Report(new ProgressNotificationValue
        {
            Progress = 50,
            Total = 100,
            Message = "Evaluating C# snippet..."
        });

        string escapedCode = ProtocolCodec.EscapeLine(code);

        return await DispatchMutatingCommandAsync<UnityEvalResult>(
            operationKind: "eval",
            operationDisplayName: "evaluation",
            commandFactory: opId => $"EVAL {opId} {escapedCode}",
            resultFilePathFactory: opId => _pathResolver.GetResultFilePath(UnityOperationKind.Eval, opId),
            resultMatcher: (r, opId) => r.OperationId == opId,
            pollExecutor: (opId, resultFile, ct) => PollOperationResultAsync<UnityEvalResult>(
                opId: opId,
                kind: "eval",
                operationDisplayName: "evaluation",
                resultFilePath: resultFile,
                pollCommand: $"POLL_EVAL {opId}",
                cancellationToken: ct),
            progress: progress,
            initialProgressMessage: null,
            onImmediateResult: null,
            cancellationToken: cancellationToken);
    }

    public virtual async Task<UnityTestRunResult> RunTestsAsync(
        string[]? testNames,
        string[]? groupNames,
        string[]? categoryNames,
        string[]? assemblyNames,
        string? mode,
        bool failedOnly = false,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!TestModeParser.TryNormalize(mode, out string testMode))
        {
            return new UnityTestRunResult
            {
                Success = false,
                ResultState = "InvalidInput",
                Message = TestModeParser.InvalidModeMessage
            };
        }

        if (!TestFilterValidation.TryValidate(
                testNames,
                groupNames,
                categoryNames,
                assemblyNames,
                out string invalidFilterMessage))
        {
            return new UnityTestRunResult
            {
                Success = false,
                ResultState = "InvalidInput",
                Message = invalidFilterMessage
            };
        }

        progress?.Report(new ProgressNotificationValue
        {
            Progress = 0,
            Message = "Refreshing AssetDatabase and compiling changes before tests..."
        });

        IProgress<ProgressNotificationValue>? refreshProgress = progress == null ? null : new ProgressRelay(p =>
        {
            progress.Report(new ProgressNotificationValue
            {
                Progress = 0,
                Message = p.Message ?? "Refreshing AssetDatabase and compiling changes before tests..."
            });
        });

        // A READY probe only observes Unity's current flags. External source
        // edits can still be waiting for Unity's asset watcher, so treating it
        // as proof that the test assemblies are current can run stale code.
        // RefreshAsync creates a correlated operation and waits for its durable
        // result before RUN_TESTS is sent. A normal AssetDatabase refresh is a
        // cheap no-op when no assets have changed.
        var refreshResult = await RefreshAsync(
            isRecompile: false,
            progress: refreshProgress,
            cancellationToken: cancellationToken);
        if (!refreshResult.Success)
        {
            return new UnityTestRunResult
            {
                Success = false,
                ResultState = refreshResult.Interrupted ? "Interrupted" : "CompileError",
                Message = refreshResult.Message
            };
        }

        var runArgs = new RunTestsArgs
        {
            Mode = testMode,
            TestNames = testNames != null && testNames.Length > 0 ? testNames : null,
            GroupNames = groupNames != null && groupNames.Length > 0 ? groupNames : null,
            CategoryNames = categoryNames != null && categoryNames.Length > 0 ? categoryNames : null,
            AssemblyNames = assemblyNames != null && assemblyNames.Length > 0 ? assemblyNames : null,
            FailedOnly = failedOnly
        };

        string json = JsonSerializer.Serialize(runArgs, s_RunArgsJsonOptions);
        string escapedJson = ProtocolCodec.EscapeLine(json);

        return await DispatchMutatingCommandAsync<UnityTestRunResult>(
            operationKind: "test",
            operationDisplayName: "test run",
            commandFactory: opId => $"RUN_TESTS {opId} {escapedJson}",
            resultFilePathFactory: opId => _pathResolver.GetResultFilePath(UnityOperationKind.Test, opId),
            resultMatcher: (r, opId) => r.RunId == opId,
            pollExecutor: (opId, resultFile, ct) =>
            {
                int lastCompleted = -1;
                string? lastTestName = null;
                string? lastStatus = null;

                var spec = new OperationPollingSpec<UnityTestRunResult>
                {
                    OperationId = opId,
                    Kind = "test",
                    OperationDisplayName = "test run",
                    ResultFilePath = resultFile,
                    IsMatch = r => r.RunId == opId,
                    PollCommand = $"POLL_TESTS {opId}",
                    PollTimeoutSeconds = 5,
                    PollIntervalMs = PollIntervalMs,
                    RequireDurableResult = true,
                    OnResultFound = res =>
                    {
                        ReportFinalProgress(progress, res);
                        return res;
                    },
                    OnPollTick = _ =>
                    {
                        var runningState = TryReadJsonFile<UnityTestRunState>(_pathResolver.TestRunningFile, s => s.RunId == opId);
                        if (runningState != null && progress != null)
                        {
                            if (runningState.CompletedTests != lastCompleted ||
                                runningState.CurrentTestName != lastTestName ||
                                runningState.Status != lastStatus)
                            {
                                lastCompleted = runningState.CompletedTests;
                                lastTestName = runningState.CurrentTestName;
                                lastStatus = runningState.Status;

                                string msg;
                                if (runningState.TotalTests > 0)
                                {
                                    if (!string.IsNullOrEmpty(runningState.CurrentTestName))
                                    {
                                        msg = $"[{runningState.CompletedTests}/{runningState.TotalTests}] Running {runningState.CurrentTestName} (Passed: {runningState.PassCount}, Failed: {runningState.FailCount})";
                                    }
                                    else
                                    {
                                        msg = $"[{runningState.CompletedTests}/{runningState.TotalTests}] Running tests... (Passed: {runningState.PassCount}, Failed: {runningState.FailCount})";
                                    }
                                }
                                else
                                {
                                    msg = !string.IsNullOrEmpty(runningState.CurrentTestName)
                                        ? $"Running {runningState.CurrentTestName}..."
                                        : "Running tests...";
                                }

                                progress.Report(new ProgressNotificationValue
                                {
                                    Progress = runningState.CompletedTests,
                                    Total = runningState.TotalTests > 0 ? runningState.TotalTests : null,
                                    Message = msg
                                });
                            }
                        }
                        return Task.CompletedTask;
                    }
                };

                return PollOperationUntilTerminalAsync(spec, ct);
            },
            progress: progress,
            initialProgressMessage: $"Initializing {testMode} test run...",
            onImmediateResult: res => ReportFinalProgress(progress, res),
            cancellationToken: cancellationToken);
    }

    private Task<TResult> PollOperationUntilTerminalAsync<TResult>(
        OperationPollingSpec<TResult> spec,
        CancellationToken cancellationToken) where TResult : class, IOperationResult, new() =>
        _operationPoller.PollOperationUntilTerminalAsync(spec, cancellationToken);

    private Task<TResult> PollOperationResultAsync<TResult>(
        string opId,
        string kind,
        string operationDisplayName,
        string resultFilePath,
        string pollCommand,
        Func<TResult, TResult>? onResultFound = null,
        CancellationToken cancellationToken = default) where TResult : UnityOperationResult, new() =>
        _operationPoller.PollOperationResultAsync(
            opId,
            kind,
            operationDisplayName,
            resultFilePath,
            pollCommand,
            PollIntervalMs,
            onResultFound,
            cancellationToken);

    private async Task<TResult> DispatchMutatingCommandAsync<TResult>(
        string operationKind,
        string operationDisplayName,
        Func<string, string> commandFactory,
        Func<string, string> resultFilePathFactory,
        Func<TResult, string, bool> resultMatcher,
        Func<string, string, CancellationToken, Task<TResult>> pollExecutor,
        IProgress<ProgressNotificationValue>? progress,
        string? initialProgressMessage,
        Action<TResult>? onImmediateResult,
        CancellationToken cancellationToken) where TResult : class, IOperationResult, new()
    {
        IProgress<ProgressNotificationValue>? refreshProgress = progress == null
            ? null
            : new ProgressRelay(p => progress.Report(new ProgressNotificationValue
            {
                Progress = p.Progress,
                Total = p.Total,
                Message = p.Message
            }));

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string opId = Guid.NewGuid().ToString("N");
            string resultFile = resultFilePathFactory(opId);
            string command = commandFactory(opId);

            if (!string.IsNullOrEmpty(initialProgressMessage))
            {
                progress?.Report(new ProgressNotificationValue
                {
                    Progress = 0,
                    Message = initialProgressMessage
                });
            }

            _logger.LogInformation("Sending {Kind} operation {OpId}...", command.Split(' ')[0], opId);
            string? initialResponse;
            try
            {
                initialResponse = await SendCommandAsync(command, 10, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await CancelOperationAsync(opId, operationKind);
                throw;
            }

            var immediateResult = TryReadJsonFile<TResult>(resultFile, r => resultMatcher(r, opId));
            if (immediateResult != null)
            {
                try { File.Delete(resultFile); } catch { }
                onImmediateResult?.Invoke(immediateResult);
                return immediateResult;
            }

            var busyInfo = ParseBusyResponse(initialResponse);
            if (busyInfo.isBusy)
            {
                if (busyInfo.isCompilation)
                {
                    progress?.Report(new ProgressNotificationValue
                    {
                        Progress = 0,
                        Message = "Unity is compiling script assemblies. Waiting for compilation to complete..."
                    });

                    var compResult = await RefreshBeforeUsingCompiledAssembliesAsync(refreshProgress, cancellationToken);
                    if (!compResult.Success)
                    {
                        var failure = new TResult
                        {
                            OperationId = opId,
                            Success = false,
                            Interrupted = compResult.Interrupted,
                            Message = compResult.Message
                        };
                        if (failure is UnityTestRunResult testRes)
                        {
                            testRes.ResultState = compResult.Interrupted ? "Interrupted" : "CompileError";
                        }
                        return failure;
                    }

                    continue;
                }
                else
                {
                    bool cleared = await WaitForActiveOperationGracePeriodAsync(busyInfo.kind, busyInfo.opId, progress, BusyGracePeriod, cancellationToken);
                    if (!cleared)
                    {
                        return new TResult
                        {
                            OperationId = opId,
                            Success = false,
                            Message = FormatBusyExecutingMessage(busyInfo.kind, busyInfo.opId)
                        };
                    }

                    continue;
                }
            }

            var terminal = ProtocolCodec.TryCreateImmediateTerminalResult<TResult>(initialResponse, opId);
            if (terminal != null)
            {
                return terminal;
            }

            return await pollExecutor(opId, resultFile, cancellationToken);
        }
    }

    private static void ReportFinalProgress(IProgress<ProgressNotificationValue>? progress, UnityTestRunResult result)
    {
        if (progress == null) return;
        int total = result.PassCount + result.FailCount + result.SkipCount;
        string skipStr = result.SkipCount > 0 ? $", {result.SkipCount} skipped" : "";
        string msg = result.Success
            ? $"Tests finished: {result.PassCount} passed{skipStr}."
            : $"Tests finished: {result.FailCount} failed, {result.PassCount} passed{skipStr}.";
        progress.Report(new ProgressNotificationValue
        {
            Progress = total,
            Total = total > 0 ? total : null,
            Message = msg
        });
    }

    private Task<string?> SendCommandAsync(string command, int timeoutSeconds = 10, CancellationToken cancellationToken = default)
    {
        int port = _processManager.ReadPortFile();
        return _socketTransport.SendCommandAsync(port, command, timeoutSeconds, cancellationToken);
    }

    private string ReadCompilationErrors()
    {
        if (File.Exists(_pathResolver.CompilationErrorsFile))
        {
            try
            {
                return UnityProcessManager.ReadFileWithRetry(_pathResolver.CompilationErrorsFile);
            }
            catch { }
        }
        return "";
    }

    internal void EnrichRefreshResultWithDiagnostics(UnityRefreshResult result)
    {
        string errors = ReadCompilationErrors();
        EnrichRefreshResultWithDiagnostics(result, errors);
    }

    internal static void EnrichRefreshResultWithDiagnostics(UnityRefreshResult result, string? errors)
    {
        if (!string.IsNullOrWhiteSpace(errors))
        {
            result.Message = errors;
            var diags = DiagnosticFormatter.Default.ParseCompilerDiagnostics(errors);
            if (diags.Count > 0)
            {
                if (diags.Any(d => string.Equals(d.Severity, "error", StringComparison.OrdinalIgnoreCase)))
                {
                    result.Success = false;
                }
            }
            else if (errors.Contains("error", StringComparison.OrdinalIgnoreCase))
            {
                result.Success = false;
            }
        }
    }

    private static T? TryReadJsonFile<T>(string filePath, Func<T, bool> predicate) where T : class =>
        OperationPoller.TryReadJsonFile(filePath, predicate);

    private sealed class ProgressRelay : IProgress<ProgressNotificationValue>
    {
        private readonly Action<ProgressNotificationValue> _handler;

        public ProgressRelay(Action<ProgressNotificationValue> handler)
        {
            _handler = handler;
        }

        public void Report(ProgressNotificationValue value) => _handler(value);
    }
}
