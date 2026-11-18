using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace UnityLeanMcp.Mcp;

internal sealed partial class UnityToolOutputFormatter
{
    private static readonly Regex s_CommonUnityTypesRegex = new(
        @"\b(" +
        "GameObject|Transform|Vector2|Vector2Int|Vector3|Vector3Int|Vector4|Quaternion|Matrix4x4|" +
        "Color|Color32|Bounds|BoundsInt|Rect|RectInt|Mathf|Time|Debug|Selection|AssetDatabase|" +
        "EditorApplication|Component|MonoBehaviour|SceneManager|Object|ScriptableObject|Camera|" +
        "Material|Mesh|Texture|Texture2D|RenderTexture|Sprite|Shader|Physics|Physics2D|Rigidbody|" +
        "Rigidbody2D|Collider|Collider2D|Ray|RaycastHit|RaycastHit2D|Input|Screen|Application|" +
        "EditorUtility|PrefabUtility|Undo|Gizmos|Handles|EditorWindow|AudioSource|AudioClip|Light" +
        @")\b",
        RegexOptions.Compiled);

    private readonly IDiagnosticFormatter _diagnosticFormatter;

    public UnityToolOutputFormatter(IDiagnosticFormatter? diagnosticFormatter = null)
    {
        _diagnosticFormatter = diagnosticFormatter ?? DiagnosticFormatter.Default;
    }

    public readonly record struct LineSpan(int StartLine, int EndLine);

    public string FormatRefreshResult(UnityRefreshResult result, bool clean, string projectRoot)
    {
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
            string msg = !string.IsNullOrWhiteSpace(result.Message) ? result.Message : interruptedMessage;
            return msg.TrimEnd();
        }

        if (result.Message?.StartsWith("Unity is busy", StringComparison.OrdinalIgnoreCase) == true)
        {
            return result.Message.TrimEnd();
        }

        return _diagnosticFormatter.FormatCompilerDiagnostics(
            result.Message,
            projectRoot,
            successTrailer: successMessage,
            failureTrailer: failedMessage,
            isSuccess: result.Success).TrimEnd();
    }

    public string FormatEvalResult(UnityEvalResult result, string projectRoot)
    {
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
                output.AppendLine(header);
                output.AppendTrimmedBounded(
                    content,
                    McpOutputLimits.MaxFormattedOutputCharacters,
                    McpOutputLimits.AggregateOutputTruncationMarker);
                output.AppendLine();
                output.AppendLine();
                output.AppendLine("Logs:");
                output.Append(logsText);
            }
            else
            {
                output.AppendTrimmedBounded(
                    content,
                    McpOutputLimits.MaxFormattedOutputCharacters,
                    McpOutputLimits.AggregateOutputTruncationMarker);
            }
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
            AppendSection("Error:", FormatEvalErrorMessage(result, projectRoot));
        }

        return output.ToString().TrimEnd();
    }

    public (string Text, bool IsError) FormatCoverageResult(CoverageResult result)
    {
        if (!result.Success)
        {
            return (result.Message, true);
        }

        var files = result.Files ?? new List<CoverageFileResult>();
        if (files.Count == 0)
        {
            return ("No compiled C# scripts found in the specified path(s).", false);
        }

        var output = new BoundedTextBuilder(
            McpOutputLimits.MaxFormattedOutputCharacters,
            McpOutputLimits.AggregateOutputTruncationMarker);

        if (files.Count > 1)
        {
            int aggregateTotalPoints = 0;
            int aggregateCoveredPoints = 0;
            foreach (var file in files)
            {
                if (file == null) continue;
                aggregateTotalPoints += file.TotalPoints;
                aggregateCoveredPoints += file.CoveredPoints;
            }

            if (aggregateTotalPoints > 0)
            {
                double aggPct = (aggregateCoveredPoints / (double)aggregateTotalPoints) * 100.0;
                output.Append("Total Coverage: ");
                output.Append(aggPct.ToString("F1", CultureInfo.InvariantCulture));
                output.Append("% (");
                output.Append(aggregateCoveredPoints.ToString());
                output.Append("/");
                output.Append(aggregateTotalPoints.ToString());
                output.Append(" points across ");
                output.Append(files.Count.ToString());
                output.AppendLine(" files)");
                output.AppendLine();
            }
        }

        foreach (var file in files)
        {
            if (file == null) continue;
            output.Append("• ");
            output.Append(file.Path);
            output.Append(": ");

            if (file.TotalPoints == 0)
            {
                output.AppendLine("No executable sequence points found (interfaces, enums, or declarations without executable code).");
                continue;
            }

            double pct = (file.CoveredPoints / (double)file.TotalPoints) * 100.0;
            output.Append(pct.ToString("F1", CultureInfo.InvariantCulture));
            output.Append("% (");
            output.Append(file.CoveredPoints.ToString());
            output.Append("/");
            output.Append(file.TotalPoints.ToString());
            output.Append(" points)");

            if (file.UncoveredSpans is { Count: > 0 })
            {
                output.Append("  Uncovered lines: ");
                output.Append(FormatUncoveredMethodSpans(file.UncoveredSpans));
            }
            else if (file.UncoveredLines is { Length: > 0 })
            {
                var spans = FormatUncoveredSpans(file.UncoveredLines);
                output.Append("  Uncovered lines: ");

                bool firstSpan = true;
                foreach (var span in spans)
                {
                    if (!firstSpan) output.Append(", ");
                    firstSpan = false;

                    output.Append(span.StartLine.ToString());
                    if (span.EndLine > span.StartLine)
                    {
                        output.Append("-");
                        output.Append(span.EndLine.ToString());
                    }
                }
            }
            output.AppendLine();
        }

        return (output.ToString().TrimEnd(), false);
    }

    private string FormatEvalErrorMessage(UnityEvalResult result, string projectRoot)
    {
        if (result.Interrupted)
        {
            return string.IsNullOrWhiteSpace(result.Message)
                ? "Command interrupted by Unity recompilation outside the UnityLeanMcp workflow."
                : result.Message;
        }

        string boundedMessage = McpOutputLimits.Truncate(
            result.Message,
            McpOutputLimits.MaxFormattedOutputCharacters,
            McpOutputLimits.AggregateOutputTruncationMarker);
        var parsedDiags = _diagnosticFormatter.ParseCompilerDiagnostics(boundedMessage);
        if (parsedDiags.Count > 0)
        {
            bool isSnippetFailure = parsedDiags.Any(d => FileUriBuilder.IsEvalSynthetic(d.File));
            string failureTrailer = isSnippetFailure
                ? "Evaluation aborted: Dynamic snippet compilation failed."
                : "Evaluation aborted: Project script compilation failed.";

            if (isSnippetFailure && parsedDiags.Any(IsMissingUnityNamespaceDiagnostic))
            {
                failureTrailer += "\nHint: Missing using directive? Include 'using UnityEngine;' or 'using UnityEditor;' at the top of your snippet.";
            }

            return _diagnosticFormatter.FormatCompilerDiagnostics(
                parsedDiags,
                result.Message,
                projectRoot,
                failureTrailer: failureTrailer,
                isSuccess: false,
                isEval: true);
        }

        return string.IsNullOrWhiteSpace(result.Message) ? "Evaluation failed." : result.Message;
    }

    private static bool IsMissingUnityNamespaceDiagnostic(StructuredCompilerDiagnostic diagnostic)
    {
        if (!FileUriBuilder.IsEvalSynthetic(diagnostic.File) && !string.IsNullOrWhiteSpace(diagnostic.File))
        {
            return false;
        }

        if (!string.Equals(diagnostic.Code, "CS0103", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(diagnostic.Code, "CS0246", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(diagnostic.Message) &&
               s_CommonUnityTypesRegex.IsMatch(diagnostic.Message);
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

    internal static string FormatUncoveredMethodSpans(IReadOnlyList<UncoveredMethodSpan> spans)
    {
        if (spans == null || spans.Count == 0)
            return "";

        var sb = new StringBuilder();
        int i = 0;
        bool firstMethod = true;

        while (i < spans.Count)
        {
            string currentMethod = spans[i].Method ?? "";
            var lineRanges = new List<string>();
            while (i < spans.Count && (spans[i].Method ?? "") == currentMethod)
            {
                int start = spans[i].StartLine;
                int end = spans[i].EndLine;
                if (end > start)
                    lineRanges.Add($"{start}-{end}");
                else
                    lineRanges.Add(start.ToString());
                i++;
            }

            if (!firstMethod)
                sb.Append(", ");
            firstMethod = false;

            string rangesStr = string.Join(", ", lineRanges);
            if (!string.IsNullOrEmpty(currentMethod))
            {
                sb.Append(currentMethod);
                sb.Append(" (");
                sb.Append(rangesStr);
                sb.Append(")");
            }
            else
            {
                sb.Append(rangesStr);
            }
        }

        return sb.ToString();
    }

    internal static List<LineSpan> FormatUncoveredSpans(int[] uncoveredLines)
    {
        var spans = new List<LineSpan>();
        if (uncoveredLines == null || uncoveredLines.Length == 0)
            return spans;

        var sorted = uncoveredLines.Distinct().OrderBy(l => l).ToArray();
        int spanStart = sorted[0];
        int prev = spanStart;

        for (int i = 1; i < sorted.Length; i++)
        {
            if (sorted[i] == prev + 1)
            {
                prev = sorted[i];
            }
            else
            {
                spans.Add(new LineSpan(spanStart, prev));
                spanStart = sorted[i];
                prev = spanStart;
            }
        }
        spans.Add(new LineSpan(spanStart, prev));
        return spans;
    }
}
