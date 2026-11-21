using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace UnityLeanMcp.Mcp;

internal sealed partial class UnityToolOutputFormatter
{
    public (string Text, bool Success) FormatTestRunResult(
        UnityTestRunResult result,
        string normalizedMode,
        string[]? testNames,
        string[]? groupNames,
        string[]? categoryNames,
        string[]? assemblyNames,
        bool failedOnly,
        string? projectRoot)
    {
        var output = new BoundedTextBuilder(
            McpOutputLimits.MaxFormattedOutputCharacters,
            McpOutputLimits.AggregateOutputTruncationMarker);

        bool hasAnyFilter = (groupNames is { Length: > 0 }) ||
                            (categoryNames is { Length: > 0 }) ||
                            (testNames is { Length: > 0 }) ||
                            (assemblyNames is { Length: > 0 });
        int totalTests = result.PassCount + result.FailCount + result.SkipCount;
        var failedTests = result.FailedTests ?? new List<FailedTestInfo>();
        bool success = result.Success && result.FailCount == 0 && !(hasAnyFilter && totalTests == 0 && !failedOnly);

        output.Append(FormatTestOutcomeHeader(
            result,
            normalizedMode,
            testNames,
            groupNames,
            categoryNames,
            assemblyNames,
            projectRoot,
            failedOnly));

        FormatTestFailures(output, failedTests, projectRoot);

        return (output.ToString().TrimEnd(), success);
    }

    private string FormatTestOutcomeHeader(
        UnityTestRunResult result,
        string normalizedMode,
        string[]? testNames,
        string[]? groupNames,
        string[]? categoryNames,
        string[]? assemblyNames,
        string? projectRoot,
        bool failedOnly = false)
    {
        if (result.ResultState == "CompileError")
        {
            return _diagnosticFormatter.FormatCompilerDiagnostics(
                result.Message,
                projectRoot,
                failureTrailer: "Test execution aborted: Script compilation failed.",
                isSuccess: false);
        }

        var headerOutput = new BoundedTextBuilder(
            McpOutputLimits.MaxFormattedOutputCharacters,
            McpOutputLimits.AggregateOutputTruncationMarker);

        bool hasAnyFilter = (groupNames is { Length: > 0 }) ||
                            (categoryNames is { Length: > 0 }) ||
                            (testNames is { Length: > 0 }) ||
                            (assemblyNames is { Length: > 0 });
        int totalTests = result.PassCount + result.FailCount + result.SkipCount;

        bool isInterrupted = result.Interrupted ||
            string.Equals(result.ResultState, "Interrupted", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(result.ResultState, "Cancelled", StringComparison.OrdinalIgnoreCase);

        if (isInterrupted)
        {
            headerOutput.Append("Test run interrupted: ");
            headerOutput.AppendTrimmedBounded(
                result.Message,
                McpOutputLimits.MaxFailureMessageCharacters,
                McpOutputLimits.FailureMessageTruncationMarker);
            headerOutput.AppendLine();
        }
        else if (!result.Success && totalTests == 0 && result.FailCount == 0)
        {
            headerOutput.Append("Test run failed: ");
            string msg = !string.IsNullOrWhiteSpace(result.Message) ? result.Message : "Unknown runner error";
            headerOutput.AppendTrimmedBounded(
                msg,
                McpOutputLimits.MaxFailureMessageCharacters,
                McpOutputLimits.FailureMessageTruncationMarker);
            headerOutput.AppendLine();
        }
        else if (failedOnly && totalTests == 0)
        {
            headerOutput.AppendLine("No previously failed tests found.");
        }
        else if (hasAnyFilter && totalTests == 0)
        {
            AppendEmptyFilterHints(headerOutput, normalizedMode, testNames, groupNames, categoryNames, assemblyNames);
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
        else if (result.Message?.StartsWith("Unity is busy", StringComparison.OrdinalIgnoreCase) == true)
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

        if (string.Equals(result.CoverageStatus, "unavailable", StringComparison.OrdinalIgnoreCase))
        {
            headerOutput.Append("Coverage unavailable: ");
            headerOutput.AppendTrimmedBounded(
                result.CoverageMessage,
                McpOutputLimits.MaxFailureMessageCharacters,
                McpOutputLimits.FailureMessageTruncationMarker);
            headerOutput.AppendLine();
        }
        else if (string.Equals(result.CoverageStatus, "available", StringComparison.OrdinalIgnoreCase))
        {
            headerOutput.AppendLine("Coverage captured.");
        }

        return headerOutput.ToString();
    }

    private static void AppendEmptyFilterHints(
        BoundedTextBuilder headerOutput,
        string normalizedMode,
        string[]? testNames,
        string[]? groupNames,
        string[]? categoryNames,
        string[]? assemblyNames)
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

        string modeHint = normalizedMode == "editmode"
            ? "Hint: If the target tests require scene loading, MonoBehaviour lifecycle, or runtime execution (such as PlayMode integration tests), try running with mode 'playmode'."
            : "Hint: If the target tests are unit tests or editor utilities without runtime/player lifecycle, try running with mode 'editmode'.";
        headerOutput.AppendLine(modeHint);
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

        output.AppendLine();
        output.AppendLine("Failures:");

        // Keep compact summaries available even when the first detailed failures contain
        // large messages and stack traces. The summary budget is sufficient for 20 entries
        // at the existing identifier/message limits, including platform-newline overhead.
        int detailedCount = Math.Min(failedTests.Count, maxDetailedFailures);
        int summaryCount = Math.Min(failedTests.Count, maxTotalFailures);
        int summaryFailureCount = Math.Max(0, summaryCount - detailedCount);
        int availableFailureCharacters = Math.Max(0, McpOutputLimits.MaxFormattedOutputCharacters - output.Length);
        int availableFailureBytes = Math.Max(0, McpOutputLimits.MaxFormattedOutputBytes - output.Utf8ByteLength);

        int summaryBudget = summaryFailureCount > 0
            ? Math.Min(McpOutputLimits.MaxFailureSummaryCharacters, availableFailureCharacters)
            : 0;
        int summaryByteBudget = summaryFailureCount > 0
            ? Math.Min(McpOutputLimits.MaxFailureSummaryBytes, availableFailureBytes)
            : 0;

        int detailBudget = Math.Max(0, availableFailureCharacters - summaryBudget);
        int detailByteBudget = Math.Max(0, availableFailureBytes - summaryByteBudget);

        var detailedOutput = new BoundedTextBuilder(
            detailBudget,
            detailByteBudget,
            McpOutputLimits.DetailedFailureOutputTruncationMarker);
        var summaryOutput = new BoundedTextBuilder(
            summaryBudget,
            summaryByteBudget,
            McpOutputLimits.FailureSummaryOutputTruncationMarker);

        for (int i = 0; i < detailedCount; i++)
        {
            var fail = failedTests[i] ?? new FailedTestInfo();
            var (filePath, lineNumber, fileUri) = _diagnosticFormatter.ExtractSourceLocation(fail.StackTrace, projectRoot);
            string compactedStackTrace = _diagnosticFormatter.CompactTestStackTrace(fail.StackTrace);

            string testIdentifier = !string.IsNullOrEmpty(fail.FullName) ? fail.FullName : fail.Name;
            detailedOutput.Append("• ");
            detailedOutput.AppendBounded(
                testIdentifier,
                McpOutputLimits.MaxFailureIdentifierCharacters,
                McpOutputLimits.FailureIdentifierTruncationMarker);
            detailedOutput.Append(" (");
            detailedOutput.Append(fail.Duration.ToString("F3", CultureInfo.InvariantCulture));
            detailedOutput.AppendLine("s)");

            if (!string.IsNullOrWhiteSpace(filePath) && lineNumber.HasValue)
            {
                detailedOutput.Append("  Location: ");
                string location = !string.IsNullOrWhiteSpace(fileUri)
                    ? fileUri
                    : $"{filePath}:{lineNumber.Value.ToString(CultureInfo.InvariantCulture)}";
                detailedOutput.AppendBounded(
                    location,
                    McpOutputLimits.MaxFailureIdentifierCharacters,
                    McpOutputLimits.FailureIdentifierTruncationMarker);
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

            if (!string.IsNullOrWhiteSpace(compactedStackTrace))
            {
                detailedOutput.AppendLine("  Stack trace:");
                detailedOutput.AppendBounded(
                    compactedStackTrace,
                    McpOutputLimits.MaxFailureStackTraceCharacters,
                    McpOutputLimits.FailureStackTraceTruncationMarker);
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
}
