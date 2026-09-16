using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace UnityLeanMcp.Mcp;

public sealed class OperationPollingSpec<TResult> where TResult : class, IOperationResult, new()
{
    public required string OperationId { get; init; }
    public string? Kind { get; init; }
    public string OperationDisplayName { get; init; } = "operation";
    public required string ResultFilePath { get; init; }
    public required Func<TResult, bool> IsMatch { get; init; }
    public required string PollCommand { get; init; }
    public int PollTimeoutSeconds { get; init; } = 5;
    public int PollIntervalMs { get; init; } = 500;
    public bool CheckOperationStoreForInterruption { get; init; } = true;
    public bool ShouldCancelOnAborted { get; init; } = true;
    /// <summary>
    /// Requires a matching durable result before accepting a protocol success.
    /// Explicit negative terminal responses remain authoritative because they
    /// cannot turn an uncorrelated operation into a false success.
    /// </summary>
    public bool RequireDurableResult { get; init; }

    /// <summary>
    /// Whether to delete the result file upon terminal completion.
    /// If null (default), only operation-scoped result files (whose filenames contain OperationId) are deleted;
    /// shared static result files (e.g. unity_refresh_result.json) are preserved.
    /// </summary>
    public bool? DeleteResultFileOnCompletion { get; init; }

    public Func<TResult, TResult>? OnResultFound { get; init; }
    public Func<CancellationToken, Task>? OnPollTick { get; init; }
    public Func<string?, CancellationToken, Task>? OnAfterPoll { get; init; }
    public Func<string, CancellationToken, Task<TResult?>>? CustomResponseHandler { get; init; }
}

public interface IOperationPoller
{
    Task<TResult> PollOperationUntilTerminalAsync<TResult>(
        OperationPollingSpec<TResult> spec,
        CancellationToken cancellationToken) where TResult : class, IOperationResult, new();

    Task<TResult> PollOperationResultAsync<TResult>(
        string opId,
        string kind,
        string operationDisplayName,
        string resultFilePath,
        string pollCommand,
        int pollIntervalMs = 500,
        Func<TResult, TResult>? onResultFound = null,
        CancellationToken cancellationToken = default) where TResult : UnityOperationResult, new();

    Task CancelOperationAsync(string opId, string kind);
}

public class OperationPoller : IOperationPoller
{
    private readonly IUnityProcessManager _processManager;
    private readonly IUnityPathResolver _pathResolver;
    private readonly IUnitySocketTransport _socketTransport;
    private readonly ILogger? _logger;

    private static readonly JsonSerializerOptions s_JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public OperationPoller(
        IUnityProcessManager processManager,
        IUnityPathResolver pathResolver,
        IUnitySocketTransport socketTransport,
        ILogger? logger = null)
    {
        _processManager = processManager ?? throw new ArgumentNullException(nameof(processManager));
        _pathResolver = pathResolver ?? throw new ArgumentNullException(nameof(pathResolver));
        _socketTransport = socketTransport ?? throw new ArgumentNullException(nameof(socketTransport));
        _logger = logger;
    }

    public static T? TryReadJsonFile<T>(string filePath, Func<T, bool> predicate) where T : class
    {
        if (!File.Exists(filePath)) return null;

        try
        {
            string json = UnityProcessManager.ReadFileWithRetry(filePath, maxRetries: 3, delayMs: 50);
            if (string.IsNullOrWhiteSpace(json)) return null;

            var result = JsonSerializer.Deserialize<T>(json, s_JsonOptions);
            if (result != null && predicate(result))
            {
                return result;
            }
        }
        catch
        {
            // Partially written file or transient read error during operation
        }

        return null;
    }

    public async Task CancelOperationAsync(string opId, string kind)
    {
        _logger?.LogInformation("Cancellation requested. Sending CANCEL_OPERATION for {OpId} ({Kind})...", opId, kind);
        try
        {
            using var cancelCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            int port = _processManager.ReadPortFile();
            await _socketTransport.SendCommandAsync(port, $"CANCEL_OPERATION {opId}", 3, cancelCts.Token);
        }
        catch (Exception ex)
        {
            _logger?.LogTrace(ex, "Failed to send CANCEL_OPERATION command for {OpId}", opId);
        }
    }

    public virtual async Task<TResult> PollOperationUntilTerminalAsync<TResult>(
        OperationPollingSpec<TResult> spec,
        CancellationToken cancellationToken) where TResult : class, IOperationResult, new()
    {
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // 1. Poll tick (e.g. running state progress updates)
                if (spec.OnPollTick != null)
                {
                    await spec.OnPollTick(cancellationToken);
                }

                // 2. Authoritative check: terminal result file
                var result = TryReadJsonFile(spec.ResultFilePath, spec.IsMatch);
                if (result != null)
                {
                    DeleteResultFileSilently(spec);
                    return spec.OnResultFound != null ? spec.OnResultFound(result) : result;
                }

                // 3. Process liveness check
                if (!_processManager.IsUnityRunning(out _))
                {
                    // Brief grace period in case result was written as process exited
                    await Task.Delay(300, cancellationToken);
                    var finalCheck = TryReadJsonFile(spec.ResultFilePath, spec.IsMatch);
                    if (finalCheck != null)
                    {
                        DeleteResultFileSilently(spec);
                        return spec.OnResultFound != null ? spec.OnResultFound(finalCheck) : finalCheck;
                    }

                    return new TResult
                    {
                        OperationId = spec.OperationId,
                        Success = false,
                        Message = $"Unity background process exited unexpectedly during {spec.OperationDisplayName}."
                    };
                }

                // 4. Operation store check for interruption
                if (spec.CheckOperationStoreForInterruption)
                {
                    var opState = TryReadJsonFile<UnityLeanMcpOperationState>(_pathResolver.OperationFile, o => o.OperationId == spec.OperationId);
                    if (opState != null && opState.Status == "Interrupted")
                    {
                        return new TResult
                        {
                            OperationId = spec.OperationId,
                            Success = false,
                            Interrupted = true,
                            Message = "Unity operation was interrupted by domain reload or editor restart."
                        };
                    }
                }

                // 5. Poll socket
                int port = _processManager.ReadPortFile();
                string? pollResp = await _socketTransport.SendCommandAsync(port, spec.PollCommand, spec.PollTimeoutSeconds, cancellationToken);
                if (pollResp != null)
                {
                    if (spec.CustomResponseHandler != null)
                    {
                        var custom = await spec.CustomResponseHandler(pollResp, cancellationToken);
                        if (custom != null)
                        {
                            return custom;
                        }
                    }

                    if (pollResp.StartsWith("INTERRUPTION", StringComparison.OrdinalIgnoreCase))
                    {
                        string msg = pollResp.Length > 12 ? pollResp[12..].Trim() : "Operation interrupted.";
                        return new TResult
                        {
                            OperationId = spec.OperationId,
                            Success = false,
                            Interrupted = true,
                            Message = ProtocolCodec.UnescapeLine(msg)
                        };
                    }

                    if (pollResp.StartsWith("BUSY", StringComparison.OrdinalIgnoreCase))
                    {
                        var busyInfo = UnityClient.ParseBusyResponse(pollResp);
                        if (busyInfo.isCompilation)
                        {
                            // Ongoing compilation/refresh; continue polling until compilation finishes and settles
                        }
                        else
                        {
                            var fileRes = TryReadJsonFile(spec.ResultFilePath, spec.IsMatch);
                            if (fileRes != null)
                            {
                                DeleteResultFileSilently(spec);
                                return spec.OnResultFound != null ? spec.OnResultFound(fileRes) : fileRes;
                            }

                            return new TResult
                            {
                                OperationId = spec.OperationId,
                                Success = false,
                                Message = $"Lost ownership of {spec.OperationDisplayName}: {pollResp}"
                            };
                        }
                    }

                    if (string.Equals(pollResp, "IDLE", StringComparison.OrdinalIgnoreCase))
                    {
                        // Always re-check terminal result file before declaring idle failure (Rule 26 & Issue #71).
                        // On Windows NTFS, allow a brief grace period for directory entry settlement if the Editor completed the operation.
                        for (int attempt = 0; attempt < 5; attempt++)
                        {
                            var fileRes = TryReadJsonFile(spec.ResultFilePath, spec.IsMatch);
                            if (fileRes != null)
                            {
                                DeleteResultFileSilently(spec);
                                return spec.OnResultFound != null ? spec.OnResultFound(fileRes) : fileRes;
                            }

                            if (attempt < 4)
                            {
                                await Task.Delay(50, cancellationToken);
                            }
                        }

                        return new TResult
                        {
                            OperationId = spec.OperationId,
                            Success = false,
                            Message = $"{spec.OperationDisplayName} is no longer recognized by the Editor (Editor is idle)."
                        };
                    }

                    if (pollResp.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase))
                    {
                        var fileRes = TryReadJsonFile(spec.ResultFilePath, spec.IsMatch);
                        if (fileRes != null)
                        {
                            DeleteResultFileSilently(spec);
                            return spec.OnResultFound != null ? spec.OnResultFound(fileRes) : fileRes;
                        }

                        string msg = pollResp.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase)
                            ? (pollResp.Length > 6 ? pollResp[6..].Trim() : "")
                            : (pollResp.Length > 5 ? pollResp[5..].Trim() : "");

                        return new TResult
                        {
                            OperationId = spec.OperationId,
                            Success = false,
                            Message = ProtocolCodec.UnescapeLine(msg)
                        };
                    }

                    if (pollResp.StartsWith("FAILURE", StringComparison.OrdinalIgnoreCase))
                    {
                        var fileRes = TryReadJsonFile(spec.ResultFilePath, spec.IsMatch);
                        if (fileRes != null)
                        {
                            DeleteResultFileSilently(spec);
                            return spec.OnResultFound != null ? spec.OnResultFound(fileRes) : fileRes;
                        }

                        string msg = pollResp.Length > 7 ? pollResp[7..].Trim() : "Operation failed.";
                        return new TResult
                        {
                            OperationId = spec.OperationId,
                            Success = false,
                            Message = ProtocolCodec.UnescapeLine(msg)
                        };
                    }

                    if (pollResp.StartsWith("SUCCESS", StringComparison.OrdinalIgnoreCase))
                    {
                        var fileRes = TryReadJsonFile(spec.ResultFilePath, spec.IsMatch);
                        if (fileRes != null)
                        {
                            DeleteResultFileSilently(spec);
                            return spec.OnResultFound != null ? spec.OnResultFound(fileRes) : fileRes;
                        }

                        if (spec.RequireDurableResult)
                        {
                            continue;
                        }

                        string payload = pollResp.Length > 7 ? pollResp[7..].Trim() : "";
                        var successRes = new TResult
                        {
                            OperationId = spec.OperationId,
                            Success = true,
                            Message = pollResp
                        };
                        if (successRes is UnityOperationResult opRes)
                        {
                            opRes.Payload = ProtocolCodec.UnescapeLine(payload);
                        }
                        DeleteResultFileSilently(spec);
                        return spec.OnResultFound != null ? spec.OnResultFound(successRes) : successRes;
                    }
                }

                // 6. After poll hook (e.g. refresh compilation progress)
                if (spec.OnAfterPoll != null)
                {
                    await spec.OnAfterPoll(pollResp, cancellationToken);
                }

                await Task.Delay(spec.PollIntervalMs, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            if (spec.ShouldCancelOnAborted && !string.IsNullOrEmpty(spec.Kind))
            {
                await CancelOperationAsync(spec.OperationId, spec.Kind);
            }
            throw;
        }
    }

    public Task<TResult> PollOperationResultAsync<TResult>(
        string opId,
        string kind,
        string operationDisplayName,
        string resultFilePath,
        string pollCommand,
        int pollIntervalMs = 500,
        Func<TResult, TResult>? onResultFound = null,
        CancellationToken cancellationToken = default) where TResult : UnityOperationResult, new()
    {
        var spec = new OperationPollingSpec<TResult>
        {
            OperationId = opId,
            Kind = kind,
            OperationDisplayName = operationDisplayName,
            ResultFilePath = resultFilePath,
            IsMatch = r => r.OperationId == opId,
            PollCommand = pollCommand,
            PollTimeoutSeconds = 5,
            PollIntervalMs = pollIntervalMs,
            RequireDurableResult = true,
            OnResultFound = onResultFound
        };

        return PollOperationUntilTerminalAsync(spec, cancellationToken);
    }

    private static void DeleteResultFileSilently<TResult>(OperationPollingSpec<TResult> spec) where TResult : class, IOperationResult, new()
    {
        if (string.IsNullOrEmpty(spec.ResultFilePath)) return;

        bool shouldDelete = spec.DeleteResultFileOnCompletion ?? IsOperationScopedResultFile(spec.ResultFilePath, spec.OperationId);
        if (!shouldDelete) return;

        try
        {
            if (File.Exists(spec.ResultFilePath))
            {
                File.Delete(spec.ResultFilePath);
            }
        }
        catch { }
    }

    internal static bool IsOperationScopedResultFile(string filePath, string? operationId)
    {
        if (string.IsNullOrEmpty(filePath) || string.IsNullOrEmpty(operationId)) return false;
        string fileName = Path.GetFileName(filePath);
        return fileName.Contains(operationId, StringComparison.OrdinalIgnoreCase);
    }
}
