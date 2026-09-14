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
        if (!_processManager.IsUnityRunning(out _))
        {
            return "Not Running";
        }

        int port = _processManager.ReadPortFile();
        if (port <= 0)
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
            return "Running Unreachable";
        }

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

        var activeOp = TryReadJsonFile<UnityLeanMcpOperationState>(_pathResolver.OperationFile, _ => true);
        if (activeOp != null)
        {
            if (activeOp.Status == "Compiling" || activeOp.Status == "Reloading" || activeOp.Status == "Refreshing" || activeOp.Status == "Recompiling")
            {
                return "Compiling";
            }
            if (!string.IsNullOrEmpty(activeOp.Kind))
            {
                return FormatBusyStatus(activeOp);
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
                if (!busy.isBusy || busy.isCompilation || (busy.kind != null && !busy.kind.Equals(kind, StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }
            else
            {
                var activeOp = TryReadJsonFile<UnityLeanMcpOperationState>(_pathResolver.OperationFile, _ => true);
                if (activeOp == null || (!string.Equals(activeOp.Kind, kind, StringComparison.OrdinalIgnoreCase) && activeOp.OperationId != opId))
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

        // Pre-check if Unity is busy
        string? busyCheck = await SendCommandAsync($"POLL_REFRESH {opId}", 2, cancellationToken);
        var busyInfo = ParseBusyResponse(busyCheck);
        if (busyInfo.isBusy)
        {
            if (busyInfo.isCompilation)
            {
                var waitResult = await WaitForCompilationToSettleAsync(
                    opId,
                    isRecompile,
                    progress,
                    acceptCurrentCompilationState: true,
                    cancellationToken: cancellationToken);
                if (!waitResult.Success || !isRecompile)
                {
                    return waitResult;
                }
            }
            else
            {
                bool cleared = await WaitForActiveOperationGracePeriodAsync(busyInfo.kind, busyInfo.opId, progress, BusyGracePeriod, cancellationToken);
                if (!cleared)
                {
                    return new UnityRefreshResult
                    {
                        OperationId = opId,
                        Success = false,
                        Message = FormatBusyExecutingMessage(busyInfo.kind, busyInfo.opId)
                    };
                }
            }
        }

        progress?.Report(new ProgressNotificationValue
        {
            Progress = 10,
            Total = 100,
            Message = isRecompile
                ? "Triggering clean script recompilation..."
                : "Triggering AssetDatabase refresh..."
        });

        // Send refresh/recompile command
        string? initialResponse = await SendCommandAsync(triggerCommand, 10, cancellationToken);

        var initialTerminalResult = TryCreateRefreshTerminalResult(initialResponse, opId);
        if (initialTerminalResult != null)
        {
            return initialTerminalResult;
        }

        var initBusy = ParseBusyResponse(initialResponse);
        if (initBusy.isBusy)
        {
            if (initBusy.isCompilation)
            {
                return await WaitForCompilationToSettleAsync(
                    opId,
                    isRecompile,
                    progress,
                    acceptCurrentCompilationState: true,
                    cancellationToken: cancellationToken);
            }
            else
            {
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

                initialResponse = await SendCommandAsync(triggerCommand, 10, cancellationToken);
                initialTerminalResult = TryCreateRefreshTerminalResult(initialResponse, opId);
                if (initialTerminalResult != null)
                {
                    return initialTerminalResult;
                }

                var retryBusy = ParseBusyResponse(initialResponse);
                if (retryBusy.isBusy)
                {
                    if (retryBusy.isCompilation)
                    {
                        return await WaitForCompilationToSettleAsync(
                            opId,
                            isRecompile,
                            progress,
                            acceptCurrentCompilationState: true,
                            cancellationToken: cancellationToken);
                    }

                    return new UnityRefreshResult
                    {
                        OperationId = opId,
                        Success = false,
                        Message = FormatBusyExecutingMessage(retryBusy.kind, retryBusy.opId)
                    };
                }
            }
        }

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

    private async Task<UnityRefreshResult> RefreshIfNeededAsync(
        IProgress<ProgressNotificationValue>? progress,
        CancellationToken cancellationToken)
    {
        string probeId = Guid.NewGuid().ToString("N");

        try
        {
            // Keep the existing auto-start guarantee before consulting the
            // readiness probe. A stale port must never make Eval/tests skip
            // startup and then dispatch into a dead Editor.
            await _processManager.EnsureUnityRunningAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is UnityCompilationException or FileNotFoundException or InvalidOperationException)
        {
            return new UnityRefreshResult
            {
                OperationId = probeId,
                Success = false,
                Message = ex.Message
            };
        }

        string? readiness = await SendCommandAsync($"POLL_REFRESH CHECK {probeId}", 2, cancellationToken);

        // The probe is deliberately fail-open. A missing, stale, or negative
        // response must use the existing correlated refresh workflow so an
        // externally changed asset or script is never silently skipped.
        if (string.Equals(readiness, "READY", StringComparison.OrdinalIgnoreCase))
        {
            return new UnityRefreshResult
            {
                OperationId = probeId,
                Success = true,
                Message = "No pending AssetDatabase refresh or compilation."
            };
        }

        return await RefreshAsync(isRecompile: false, progress, cancellationToken);
    }

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

        var spec = new OperationPollingSpec<UnityRefreshResult>
        {
            OperationId = opId,
            Kind = null,
            ShouldCancelOnAborted = false,
            RequireDurableResult = true,
            OperationDisplayName = "refresh operation",
            ResultFilePath = _pathResolver.GetResultFilePath(UnityOperationKind.Refresh, opId),
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
                    var refreshResultPath = _pathResolver.GetResultFilePath(UnityOperationKind.Refresh, opId);
                    var result = TryReadJsonFile<UnityRefreshResult>(refreshResultPath, r => r.OperationId == opId);
                    if (result != null)
                    {
                        // Let the poller's next iteration consume the
                        // operation-scoped file through OnResultFound. That
                        // keeps terminal cleanup and completion reporting on
                        // the normal authoritative-result path.
                        return null;
                    }

                    if (!acceptCurrentCompilationState || !currentCompilationWasProven)
                    {
                        return null;
                    }

                    result = new UnityRefreshResult
                    {
                        OperationId = opId,
                        Success = true,
                        Message = "The already-running compilation completed successfully."
                    };

                    return CompleteRefreshResult(result);
                }

                if (string.Equals(pollResp, "COMPILATION_ERROR", StringComparison.OrdinalIgnoreCase))
                {
                    var refreshResultPath = _pathResolver.GetResultFilePath(UnityOperationKind.Refresh, opId);
                    var result = TryReadJsonFile<UnityRefreshResult>(refreshResultPath, r => r.OperationId == opId);
                    if (result != null)
                    {
                        // Let the poller's next iteration consume the
                        // operation-scoped file through OnResultFound. That
                        // keeps terminal cleanup and completion reporting on
                        // the normal authoritative-result path.
                        return null;
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
                    return CompleteRefreshResult(new UnityRefreshResult
                    {
                        OperationId = opId,
                        Success = false,
                        Message = !string.IsNullOrWhiteSpace(diag) ? diag : "Unity script compilation failed."
                    });
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

    private static UnityRefreshResult? TryCreateRefreshTerminalResult(string? response, string operationId)
    {
        if (string.IsNullOrWhiteSpace(response))
        {
            return null;
        }

        string trimmed = response.Trim();
        if (trimmed.StartsWith("INTERRUPTION", StringComparison.OrdinalIgnoreCase))
        {
            string message = trimmed.Length > 12 ? trimmed[12..].Trim() : "Operation interrupted.";
            return new UnityRefreshResult
            {
                OperationId = operationId,
                Success = false,
                Interrupted = true,
                Message = ProtocolCodec.UnescapeLine(message)
            };
        }

        if (trimmed.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("FAILURE", StringComparison.OrdinalIgnoreCase))
        {
            return new UnityRefreshResult
            {
                OperationId = operationId,
                Success = false,
                Message = ProtocolCodec.UnescapeLine(StripStatusPrefix(trimmed))
            };
        }

        return null;
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

        var refreshResult = await RefreshIfNeededAsync(refreshProgress, cancellationToken);
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

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string opId = Guid.NewGuid().ToString("N");
            string resultFile = _pathResolver.GetResultFilePath(UnityOperationKind.Eval, opId);
            string command = $"EVAL {opId} {escapedCode}";

            _logger.LogInformation("Sending EVAL operation {OpId}...", opId);
            string? initialResponse;
            try
            {
                initialResponse = await SendCommandAsync(command, 10, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The command may have reached Unity before its RUNNING
                // acknowledgement was returned. Ensure that an accepted
                // operation is cancelled even though polling never began.
                await CancelOperationAsync(opId, "eval");
                throw;
            }

            // Check if result already available
            var immediateResult = TryReadJsonFile<UnityEvalResult>(resultFile, r => r.OperationId == opId);
            if (immediateResult != null)
            {
                try { File.Delete(resultFile); } catch { }
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

                    var compResult = await RefreshIfNeededAsync(refreshProgress, cancellationToken);
                    if (!compResult.Success)
                    {
                        return new UnityEvalResult
                        {
                            OperationId = opId,
                            Success = false,
                            Interrupted = compResult.Interrupted,
                            Message = compResult.Message
                        };
                    }

                    continue;
                }
                else
                {
                    bool cleared = await WaitForActiveOperationGracePeriodAsync(busyInfo.kind, busyInfo.opId, progress, BusyGracePeriod, cancellationToken);
                    if (!cleared)
                    {
                        return new UnityEvalResult
                        {
                            OperationId = opId,
                            Success = false,
                            Message = FormatBusyExecutingMessage(busyInfo.kind, busyInfo.opId)
                        };
                    }

                    continue;
                }
            }

            if (initialResponse != null && (initialResponse.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase) || initialResponse.StartsWith("FAILURE", StringComparison.OrdinalIgnoreCase)))
            {
                return new UnityEvalResult { OperationId = opId, Success = false, Message = ProtocolCodec.UnescapeLine(StripStatusPrefix(initialResponse)) };
            }

            return await PollOperationResultAsync<UnityEvalResult>(
                opId: opId,
                kind: "eval",
                operationDisplayName: "evaluation",
                resultFilePath: resultFile,
                pollCommand: $"POLL_EVAL {opId}",
                cancellationToken: cancellationToken);
        }
    }

    [Obsolete("unity_execute_method has been retired; use EvalAsync instead.")]
    public Task<UnityExecuteResult> ExecuteMethodAsync(string methodName, string[]? args, CancellationToken cancellationToken) =>
        ExecuteMethodAsync(methodName, args, null, cancellationToken);

    /// <summary>
    /// Invokes static C# method with arguments in Unity Editor.
    /// </summary>
    [Obsolete("unity_execute_method has been retired; use EvalAsync instead.")]
    public virtual async Task<UnityExecuteResult> ExecuteMethodAsync(
        string methodName,
        string[]? args,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report(new ProgressNotificationValue
        {
            Progress = 0,
            Total = 100,
            Message = "Refreshing AssetDatabase prior to execution..."
        });

        IProgress<ProgressNotificationValue>? refreshProgress = progress == null ? null : new ProgressRelay(p =>
        {
            int scaled = (int)Math.Round((p.Progress / (double)(p.Total ?? 100)) * 40);
            progress.Report(new ProgressNotificationValue
            {
                Progress = scaled,
                Total = 100,
                Message = "Refreshing AssetDatabase prior to execution..."
            });
        });

        var refreshResult = await RefreshAsync(isRecompile: false, refreshProgress, cancellationToken);
        if (!refreshResult.Success)
        {
            return new UnityExecuteResult
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
            Message = $"Executing static method {methodName}..."
        });

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string opId = Guid.NewGuid().ToString("N");
            string resultFile = _pathResolver.GetResultFilePath(UnityOperationKind.Execute, opId);
            var sb = new StringBuilder($"EXECUTE_METHOD {opId} {methodName}");
            if (args != null)
            {
                foreach (var arg in args)
                {
                    string escaped = ProtocolCodec.EscapeParam(arg ?? "");
                    sb.Append(" \"").Append(escaped).Append('"');
                }
            }

            _logger.LogInformation("Sending EXECUTE_METHOD operation {OpId} for {MethodName}...", opId, methodName);
            string? initialResponse = await SendCommandAsync(sb.ToString(), 10, cancellationToken);

            var immediateResult = TryReadJsonFile<UnityExecuteResult>(resultFile, r => r.OperationId == opId);
            if (immediateResult != null)
            {
                try { File.Delete(resultFile); } catch { }
                if (immediateResult.Success) ReportExecuteCompleted(progress);
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

                    var compResult = await RefreshAsync(isRecompile: false, refreshProgress, cancellationToken);
                    if (!compResult.Success)
                    {
                        return new UnityExecuteResult
                        {
                            OperationId = opId,
                            Success = false,
                            Interrupted = compResult.Interrupted,
                            Message = compResult.Message
                        };
                    }

                    continue;
                }
                else
                {
                    bool cleared = await WaitForActiveOperationGracePeriodAsync(busyInfo.kind, busyInfo.opId, progress, BusyGracePeriod, cancellationToken);
                    if (!cleared)
                    {
                        return new UnityExecuteResult
                        {
                            OperationId = opId,
                            Success = false,
                            Message = FormatBusyExecutingMessage(busyInfo.kind, busyInfo.opId)
                        };
                    }

                    continue;
                }
            }

            if (initialResponse != null && (initialResponse.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase) || initialResponse.StartsWith("FAILURE", StringComparison.OrdinalIgnoreCase)))
            {
                return new UnityExecuteResult { OperationId = opId, Success = false, Message = ProtocolCodec.UnescapeLine(StripStatusPrefix(initialResponse)) };
            }

            return await PollOperationResultAsync<UnityExecuteResult>(
                opId: opId,
                kind: "execute",
                operationDisplayName: "method execution",
                resultFilePath: resultFile,
                pollCommand: $"POLL_EXECUTE {opId}",
                onResultFound: res =>
                {
                    if (res.Success) ReportExecuteCompleted(progress);
                    return res;
                },
                cancellationToken: cancellationToken);
        }
    }

    public Task<UnityTestRunResult> RunTestsAsync(
        string? filter,
        string? category,
        string? mode,
        IProgress<ProgressNotificationValue>? progress,
        CancellationToken cancellationToken = default) =>
        RunTestsAsync(filter, category, mode, false, progress, cancellationToken);

    public virtual Task<UnityTestRunResult> RunTestsAsync(
        string? filter,
        string? category,
        string? mode,
        bool failedOnly = false,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default) =>
        RunTestsAsync(
            testNames: null,
            groupNames: !string.IsNullOrEmpty(filter) ? [filter] : null,
            categoryNames: !string.IsNullOrEmpty(category) ? [category] : null,
            assemblyNames: null,
            mode: mode,
            failedOnly: failedOnly,
            progress: progress,
            cancellationToken: cancellationToken);

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
            Message = "Checking compilation and pending AssetDatabase changes before tests..."
        });

        IProgress<ProgressNotificationValue>? refreshProgress = progress == null ? null : new ProgressRelay(p =>
        {
            progress.Report(new ProgressNotificationValue
            {
                Progress = 0,
                Message = p.Message ?? "Checking compilation and pending AssetDatabase changes before tests..."
            });
        });

        var refreshResult = await RefreshIfNeededAsync(refreshProgress, cancellationToken);
        if (!refreshResult.Success)
        {
            return new UnityTestRunResult
            {
                Success = false,
                ResultState = refreshResult.Interrupted ? "Interrupted" : "CompileError",
                Message = refreshResult.Message
            };
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string opId = Guid.NewGuid().ToString("N");
            string resultFile = _pathResolver.GetResultFilePath(UnityOperationKind.Test, opId);

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
            string command = $"RUN_TESTS {opId} {ProtocolCodec.EscapeLine(json)}";

            progress?.Report(new ProgressNotificationValue
            {
                Progress = 0,
                Message = $"Initializing {testMode} test run..."
            });

            _logger.LogInformation("Sending RUN_TESTS operation {OpId} (mode: {Mode})...", opId, testMode);
            string? initialResponse;
            try
            {
                initialResponse = await SendCommandAsync(command, 10, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await CancelOperationAsync(opId, "test");
                throw;
            }

            var immediateResult = TryReadJsonFile<UnityTestRunResult>(resultFile, r => r.RunId == opId);
            if (immediateResult != null)
            {
                try { File.Delete(resultFile); } catch { }
                ReportFinalProgress(progress, immediateResult);
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

                    var compResult = await RefreshIfNeededAsync(refreshProgress, cancellationToken);
                    if (!compResult.Success)
                    {
                        return new UnityTestRunResult
                        {
                            RunId = opId,
                            Success = false,
                            ResultState = compResult.Interrupted ? "Interrupted" : "CompileError",
                            Message = compResult.Message
                        };
                    }

                    continue;
                }
                else
                {
                    bool cleared = await WaitForActiveOperationGracePeriodAsync(busyInfo.kind, busyInfo.opId, progress, BusyGracePeriod, cancellationToken);
                    if (!cleared)
                    {
                        return new UnityTestRunResult
                        {
                            RunId = opId,
                            Success = false,
                            Message = FormatBusyExecutingMessage(busyInfo.kind, busyInfo.opId)
                        };
                    }

                    continue;
                }
            }

            if (initialResponse != null && (initialResponse.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase) || initialResponse.StartsWith("FAILURE", StringComparison.OrdinalIgnoreCase)))
            {
                return new UnityTestRunResult { RunId = opId, Success = false, Message = ProtocolCodec.UnescapeLine(StripStatusPrefix(initialResponse)) };
            }
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

        return await PollOperationUntilTerminalAsync(spec, cancellationToken);
        }
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

    private static void ReportFinalProgress(IProgress<ProgressNotificationValue>? progress, UnityTestRunResult result)
    {
        if (progress == null) return;
        int total = result.PassCount + result.FailCount + result.SkipCount;
        string msg = result.Success
            ? $"Tests finished: {result.PassCount} passed, {result.SkipCount} skipped."
            : $"Tests finished: {result.FailCount} failed, {result.PassCount} passed, {result.SkipCount} skipped.";
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

    internal static string StripStatusPrefix(string response)
    {
        if (response.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase))
        {
            return response[6..].Trim();
        }

        if (response.StartsWith("FAILURE:", StringComparison.OrdinalIgnoreCase))
        {
            return response[8..].Trim();
        }

        if (response.StartsWith("SUCCESS:", StringComparison.OrdinalIgnoreCase))
        {
            return response[8..].Trim();
        }

        int spaceIdx = response.IndexOf(' ');
        if (spaceIdx > 0)
        {
            return response[(spaceIdx + 1)..].Trim();
        }

        if (response.Equals("ERROR", StringComparison.OrdinalIgnoreCase) ||
            response.Equals("FAILURE", StringComparison.OrdinalIgnoreCase) ||
            response.Equals("SUCCESS", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        return response;
    }

    private static T? TryReadJsonFile<T>(string filePath, Func<T, bool> predicate) where T : class =>
        OperationPoller.TryReadJsonFile(filePath, predicate);

    private static void ReportExecuteCompleted(IProgress<ProgressNotificationValue>? progress)
    {
        progress?.Report(new ProgressNotificationValue
        {
            Progress = 100,
            Total = 100,
            Message = "Method execution completed."
        });
    }

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
