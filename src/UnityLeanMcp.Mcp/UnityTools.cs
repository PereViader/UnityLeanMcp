using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace UnityLeanMcp.Mcp;

[McpServerToolType]
public class UnityTools
{
    private readonly IUnityClient _client;
    private readonly IUnityProcessManager _processManager;
    private readonly IUnityPathResolver _pathResolver;
    private readonly IDiagnosticFormatter _diagnosticFormatter;

    public UnityTools(
        IUnityClient client,
        IUnityProcessManager processManager,
        IUnityPathResolver? pathResolver = null,
        IDiagnosticFormatter? diagnosticFormatter = null)
    {
        _client = client;
        _processManager = processManager;
        _pathResolver = pathResolver ?? processManager.PathResolver;
        _diagnosticFormatter = diagnosticFormatter ?? DiagnosticFormatter.Default;
    }

    [McpServerTool(Name = "unity_refresh")]
    [Description("Refreshes AssetDatabase and returns compiler diagnostics. Fast (<200ms) when unchanged. Use to verify compilation after editing scripts. Note: unity_run_tests and unity_eval automatically refresh pending changes beforehand, so calling unity_refresh immediately before those tools is unnecessary.")]
    public async Task<CallToolResult> UnityRefreshAsync(
        [Description("Optional. If true, forces a full clean rebuild by clearing the assembly compiler cache. Defaults to false; use only when recovering from corrupted cache or stale errors.")]
        bool clean = false,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var result = await _client.RefreshAsync(isRecompile: clean, progress, cancellationToken);
        var sb = new StringBuilder();

        string successMessage = clean
            ? "Clean script recompilation completed with 0 errors."
            : "AssetDatabase refresh completed with 0 errors.";
        string interruptedMessage = clean
            ? "Unity recompilation interrupted by domain reload or restart."
            : "Unity compilation interrupted by domain reload or restart.";
        string failedMessage = clean
            ? "Error: Unity recompilation failed."
            : "Error: Unity compilation failed.";

        if (result.Interrupted)
        {
            string msg = !string.IsNullOrWhiteSpace(result.Message)
                ? result.Message
                : interruptedMessage;
            sb.Append(msg);
        }
        else if (result.Message?.Contains("busy", StringComparison.OrdinalIgnoreCase) == true)
        {
            sb.Append(result.Message);
        }
        else
        {
            string formatted = _diagnosticFormatter.FormatCompilerDiagnostics(
                result.Message,
                _processManager.ProjectRoot,
                successTrailer: successMessage,
                failureTrailer: failedMessage,
                isSuccess: result.Success);
            sb.Append(formatted);
        }

        return new CallToolResult
        {
            Content =
            [
                new TextContentBlock { Text = sb.ToString().TrimEnd() }
            ],
            IsError = !result.Success
        };
    }

    [McpServerTool(Name = "unity_eval")]
    [Description("Evaluates C# code in-memory against the active Unity Editor to query scene state, GameObjects, components, and project data. Accepts raw multiline C# top-level statements (and 'using' directives). Do not wrap code in a class, method, or namespace. No default namespaces are pre-imported; include all required 'using' directives in the snippet. Top-level 'await' is supported. Use 'return <value>;' to return data; void statements and 'return;' complete naturally without returning a value.")]
    public async Task<CallToolResult> UnityEvalAsync(
        [Description("Raw multiline C# code text to evaluate verbatim. Write executable statements directly like a C# script (do not wrap in a class or method). Supports top-level 'using' directives (e.g. 'using UnityEngine;') and top-level 'await'. No default namespaces are pre-imported. Send raw C# text directly—do not wrap in JSON.")] string code,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default)
    {
        string resolvedCode = UnwrapJsonCodeIfPresent(code);
        var result = await _client.EvalAsync(resolvedCode, progress, cancellationToken);

        string logsText = "";
        if (result.Logs.Count > 0)
        {
            var logSb = new StringBuilder();
            foreach (var log in result.Logs)
            {
                if (log.LogType == "Warning")
                {
                    logSb.AppendLine($"[Warning] {log.Message}");
                }
                else if (log.LogType is "Error" or "Assert" or "Exception")
                {
                    logSb.AppendLine($"[{log.LogType}] {log.Message}");
                }
                else
                {
                    logSb.AppendLine(log.Message);
                }
            }
            logsText = logSb.ToString().TrimEnd();
        }

        bool hasLogs = !string.IsNullOrEmpty(logsText);
        string humanText;

        if (result.Success)
        {
            bool hasPayload = !string.IsNullOrEmpty(result.Payload);
            if (hasLogs && hasPayload)
            {
                humanText = $"Logs:{Environment.NewLine}{logsText}{Environment.NewLine}{Environment.NewLine}Result:{Environment.NewLine}{result.Payload!.TrimEnd()}";
            }
            else if (hasPayload)
            {
                humanText = result.Payload!.TrimEnd();
            }
            else if (hasLogs)
            {
                humanText = logsText;
            }
            else
            {
                humanText = "(Evaluation completed without a return statement. Use 'return <expr>;' to return a value.)";
            }
        }
        else
        {
            string errorMsg;
            if (result.Interrupted)
            {
                errorMsg = string.IsNullOrWhiteSpace(result.Message)
                    ? "Command interrupted by Unity recompilation outside the UnityLeanMcp workflow."
                    : result.Message;
            }
            else
            {
                var parsedDiags = _diagnosticFormatter.ParseCompilerDiagnostics(result.Message);
                if (parsedDiags.Count > 0)
                {
                    bool isSnippetFailure = parsedDiags.Any(d => DiagnosticFormatter.IsEvalSynthetic(d.File));
                    string failureTrailer = isSnippetFailure
                        ? "Evaluation aborted: Dynamic snippet compilation failed."
                        : "Evaluation aborted: Project script compilation failed.";

                    errorMsg = _diagnosticFormatter.FormatCompilerDiagnostics(
                        result.Message,
                        _processManager.ProjectRoot,
                        failureTrailer: failureTrailer,
                        isSuccess: false,
                        isEval: true);
                }
                else
                {
                    errorMsg = string.IsNullOrWhiteSpace(result.Message) ? "Evaluation failed." : result.Message;
                }
            }

            if (hasLogs)
            {
                humanText = $"Logs:{Environment.NewLine}{logsText}{Environment.NewLine}{Environment.NewLine}Error:{Environment.NewLine}{errorMsg.TrimEnd()}";
            }
            else
            {
                humanText = errorMsg.TrimEnd();
            }
        }

        return new CallToolResult
        {
            Content =
            [
                new TextContentBlock { Text = humanText }
            ],
            IsError = !result.Success
        };
    }


    [McpServerTool(Name = "unity_run_tests")]
    [Description("Runs EditMode/PlayMode tests with failure diagnostics.")]
    public async Task<CallToolResult> UnityRunTestsAsync(
        [Description("Test filter string (wildcards and class/method names supported).")] string? filter = null,
        [Description("Test category filter.")] string? category = null,
        [Description("Test execution mode: 'all' (default), 'editmode', or 'playmode'.")] string? mode = "all",
        [Description("Only run tests that previously failed.")] bool failedOnly = false,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default)
    {
        mode = string.IsNullOrWhiteSpace(mode) ? "all" : mode;
        var result = await _client.RunTestsAsync(filter, category, mode, failedOnly, progress, cancellationToken);
        var sb = new StringBuilder();

        bool hasFilter = !string.IsNullOrWhiteSpace(filter);
        bool hasCategory = !string.IsNullOrWhiteSpace(category);
        bool hasFilterOrCategory = hasFilter || hasCategory;
        int totalTests = result.PassCount + result.FailCount + result.SkipCount;

        bool success = result.Success && result.FailCount == 0;

        if (result.ResultState == "CompileError")
        {
            string formatted = _diagnosticFormatter.FormatCompilerDiagnostics(
                result.Message,
                _processManager.ProjectRoot,
                failureTrailer: "Test execution aborted: Script compilation failed.",
                isSuccess: false);
            sb.Append(formatted);
        }
        else if (result.ResultState == "Interrupted")
        {
            sb.AppendLine($"Test run interrupted: {result.Message}");
        }
        else if (hasFilterOrCategory && totalTests == 0)
        {
            success = false;
            if (hasFilter && hasCategory)
            {
                sb.AppendLine($"No tests found matching filter '{filter}' and category '{category}' (mode: {mode}).");
            }
            else if (hasFilter)
            {
                sb.AppendLine($"No tests found matching filter '{filter}' (mode: {mode}).");
            }
            else
            {
                sb.AppendLine($"No tests found matching category '{category}' (mode: {mode}).");
            }
        }
        else if (result.Success)
        {
            if (totalTests == 0)
            {
                if (!string.IsNullOrWhiteSpace(result.Message))
                {
                    sb.AppendLine(result.Message);
                }
                else
                {
                    sb.AppendLine("Tests Passed: 0 passed, 0 skipped (no tests found in suite).");
                }
            }
            else
            {
                sb.AppendLine($"Tests Passed: {result.PassCount} passed, {result.SkipCount} skipped.");
            }
        }
        else if (result.Message?.Contains("busy", StringComparison.OrdinalIgnoreCase) == true)
        {
            sb.AppendLine(result.Message);
        }
        else
        {
            sb.AppendLine($"Tests Failed: {result.FailCount} failed, {result.PassCount} passed, {result.SkipCount} skipped.");
            if (!string.IsNullOrWhiteSpace(result.Message))
            {
                sb.AppendLine(result.Message);
            }
        }

        var structuredFailures = new List<StructuredTestFailure>();
        for (int i = 0; i < result.FailedTests.Count; i++)
        {
            var fail = result.FailedTests[i];
            var (filePath, lineNumber, fileUri) = _diagnosticFormatter.ExtractSourceLocation(fail.StackTrace, _processManager.ProjectRoot);
            structuredFailures.Add(new StructuredTestFailure
            {
                Name = fail.Name,
                FullName = fail.FullName,
                Duration = fail.Duration,
                Message = fail.Message,
                StackTrace = fail.StackTrace,
                FilePath = filePath,
                LineNumber = lineNumber,
                FileUri = fileUri
            });
        }

        if (result.FailedTests.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Failures:");
            const int maxDetailedFailures = 25;
            int countToReport = Math.Min(result.FailedTests.Count, maxDetailedFailures);
            for (int i = 0; i < countToReport; i++)
            {
                var fail = structuredFailures[i];
                sb.AppendLine($"• {fail.FullName ?? fail.Name} ({fail.Duration.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)}s)");
                if (!string.IsNullOrWhiteSpace(fail.FilePath) && fail.LineNumber.HasValue)
                {
                    string link = !string.IsNullOrWhiteSpace(fail.FileUri)
                        ? $"[{fail.FilePath}:{fail.LineNumber}]({fail.FileUri})"
                        : $"{fail.FilePath}:{fail.LineNumber}";
                    sb.AppendLine($"  Location: {link}");
                }
                if (!string.IsNullOrWhiteSpace(fail.Message))
                {
                    sb.AppendLine($"  Message: {fail.Message}");
                }
                if (!string.IsNullOrWhiteSpace(fail.StackTrace))
                {
                    sb.AppendLine($"  Stack trace:\n{fail.StackTrace}");
                }
            }

            if (result.FailedTests.Count > maxDetailedFailures)
            {
                int remaining = result.FailedTests.Count - maxDetailedFailures;
                sb.AppendLine($"... and {remaining} more failed test(s).");
            }
        }

        return new CallToolResult
        {
            Content =
            [
                new TextContentBlock { Text = sb.ToString().TrimEnd() }
            ],
            IsError = !success
        };
    }

    internal static (string? filePath, int? lineNumber, string? fileUri) ExtractSourceLocation(string? stackTrace, string? projectRoot)
        => DiagnosticFormatter.Default.ExtractSourceLocation(stackTrace, projectRoot);

    internal static List<StructuredCompilerDiagnostic> ParseCompilerDiagnostics(string? diagnosticText)
        => DiagnosticFormatter.Default.ParseCompilerDiagnostics(diagnosticText);

    internal static string FormatCompilerDiagnostics(
        string? diagnosticText,
        string? projectRoot,
        string? successTrailer = null,
        string? failureTrailer = null,
        bool isSuccess = false,
        int maxWarnings = DiagnosticFormatter.DefaultMaxWarnings)
        => DiagnosticFormatter.Default.FormatCompilerDiagnostics(diagnosticText, projectRoot, successTrailer, failureTrailer, isSuccess, maxWarnings);

    internal static string FormatDiagnostic(StructuredCompilerDiagnostic diagnostic, string? projectRoot)
        => DiagnosticFormatter.Default.FormatDiagnostic(diagnostic, projectRoot);


    [McpServerTool(Name = "unity_stop")]
    [Description("Safely stops the running Unity background instance. Do NOT call this automatically after operations; keep the instance warm for speed. Only use when explicitly requested by the user, to recover from a freeze/hang, or to release project locks so the user can open the Unity GUI.")]
    public async Task<CallToolResult> UnityStopAsync(
        [Description("Optional. If true, forces termination even if Unity is running as an interactive GUI Editor. Defaults to false.")] bool force = false,
        CancellationToken cancellationToken = default)
    {
        if (!_processManager.IsUnityRunning(out int? pid))
        {
            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = "Unity background instance is not running." }],
                IsError = false
            };
        }

        if (_processManager.GetUnityMode(pid) == "GUI" && !force)
        {
            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = "Refusing to stop Unity: The active Unity Editor is running in interactive GUI mode. Stopping it may lose unsaved user changes. Set 'force: true' to stop it anyway." }],
                IsError = true
            };
        }

        bool stopped = await _processManager.StopUnityAsync(force, cancellationToken);
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = stopped ? "Stopped." : "Error: Unity background instance could not be stopped." }],
            IsError = !stopped
        };
    }

    internal static string UnwrapJsonCodeIfPresent(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return code;
        string trimmed = code.Trim();
        if (trimmed.StartsWith("{") && trimmed.EndsWith("}"))
        {
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty("code", out var codeElem) &&
                    codeElem.ValueKind == JsonValueKind.String)
                {
                    return codeElem.GetString() ?? code;
                }
            }
            catch
            {
                // Not valid JSON, keep as raw C# code
            }
        }
        return code;
    }
}
