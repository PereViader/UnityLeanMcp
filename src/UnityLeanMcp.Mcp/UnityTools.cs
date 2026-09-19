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

    private static CallToolResult Result(string text, bool isError = false) => new()
    {
        Content = [new TextContentBlock { Text = text }],
        IsError = isError
    };

    private static CallToolResult Error(string message) =>
        Result(message.StartsWith("Error:") ? message : $"Error: {message}", isError: true);

    [McpServerTool(Name = "unity_refresh")]
    [Description("Refreshes AssetDatabase and returns compiler diagnostics. A normal refresh is fast when unchanged. Set clean to true only when a full script recompilation is needed to recover from a stale or corrupted compiler cache; clean refreshes are more expensive.")]
    public async Task<CallToolResult> UnityRefreshAsync(
        [Description("Optional. If true, forces a more expensive full script recompilation by clearing the assembly compiler cache. Defaults to false; use only when recovering from corrupted cache or stale errors.")]
        bool clean = false,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default)
    {
        try
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
                    _pathResolver.ProjectRoot,
                    successTrailer: successMessage,
                    failureTrailer: failedMessage,
                    isSuccess: result.Success);
                sb.Append(formatted);
            }

            return Result(sb.ToString().TrimEnd(), isError: !result.Success);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Error(ex.Message);
        }
    }

    [McpServerTool(Name = "unity_eval")]
    [Description("Evaluates C# top-level script source code in-memory against the active Unity Editor. Evaluation can mutate Unity state, so treat every call as potentially state-changing even when it is intended to query data. Write code directly as top-level statements without class or method wrappers. Top-level 'await' is supported for asynchronous code. Use 'return <value>;' to return a result; void statements and 'return;' complete without returning a value. No namespaces are pre-imported by default; include 'using UnityEngine;' to access Unity types (e.g., GameObject, Transform).")]
    public async Task<CallToolResult> UnityEvalAsync(
        [Description("Raw C# source text to evaluate. Send plain text directly—do not wrap in JSON.")] string code,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            string resolvedCode = UnwrapJsonCodeIfPresent(code);
            var result = await _client.EvalAsync(resolvedCode, progress, cancellationToken);
            var logs = result.Logs ?? new List<ConsoleLogEntry>();

            string logsText = FormatLogs(logs);

            bool hasLogs = !string.IsNullOrEmpty(logsText);
            var output = new BoundedTextBuilder(
                McpOutputLimits.MaxFormattedOutputCharacters,
                McpOutputLimits.AggregateOutputTruncationMarker);

            void AppendSection(string header, string content)
            {
                if (hasLogs)
                {
                    output.AppendLine("Logs:");
                    output.AppendLine(logsText);
                    output.AppendLine();
                    output.AppendLine(header);
                }

                output.AppendTrimmedBounded(
                    content,
                    McpOutputLimits.MaxFormattedOutputCharacters,
                    McpOutputLimits.AggregateOutputTruncationMarker);
            }

            if (result.Success)
            {
                if (!string.IsNullOrEmpty(result.Payload))
                {
                    AppendSection("Result:", result.Payload);
                }
                else if (hasLogs)
                {
                    output.Append(logsText);
                }
                else
                {
                    output.Append("(Evaluation completed without a return statement. Use 'return <expr>;' to return a value.)");
                }
            }
            else
            {
                AppendSection("Error:", FormatEvalErrorMessage(result));
            }

            return Result(output.ToString().TrimEnd(), isError: !result.Success);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Error(ex.Message);
        }
    }

    private string FormatEvalErrorMessage(UnityEvalResult result)
    {
        if (result.Interrupted)
        {
            return string.IsNullOrWhiteSpace(result.Message)
                ? "Command interrupted by Unity recompilation outside the UnityLeanMcp workflow."
                : result.Message;
        }

        var parsedDiags = _diagnosticFormatter.ParseCompilerDiagnostics(result.Message);
        if (parsedDiags.Count > 0)
        {
            bool isSnippetFailure = parsedDiags.Any(d => DiagnosticFormatter.IsEvalSynthetic(d.File));
            string failureTrailer = isSnippetFailure
                ? "Evaluation aborted: Dynamic snippet compilation failed."
                : "Evaluation aborted: Project script compilation failed.";

            if (isSnippetFailure && parsedDiags.Any(IsMissingUnityNamespaceDiagnostic))
            {
                failureTrailer += "\nHint: Missing using directive? Include 'using UnityEngine;' or 'using UnityEditor;' at the top of your snippet.";
            }

            return _diagnosticFormatter.FormatCompilerDiagnostics(
                result.Message,
                _pathResolver.ProjectRoot,
                failureTrailer: failureTrailer,
                isSuccess: false,
                isEval: true);
        }

        return string.IsNullOrWhiteSpace(result.Message) ? "Evaluation failed." : result.Message;
    }

    private static readonly Regex s_CommonUnityTypesRegex = new(
        @"\b(" +
        "GameObject|Transform|Vector2|Vector3|Vector4|Quaternion|Color|Color32|Bounds|Rect|" +
        "Mathf|Time|Debug|Selection|AssetDatabase|EditorApplication|Component|MonoBehaviour|" +
        "SceneManager|Object|ScriptableObject|Camera|Material|Mesh|Texture|Texture2D|Shader|" +
        "Physics|Physics2D|Ray|RaycastHit|Input|Screen|Application|EditorUtility|PrefabUtility|" +
        "Undo|Gizmos|Handles|EditorWindow" +
        @")\b",
        RegexOptions.Compiled);

    private static bool IsMissingUnityNamespaceDiagnostic(StructuredCompilerDiagnostic diagnostic)
    {
        if (!DiagnosticFormatter.IsEvalSynthetic(diagnostic.File) && !string.IsNullOrWhiteSpace(diagnostic.File))
        {
            return false;
        }

        if (!string.Equals(diagnostic.Code, "CS0103", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(diagnostic.Code, "CS0246", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(diagnostic.Message))
        {
            return false;
        }

        return s_CommonUnityTypesRegex.IsMatch(diagnostic.Message);
    }

    private static string FormatLogs(IReadOnlyList<ConsoleLogEntry> logs)
    {
        var output = new BoundedTextBuilder(
            McpOutputLimits.MaxLogOutputCharacters,
            McpOutputLimits.LogOutputTruncationMarker);

        foreach (var log in logs)
        {
            string? logType = log?.LogType;
            if (logType == "Warning")
            {
                output.Append("[Warning] ");
            }
            else if (logType is "Error" or "Assert" or "Exception")
            {
                output.Append("[");
                output.Append(logType);
                output.Append("] ");
            }

            output.AppendBounded(
                log?.Message,
                McpOutputLimits.MaxLogEntryCharacters,
                McpOutputLimits.LogEntryTruncationMarker);
            output.AppendLine();
        }

        return output.ToString().TrimEnd();
    }


    [McpServerTool(Name = "unity_run_tests")]
    [Description("Runs Unity tests in 'all', 'editmode', or 'playmode' mode. Use testNames for exact fully qualified name filters and groupNames for .NET regex filters; categoryNames and assemblyNames are also supported as string arrays. Set failedOnly to run only tests that previously failed.")]
    public async Task<CallToolResult> UnityRunTestsAsync(
        [Description("Exact fully qualified test names in 'FixtureName.MethodName' or 'Namespace.FixtureName.MethodName' format. Matches exact names only.")]
        string[]? testNames = null,

        [Description(".NET Regular Expression patterns to match test names, fixtures, or namespaces (e.g. ['.*Movement.*']). Evaluated as .NET Regex (do not use glob syntax like *Test*).")]
        string[]? groupNames = null,

        [Description("Test category filters to include or exclude (prefix with '!' to exclude, e.g. '!Integration').")]
        string[]? categoryNames = null,

        [Description("Test assembly names without the .dll extension to run.")]
        string[]? assemblyNames = null,

        [Description("Test execution mode: 'all' (default), 'editmode', or 'playmode'.")]
        UnityTestMode mode = UnityTestMode.All,

        [Description("Only run tests that previously failed.")]
        bool failedOnly = false,

        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!TestModeParser.TryNormalize(mode, out string normalizedMode))
            {
                return Result(TestModeParser.InvalidModeMessage, isError: true);
            }

            if (!TestFilterValidation.TryValidate(testNames, groupNames, categoryNames, assemblyNames, out string filterError))
            {
                return Result(filterError, isError: true);
            }

            var result = await _client.RunTestsAsync(testNames, groupNames, categoryNames, assemblyNames, normalizedMode, failedOnly, progress, cancellationToken);
            var output = new BoundedTextBuilder(
                McpOutputLimits.MaxFormattedOutputCharacters,
                McpOutputLimits.AggregateOutputTruncationMarker);

            bool hasFilter = groupNames != null && groupNames.Length > 0;
            bool hasCategory = categoryNames != null && categoryNames.Length > 0;
            bool hasTestNames = testNames != null && testNames.Length > 0;
            bool hasAssemblyNames = assemblyNames != null && assemblyNames.Length > 0;
            bool hasAnyFilter = hasFilter || hasCategory || hasTestNames || hasAssemblyNames;
            int totalTests = result.PassCount + result.FailCount + result.SkipCount;
            var failedTests = result.FailedTests ?? new List<FailedTestInfo>();

            bool success = result.Success && result.FailCount == 0 && !(hasAnyFilter && totalTests == 0);

            output.Append(FormatTestOutcomeHeader(
                result,
                normalizedMode,
                testNames,
                groupNames,
                categoryNames,
                assemblyNames));

            FormatTestFailures(output, failedTests, _pathResolver.ProjectRoot);

            return Result(output.ToString().TrimEnd(), isError: !success);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Error(ex.Message);
        }
    }

    private string FormatTestOutcomeHeader(
        UnityTestRunResult result,
        string normalizedMode,
        string[]? testNames,
        string[]? groupNames,
        string[]? categoryNames,
        string[]? assemblyNames)
    {
        if (result.ResultState == "CompileError")
        {
            return _diagnosticFormatter.FormatCompilerDiagnostics(
                result.Message,
                _pathResolver.ProjectRoot,
                failureTrailer: "Test execution aborted: Script compilation failed.",
                isSuccess: false);
        }

        var headerOutput = new BoundedTextBuilder(
            McpOutputLimits.MaxFormattedOutputCharacters,
            McpOutputLimits.AggregateOutputTruncationMarker);

        bool hasFilter = groupNames != null && groupNames.Length > 0;
        bool hasCategory = categoryNames != null && categoryNames.Length > 0;
        bool hasTestNames = testNames != null && testNames.Length > 0;
        bool hasAssemblyNames = assemblyNames != null && assemblyNames.Length > 0;
        bool hasAnyFilter = hasFilter || hasCategory || hasTestNames || hasAssemblyNames;
        int totalTests = result.PassCount + result.FailCount + result.SkipCount;

        if (result.ResultState == "Interrupted")
        {
            headerOutput.Append("Test run interrupted: ");
            headerOutput.AppendTrimmedBounded(
                result.Message,
                McpOutputLimits.MaxFailureMessageCharacters,
                McpOutputLimits.FailureMessageTruncationMarker);
            headerOutput.AppendLine();
        }
        else if (!result.Success && !string.IsNullOrWhiteSpace(result.Message))
        {
            headerOutput.Append("Test run failed: ");
            headerOutput.AppendTrimmedBounded(
                result.Message,
                McpOutputLimits.MaxFailureMessageCharacters,
                McpOutputLimits.FailureMessageTruncationMarker);
            headerOutput.AppendLine();
        }
        else if (hasAnyFilter && totalTests == 0)
        {
            string filterDesc = groupNames != null ? string.Join(", ", groupNames) : "";
            string categoryDesc = categoryNames != null ? string.Join(", ", categoryNames) : "";

            if (!string.IsNullOrWhiteSpace(filterDesc) && !string.IsNullOrWhiteSpace(categoryDesc))
            {
                headerOutput.AppendLine($"No tests found matching filter '{filterDesc}' and category '{categoryDesc}' (mode: {normalizedMode}).");
            }
            else if (!string.IsNullOrWhiteSpace(filterDesc))
            {
                headerOutput.AppendLine($"No tests found matching filter '{filterDesc}' (mode: {normalizedMode}).");
            }
            else if (!string.IsNullOrWhiteSpace(categoryDesc))
            {
                headerOutput.AppendLine($"No tests found matching category '{categoryDesc}' (mode: {normalizedMode}).");
            }
            else if (testNames is { Length: > 0 })
            {
                headerOutput.AppendLine($"No tests found matching testNames '{string.Join(", ", testNames)}' (mode: {normalizedMode}).");
            }
            else if (assemblyNames is { Length: > 0 })
            {
                headerOutput.AppendLine($"No tests found matching assemblyNames '{string.Join(", ", assemblyNames)}' (mode: {normalizedMode}).");
            }
            else
            {
                headerOutput.AppendLine($"No tests found matching the specified test filter(s) (mode: {normalizedMode}).");
            }
        }
        else if (result.Success)
        {
            if (totalTests == 0)
            {
                if (!string.IsNullOrWhiteSpace(result.Message))
                {
                    headerOutput.AppendTrimmedBounded(
                        result.Message,
                        McpOutputLimits.MaxFailureMessageCharacters,
                        McpOutputLimits.FailureMessageTruncationMarker);
                    headerOutput.AppendLine();
                }
                else
                {
                    headerOutput.AppendLine("Tests Passed: 0 passed (no tests found in suite).");
                }
            }
            else
            {
                string skipStr = result.SkipCount > 0 ? $", {result.SkipCount} skipped" : "";
                headerOutput.AppendLine($"Tests Passed: {result.PassCount} passed{skipStr}.");
            }
        }
        else if (result.Message?.Contains("busy", StringComparison.OrdinalIgnoreCase) == true)
        {
            headerOutput.AppendTrimmedBounded(
                result.Message,
                McpOutputLimits.MaxFailureMessageCharacters,
                McpOutputLimits.FailureMessageTruncationMarker);
            headerOutput.AppendLine();
        }
        else
        {
            string skipStr = result.SkipCount > 0 ? $", {result.SkipCount} skipped" : "";
            headerOutput.AppendLine($"Tests Failed: {result.FailCount} failed, {result.PassCount} passed{skipStr}.");
            if (!string.IsNullOrWhiteSpace(result.Message))
            {
                headerOutput.AppendTrimmedBounded(
                    result.Message,
                    McpOutputLimits.MaxFailureMessageCharacters,
                    McpOutputLimits.FailureMessageTruncationMarker);
                headerOutput.AppendLine();
            }
        }

        return headerOutput.ToString();
    }

    private void FormatTestFailures(
        BoundedTextBuilder output,
        IReadOnlyList<FailedTestInfo> failedTests,
        string? projectRoot)
    {
        if (failedTests.Count == 0)
        {
            return;
        }

        const int maxDetailedFailures = 5;
        const int maxTotalFailures = 25;

        int detailedCount = Math.Min(failedTests.Count, maxDetailedFailures);
        var structuredFailures = new List<StructuredTestFailure>(detailedCount);
        for (int i = 0; i < detailedCount; i++)
        {
            var fail = failedTests[i] ?? new FailedTestInfo();
            var (filePath, lineNumber, fileUri) = _diagnosticFormatter.ExtractSourceLocation(fail.StackTrace, projectRoot);
            string sanitizedStackTrace = McpOutputLimits.Truncate(
                _diagnosticFormatter.SanitizeTestStackTrace(fail.StackTrace),
                McpOutputLimits.MaxFailureStackTraceCharacters,
                McpOutputLimits.FailureStackTraceTruncationMarker);

            structuredFailures.Add(new StructuredTestFailure
            {
                Name = McpOutputLimits.Truncate(
                    fail.Name,
                    McpOutputLimits.MaxFailureIdentifierCharacters,
                    McpOutputLimits.FailureIdentifierTruncationMarker),
                FullName = McpOutputLimits.Truncate(
                    fail.FullName,
                    McpOutputLimits.MaxFailureIdentifierCharacters,
                    McpOutputLimits.FailureIdentifierTruncationMarker),
                Duration = fail.Duration,
                Message = McpOutputLimits.Truncate(
                    fail.Message,
                    McpOutputLimits.MaxFailureMessageCharacters,
                    McpOutputLimits.FailureMessageTruncationMarker),
                StackTrace = sanitizedStackTrace,
                FilePath = McpOutputLimits.Truncate(
                    filePath,
                    McpOutputLimits.MaxFailureIdentifierCharacters,
                    McpOutputLimits.FailureIdentifierTruncationMarker),
                LineNumber = lineNumber,
                FileUri = McpOutputLimits.Truncate(
                    fileUri,
                    McpOutputLimits.MaxFailureIdentifierCharacters,
                    McpOutputLimits.FailureIdentifierTruncationMarker)
            });
        }

        output.AppendLine();
        output.AppendLine("Failures:");

        // Keep compact summaries available even when the first detailed failures contain
        // large messages and stack traces. The summary budget is sufficient for 20 entries
        // at the existing identifier/message limits, including platform-newline overhead.
        int summaryCount = Math.Min(failedTests.Count, maxTotalFailures);
        int summaryFailureCount = Math.Max(0, summaryCount - detailedCount);
        int availableFailureCharacters = Math.Max(0, McpOutputLimits.MaxFormattedOutputCharacters - output.Length);
        int summaryBudget = summaryFailureCount > 0
            ? Math.Min(McpOutputLimits.MaxFailureSummaryCharacters, availableFailureCharacters)
            : 0;
        int detailBudget = Math.Max(0, availableFailureCharacters - summaryBudget);
        var detailedOutput = new BoundedTextBuilder(
            detailBudget,
            McpOutputLimits.DetailedFailureOutputTruncationMarker);
        var summaryOutput = new BoundedTextBuilder(
            summaryBudget,
            McpOutputLimits.FailureSummaryOutputTruncationMarker);

        for (int i = 0; i < detailedCount; i++)
        {
            var fail = structuredFailures[i];
            string testIdentifier = !string.IsNullOrEmpty(fail.FullName) ? fail.FullName : fail.Name;
            detailedOutput.Append("• ");
            detailedOutput.AppendBounded(
                testIdentifier,
                McpOutputLimits.MaxFailureIdentifierCharacters,
                McpOutputLimits.FailureIdentifierTruncationMarker);
            detailedOutput.Append(" (");
            detailedOutput.Append(fail.Duration.ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
            detailedOutput.AppendLine("s)");
            if (!string.IsNullOrWhiteSpace(fail.FilePath) && fail.LineNumber.HasValue)
            {
                detailedOutput.Append("  Location: ");
                if (!string.IsNullOrWhiteSpace(fail.FileUri))
                {
                    detailedOutput.Append("[");
                }
                detailedOutput.AppendBounded(
                    fail.FilePath,
                    McpOutputLimits.MaxFailureIdentifierCharacters,
                    McpOutputLimits.FailureIdentifierTruncationMarker);
                detailedOutput.Append(":");
                detailedOutput.Append(fail.LineNumber.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
                if (!string.IsNullOrWhiteSpace(fail.FileUri))
                {
                    detailedOutput.Append("](");
                    detailedOutput.AppendBounded(
                        fail.FileUri,
                        McpOutputLimits.MaxFailureIdentifierCharacters,
                        McpOutputLimits.FailureIdentifierTruncationMarker);
                    detailedOutput.Append(")");
                }
                detailedOutput.AppendLine();
            }
            if (!string.IsNullOrWhiteSpace(fail.Message))
            {
                detailedOutput.Append("  Message: ");
                detailedOutput.AppendBounded(
                    fail.Message,
                    McpOutputLimits.MaxFailureMessageCharacters,
                    McpOutputLimits.FailureMessageTruncationMarker);
                detailedOutput.AppendLine();
            }
            if (!string.IsNullOrWhiteSpace(fail.StackTrace))
            {
                detailedOutput.AppendLine("  Stack trace:");
                detailedOutput.Append(fail.StackTrace);
                detailedOutput.AppendLine();
            }
        }

        for (int i = detailedCount; i < summaryCount; i++)
        {
            var fail = failedTests[i] ?? new FailedTestInfo();
            string testIdentifier = !string.IsNullOrEmpty(fail.FullName) ? fail.FullName : fail.Name;
            string? oneLineMsg = ExtractOneLineSummaryMessage(
                fail.Message,
                McpOutputLimits.MaxFailureSummaryMessageCharacters);
            summaryOutput.Append("• ");
            summaryOutput.AppendBounded(
                testIdentifier,
                McpOutputLimits.MaxFailureIdentifierCharacters,
                McpOutputLimits.FailureIdentifierTruncationMarker);
            if (!string.IsNullOrWhiteSpace(oneLineMsg))
            {
                summaryOutput.Append(": ");
                summaryOutput.Append(oneLineMsg);
            }
            summaryOutput.AppendLine();
        }

        if (failedTests.Count > maxTotalFailures)
        {
            int remaining = failedTests.Count - maxTotalFailures;
            summaryOutput.AppendLine($"... and {remaining} more failed test(s).");
        }

        output.Append(detailedOutput.ToString());
        output.Append(summaryOutput.ToString());
    }

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
                if (maxLineLength > 0 && trimmed.Length > maxLineLength)
                {
                    return maxLineLength > 3
                        ? McpOutputLimits.Truncate(trimmed, maxLineLength, "...")
                        : McpOutputLimits.Truncate(trimmed, maxLineLength, string.Empty);
                }
                return trimmed;
            }
        }

        return null;
    }


    [McpServerTool(Name = "unity_stop")]
    [Description("Stops the running Unity instance when explicitly requested, to recover from a freeze or release project locks. Stopping an interactive GUI Editor can discard unsaved changes; use force: true only with explicit approval. Do not call automatically after operations.")]
    public async Task<CallToolResult> UnityStopAsync(
        [Description("Optional. If true, forces termination even when Unity is an interactive GUI Editor; this may discard unsaved Editor changes and requires explicit user approval. Defaults to false.")] bool force = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!_processManager.IsUnityRunning(out int? pid))
            {
                return Result("Unity background instance is not running.");
            }

            if (_processManager.GetUnityMode(pid) == "GUI" && !force)
            {
                return Result("Refusing to stop Unity: The active Unity Editor is running in interactive GUI mode. Stopping it may lose unsaved user changes. Set 'force: true' to stop it anyway.", isError: true);
            }

            bool stopped = await _processManager.StopUnityAsync(force, cancellationToken);
            return stopped
                ? Result("Stopped.")
                : Error("Unity background instance could not be stopped.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Error(ex.Message);
        }
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
