using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    [Description("Evaluates C# top-level script source code in-memory against the active Unity Editor to query or modify state. Write code directly as top-level statements without class or method wrappers. Top-level 'await' is supported for asynchronous code. Use 'return <value>;' to return a result; void statements and 'return;' complete without returning a value. No namespaces are pre-imported by default; include 'using UnityEngine;' to access Unity types (e.g., GameObject, Transform).")]
    public async Task<CallToolResult> UnityEvalAsync(
        [Description("Raw C# source text to evaluate. Send plain text directly—do not wrap in JSON.")] string code,
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
        [Description("Exact fully qualified test names in 'FixtureName.MethodName' or 'Namespace.FixtureName.MethodName' format. Matches exact names only.")]
        SingleOrArray? testNames = null,

        [Description(".NET Regular Expression pattern(s) to match test names, fixtures, or namespaces (e.g. ['.*Movement.*']). Evaluated as .NET Regex (do not use glob syntax like *Test*).")]
        SingleOrArray? groupNames = null,

        [Description("Test category filter(s) to include or exclude (prefix with '!' to exclude, e.g. '!Integration').")]
        SingleOrArray? categoryNames = null,

        [Description("Test assembly name(s) without .dll extension to run.")]
        SingleOrArray? assemblyNames = null,

        [Description("Test execution mode: 'all' (default), 'editmode', or 'playmode'.")]
        string? mode = "all",

        [Description("Only run tests that previously failed.")]
        bool failedOnly = false,

        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default)
    {
        string[]? tests = testNames?.ToArray();
        string[]? groups = groupNames?.ToArray();
        string[]? categories = categoryNames?.ToArray();
        string[]? assemblies = assemblyNames?.ToArray();

        mode = string.IsNullOrWhiteSpace(mode) ? "all" : mode;
        var result = await _client.RunTestsAsync(tests, groups, categories, assemblies, mode, failedOnly, progress, cancellationToken);
        var sb = new StringBuilder();

        bool hasFilter = groups != null && groups.Length > 0;
        bool hasCategory = categories != null && categories.Length > 0;
        bool hasTestNames = tests != null && tests.Length > 0;
        bool hasAssemblyNames = assemblies != null && assemblies.Length > 0;
        bool hasAnyFilter = hasFilter || hasCategory || hasTestNames || hasAssemblyNames;
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
        else if (!result.Success && !string.IsNullOrWhiteSpace(result.Message))
        {
            success = false;
            sb.AppendLine($"Test run failed: {result.Message}");
        }
        else if (hasAnyFilter && totalTests == 0)
        {
            success = false;
            string filterDesc = groups != null ? string.Join(", ", groups) : "";
            string categoryDesc = categories != null ? string.Join(", ", categories) : "";

            if (!string.IsNullOrWhiteSpace(filterDesc) && !string.IsNullOrWhiteSpace(categoryDesc))
            {
                sb.AppendLine($"No tests found matching filter '{filterDesc}' and category '{categoryDesc}' (mode: {mode}).");
            }
            else if (!string.IsNullOrWhiteSpace(filterDesc))
            {
                sb.AppendLine($"No tests found matching filter '{filterDesc}' (mode: {mode}).");
            }
            else if (!string.IsNullOrWhiteSpace(categoryDesc))
            {
                sb.AppendLine($"No tests found matching category '{categoryDesc}' (mode: {mode}).");
            }
            else if (hasTestNames)
            {
                sb.AppendLine($"No tests found matching testNames '{string.Join(", ", testNames!)}' (mode: {mode}).");
            }
            else if (hasAssemblyNames)
            {
                sb.AppendLine($"No tests found matching assemblyNames '{string.Join(", ", assemblyNames!)}' (mode: {mode}).");
            }
            else
            {
                sb.AppendLine($"No tests found matching the specified test filter(s) (mode: {mode}).");
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

        const int maxDetailedFailures = 5;
        const int maxTotalFailures = 25;

        var structuredFailures = new List<StructuredTestFailure>();
        for (int i = 0; i < result.FailedTests.Count; i++)
        {
            var fail = result.FailedTests[i];
            string? filePath = null;
            int? lineNumber = null;
            string? fileUri = null;
            string sanitizedStackTrace = string.Empty;

            if (i < maxDetailedFailures)
            {
                (filePath, lineNumber, fileUri) = _diagnosticFormatter.ExtractSourceLocation(fail.StackTrace, _processManager.ProjectRoot);
                sanitizedStackTrace = _diagnosticFormatter.SanitizeTestStackTrace(fail.StackTrace);
            }

            structuredFailures.Add(new StructuredTestFailure
            {
                Name = fail.Name,
                FullName = fail.FullName,
                Duration = fail.Duration,
                Message = fail.Message,
                StackTrace = sanitizedStackTrace,
                FilePath = filePath,
                LineNumber = lineNumber,
                FileUri = fileUri
            });
        }

        if (result.FailedTests.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Failures:");
            int detailedCount = Math.Min(result.FailedTests.Count, maxDetailedFailures);
            for (int i = 0; i < detailedCount; i++)
            {
                var fail = structuredFailures[i];
                string testIdentifier = !string.IsNullOrEmpty(fail.FullName) ? fail.FullName : fail.Name;
                sb.AppendLine($"• {testIdentifier} ({fail.Duration.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)}s)");
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

            int summaryCount = Math.Min(result.FailedTests.Count, maxTotalFailures);
            for (int i = detailedCount; i < summaryCount; i++)
            {
                var fail = structuredFailures[i];
                string testIdentifier = !string.IsNullOrEmpty(fail.FullName) ? fail.FullName : fail.Name;
                string? oneLineMsg = ExtractOneLineSummaryMessage(fail.Message);
                if (!string.IsNullOrWhiteSpace(oneLineMsg))
                {
                    sb.AppendLine($"• {testIdentifier}: {oneLineMsg}");
                }
                else
                {
                    sb.AppendLine($"• {testIdentifier}");
                }
            }

            if (result.FailedTests.Count > maxTotalFailures)
            {
                int remaining = result.FailedTests.Count - maxTotalFailures;
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

    internal static string? ExtractOneLineSummaryMessage(string? message, int maxLineLength = 200)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        using var reader = new StringReader(message);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            string trimmed = line.Trim();
            if (!string.IsNullOrEmpty(trimmed))
            {
                if (maxLineLength > 3 && trimmed.Length > maxLineLength)
                {
                    return trimmed.Substring(0, maxLineLength - 3) + "...";
                }
                return trimmed;
            }
        }

        return null;
    }


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
