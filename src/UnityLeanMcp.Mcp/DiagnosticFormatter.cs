using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace UnityLeanMcp.Mcp;

public class DiagnosticFormatter : IDiagnosticFormatter
{
    public const int DefaultMaxWarnings = 10;
    public static IDiagnosticFormatter Default { get; } = new DiagnosticFormatter();

    private static readonly Regex s_StackTraceRegex = new(
        @"(?:(?:\)\s+in|\(at|\bin(?=\s+(?:[a-zA-Z]:[\\/]|/|(?:Assets|Packages)[\\/])))\s+)(?<file>(?:[a-zA-Z]:[\\/]|/|[A-Za-z0-9_.\-@\+\s\(\)]+[\\/])[^:\r\n]+?):(?:line\s+)?(?<line>\d+)\)?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex s_BareAtRegex = new(
        @"^\s*at\s+(?<file>(?:[a-zA-Z]:[\\/]|/|[A-Za-z0-9_.\-@\+\s\(\)]+[\\/])[^:\r\n]+?):(?:line\s+)?(?<line>\d+)\)?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex s_CompilerDiagnosticRegex = new(
        @"^(?<file>.+?)(?:\((?<line>\d+)(?:,\s*(?<col>\d+))?\)|:(?<line>\d+)(?::(?<col>\d+))?)?:\s*(?<severity>error|warning):?\s*(?:(?<code>[A-Z0-9]+):\s*)?(?<msg>.+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex s_StandaloneDiagnosticRegex = new(
        @"^(?<severity>error|warning)(?::|\s+(?<code>[A-Z0-9]+):|\s+)(?<msg>.+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static bool IsPathRootedCrossPlatform(string? path) =>
        FileUriBuilder.IsPathRootedCrossPlatform(path);

    public static bool IsEvalSynthetic(string? file) =>
        FileUriBuilder.IsEvalSynthetic(file);

    public static string BuildFileUri(string rawFile, int? lineNumber, string? projectRoot) =>
        FileUriBuilder.BuildFileUri(rawFile, lineNumber, projectRoot);

    public (string? filePath, int? lineNumber, string? fileUri) ExtractSourceLocation(string? stackTrace, string? projectRoot)
    {
        if (string.IsNullOrWhiteSpace(stackTrace))
            return (null, null, null);

        using var reader = new StringReader(stackTrace);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            line = line.Trim();
            if (line.Contains("<filename unknown>", StringComparison.OrdinalIgnoreCase))
                continue;

            var match = s_StackTraceRegex.Match(line);
            if (!match.Success)
            {
                match = s_BareAtRegex.Match(line);
            }
            if (match.Success)
            {
                string rawFile = match.Groups["file"].Value.Trim();
                if (int.TryParse(match.Groups["line"].Value, out int lineNum))
                {
                    string fileUri = BuildFileUri(rawFile, lineNum, projectRoot);
                    return (rawFile, lineNum, fileUri);
                }
            }
        }

        return (null, null, null);
    }

    public string SanitizeTestStackTrace(string? stackTrace)
    {
        if (string.IsNullOrWhiteSpace(stackTrace))
            return string.Empty;

        // Bound the input before splitting so a pathological Unity trace cannot create
        // an unnecessarily large temporary line array. Source location extraction is
        // deliberately performed by the caller on the original trace first.
        string boundedStackTrace = McpOutputLimits.Truncate(
            stackTrace,
            McpOutputLimits.MaxFailureStackTraceCharacters,
            McpOutputLimits.FailureStackTraceTruncationMarker);
        var lines = boundedStackTrace.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

        // 1. Locate the first line matching the initial shared framework runner marker
        int markerIndex = -1;
        for (int i = 0; i < lines.Length; i++)
        {
            string trimmed = lines[i].TrimStart();
            if (IsFrameworkRunnerMarker(trimmed))
            {
                markerIndex = i;
                break;
            }
        }

        if (markerIndex == -1)
        {
            return boundedStackTrace.TrimEnd();
        }

        // 2. Scan backwards from the marker past any intermediate invocation plumbing (reflection, native wrappers)
        int testEntryPointIndex = markerIndex - 1;
        while (testEntryPointIndex >= 0 && (string.IsNullOrWhiteSpace(lines[testEntryPointIndex]) || IsInvocationPlumbing(lines[testEntryPointIndex].TrimStart())))
        {
            testEntryPointIndex--;
        }

        if (testEntryPointIndex < 0)
        {
            return boundedStackTrace.TrimEnd();
        }

        // 3. Keep all lines from 0 to testEntryPointIndex inclusive
        var keptLines = new string[testEntryPointIndex + 1];
        Array.Copy(lines, 0, keptLines, 0, testEntryPointIndex + 1);
        return McpOutputLimits.Truncate(
            string.Join(Environment.NewLine, keptLines).TrimEnd(),
            McpOutputLimits.MaxFailureStackTraceCharacters,
            McpOutputLimits.FailureStackTraceTruncationMarker);
    }

    internal static bool IsFrameworkRunnerMarker(string line) =>
        line.Contains("NUnit.Framework.Internal.") ||
        line.Contains("UnityEditor.TestTools.TestRunner.") ||
        line.Contains("UnityEngine.TestRunner.") ||
        line.Contains("TestMethodCommand");

    internal static bool IsInvocationPlumbing(string line) =>
        line.Contains("System.Reflection.") ||
        line.Contains("System.RuntimeMethodHandle") ||
        line.Contains("(wrapper ");

    public string CompactTestStackTrace(string? stackTrace, int maxLines = 3)
    {
        string sanitized = SanitizeTestStackTrace(stackTrace);
        if (string.IsNullOrWhiteSpace(sanitized))
            return string.Empty;

        var lines = sanitized.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length <= maxLines)
            return sanitized;

        var projectLines = new List<string>();
        foreach (var line in lines)
        {
            if (IsProjectLine(line))
            {
                projectLines.Add(line);
            }
        }

        if (projectLines.Count > 0)
        {
            if (projectLines.Count >= maxLines)
            {
                return string.Join(Environment.NewLine, projectLines.Take(maxLines));
            }

            var result = new List<string>(maxLines);
            foreach (var line in lines)
            {
                if (result.Count >= maxLines)
                    break;
                result.Add(line);
            }
            foreach (var projLine in projectLines)
            {
                if (!result.Contains(projLine) && result.Count < maxLines)
                {
                    result.Add(projLine);
                }
            }
            return string.Join(Environment.NewLine, result);
        }

        return string.Join(Environment.NewLine, lines.Take(maxLines));
    }

    internal static bool IsProjectLine(string line)
    {
        string trimmed = line.Trim();
        if (IsFrameworkRunnerMarker(trimmed) || IsInvocationPlumbing(trimmed))
            return false;

        if (trimmed.Contains("NUnit.Framework.") ||
            trimmed.Contains("UnityEngine.Assertions.") ||
            trimmed.Contains("UnityEngine.TestTools."))
            return false;

        if (trimmed.Contains("<filename unknown>") || trimmed.Contains("<...>:0"))
            return false;

        return trimmed.Contains("Assets/") ||
               trimmed.Contains("Assets\\") ||
               trimmed.Contains("Packages/") ||
               trimmed.Contains("Packages\\") ||
               s_StackTraceRegex.IsMatch(trimmed) ||
               trimmed.Contains(".cs:");
    }

    public static bool IsPackageCache(string? file) =>
        FileUriBuilder.IsPackageCache(file);

    public List<StructuredCompilerDiagnostic> ParseCompilerDiagnostics(string? diagnosticText)
    {
        var diagnostics = new List<StructuredCompilerDiagnostic>();
        if (string.IsNullOrWhiteSpace(diagnosticText))
            return diagnostics;

        using var reader = new StringReader(diagnosticText);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            string trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed))
                continue;

            var entries = SplitPipeSeparatedDiagnostics(trimmed);

            foreach (var entry in entries)
            {
                string item = entry.Trim();
                var match = s_CompilerDiagnosticRegex.Match(item);
                if (match.Success)
                {
                    int diagLine = match.Groups["line"].Success && int.TryParse(match.Groups["line"].Value, out int pl) ? pl : 0;
                    int column = match.Groups["col"].Success && int.TryParse(match.Groups["col"].Value, out int pc) ? pc : 0;
                    string code = match.Groups["code"].Success ? match.Groups["code"].Value : "";

                    string rawDiagMsg = match.Groups["msg"].Value.Trim();
                    if (rawDiagMsg.StartsWith(':'))
                        rawDiagMsg = rawDiagMsg.TrimStart(':', ' ');

                    diagnostics.Add(new StructuredCompilerDiagnostic
                    {
                        File = match.Groups["file"].Value,
                        Line = diagLine,
                        Column = column,
                        Severity = match.Groups["severity"].Value.ToLowerInvariant(),
                        Code = code,
                        Message = rawDiagMsg,
                        Assembly = null
                    });
                }
                else
                {
                    var standaloneMatch = s_StandaloneDiagnosticRegex.Match(item);
                    if (standaloneMatch.Success)
                    {
                        string code = standaloneMatch.Groups["code"].Success ? standaloneMatch.Groups["code"].Value : "";
                        string rawDiagMsg = standaloneMatch.Groups["msg"].Value.Trim();
                        if (rawDiagMsg.StartsWith(':'))
                            rawDiagMsg = rawDiagMsg.TrimStart(':', ' ');

                        diagnostics.Add(new StructuredCompilerDiagnostic
                        {
                            File = "",
                            Line = 0,
                            Column = 0,
                            Severity = standaloneMatch.Groups["severity"].Value.ToLowerInvariant(),
                            Code = code,
                            Message = rawDiagMsg,
                            Assembly = null
                        });
                    }
                }
            }
        }

        return diagnostics;
    }

    private static List<string> SplitPipeSeparatedDiagnostics(string line)
    {
        if (!line.Contains(" | ", StringComparison.Ordinal))
        {
            return new List<string> { line };
        }

        string[] segments = line.Split(new[] { " | " }, StringSplitOptions.None);
        var entries = new List<string>(segments.Length) { segments[0] };
        for (int i = 1; i < segments.Length; i++)
        {
            if (s_CompilerDiagnosticRegex.IsMatch(segments[i].Trim()))
            {
                entries.Add(segments[i]);
            }
            else
            {
                entries[^1] = entries[^1] + " | " + segments[i];
            }
        }

        return entries;
    }

    public string FormatDiagnostic(StructuredCompilerDiagnostic diagnostic, string? projectRoot, bool isEval = false)
    {
        string location;
        if (isEval && (string.IsNullOrWhiteSpace(diagnostic.File) || IsEvalSynthetic(diagnostic.File)))
        {
            if (diagnostic.Line > 0 && diagnostic.Column > 0)
            {
                location = $"snippet line {diagnostic.Line}, col {diagnostic.Column}";
            }
            else if (diagnostic.Line > 0)
            {
                location = $"snippet line {diagnostic.Line}";
            }
            else
            {
                location = "snippet";
            }
        }
        else if (!string.IsNullOrWhiteSpace(diagnostic.File) && diagnostic.Line > 0)
        {
            location = BuildFileUri(diagnostic.File, diagnostic.Line, projectRoot);
        }
        else if (!string.IsNullOrWhiteSpace(diagnostic.File))
        {
            location = BuildFileUri(diagnostic.File, null, projectRoot);
        }
        else
        {
            location = "Unknown";
        }

        string codePart = !string.IsNullOrWhiteSpace(diagnostic.Code) ? $" {diagnostic.Code}" : "";
        string severity = !string.IsNullOrWhiteSpace(diagnostic.Severity) ? diagnostic.Severity : "diagnostic";
        return $"• {location}: {severity}{codePart}: {diagnostic.Message}";
    }

    public string FormatCompilerDiagnostics(
        string? diagnosticText,
        string? projectRoot,
        string? successTrailer = null,
        string? failureTrailer = null,
        bool isSuccess = false,
        int maxWarnings = DefaultMaxWarnings,
        bool isEval = false)
    {
        // Limit the parser's input as well as the final response. Unity can return a
        // pathological amount of raw text or a very large number of diagnostics, and
        // parsing the complete value would otherwise allocate an unbounded list of
        // structured diagnostics before the response limit is applied.
        string boundedDiagnosticText = McpOutputLimits.Truncate(
            diagnosticText,
            McpOutputLimits.MaxFormattedOutputCharacters,
            McpOutputLimits.AggregateOutputTruncationMarker);
        var diagnostics = ParseCompilerDiagnostics(boundedDiagnosticText);
        return FormatCompilerDiagnostics(
            diagnostics,
            diagnosticText,
            projectRoot,
            successTrailer,
            failureTrailer,
            isSuccess,
            maxWarnings,
            isEval);
    }

    public string FormatCompilerDiagnostics(
        IReadOnlyList<StructuredCompilerDiagnostic> diagnostics,
        string? rawDiagnosticText,
        string? projectRoot,
        string? successTrailer = null,
        string? failureTrailer = null,
        bool isSuccess = false,
        int maxWarnings = DefaultMaxWarnings,
        bool isEval = false)
    {
        if (diagnostics.Count == 0)
        {
            if (isSuccess)
            {
                if (string.IsNullOrWhiteSpace(rawDiagnosticText) ||
                    rawDiagnosticText.AsSpan().Trim().SequenceEqual("AssetDatabase refresh completed successfully."))
                {
                    return BoundOutput(builder => builder.Append(successTrailer));
                }

                return BoundOutput(
                    builder => builder.AppendTrimmedBounded(
                        rawDiagnosticText,
                        McpOutputLimits.MaxFormattedOutputCharacters,
                        McpOutputLimits.AggregateOutputTruncationMarker),
                    successTrailer,
                    appendTrailer: !string.IsNullOrWhiteSpace(successTrailer));
            }

            if (string.IsNullOrWhiteSpace(rawDiagnosticText))
            {
                return BoundOutput(builder => builder.Append(failureTrailer));
            }

            bool appendFailureTrailer = failureTrailer != null && !rawDiagnosticText.Contains(failureTrailer);
            return BoundOutput(
                builder => builder.AppendTrimmedBounded(
                    rawDiagnosticText,
                    McpOutputLimits.MaxFormattedOutputCharacters,
                    McpOutputLimits.AggregateOutputTruncationMarker),
                appendFailureTrailer ? failureTrailer : null,
                appendTrailer: appendFailureTrailer);
        }

        var errors = new List<StructuredCompilerDiagnostic>();
        var warnings = new List<StructuredCompilerDiagnostic>();

        foreach (var diag in diagnostics)
        {
            if (string.Equals(diag.Severity, "error", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add(diag);
            }
            else
            {
                warnings.Add(diag);
            }
        }

        bool hasProjectErrors = errors.Any(e => !IsPackageCache(e.File));
        if (hasProjectErrors)
        {
            warnings.RemoveAll(w => IsPackageCache(w.File));
        }

        bool failed = errors.Count > 0 || !isSuccess;
        string? trailer = null;
        if (failed)
        {
            if (!string.IsNullOrWhiteSpace(failureTrailer))
            {
                trailer = failureTrailer;
            }
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(successTrailer))
            {
                string computedTrailer = successTrailer;
                if (warnings.Count > 0)
                {
                    string warningPart = warnings.Count == 1 ? "1 warning" : $"{warnings.Count} warnings";
                    if (computedTrailer.EndsWith('.'))
                    {
                        computedTrailer = computedTrailer[..^1] + $" ({warningPart}).";
                    }
                    else
                    {
                        computedTrailer = $"{computedTrailer} ({warningPart})";
                    }
                }

                trailer = computedTrailer;
            }
        }

        return BoundOutput(
            builder =>
            {
                bool showHeaders = warnings.Count > 0 && errors.Count > 0;

                if (errors.Count > 0)
                {
                    if (showHeaders)
                    {
                        builder.AppendLine("Errors:");
                    }

                    foreach (var error in errors)
                    {
                        builder.AppendLine(FormatDiagnostic(error, projectRoot, isEval));
                    }
                }

                if (warnings.Count > 0)
                {
                    if (errors.Count > 0)
                    {
                        builder.AppendLine();
                    }

                    if (showHeaders)
                    {
                        builder.AppendLine("Warnings:");
                    }

                    int warningsToReport = Math.Min(warnings.Count, maxWarnings);
                    for (int i = 0; i < warningsToReport; i++)
                    {
                        builder.AppendLine(FormatDiagnostic(warnings[i], projectRoot, isEval));
                    }

                    if (warnings.Count > maxWarnings)
                    {
                        int omitted = warnings.Count - maxWarnings;
                        builder.AppendLine($"... and {omitted} more warning(s) omitted to preserve context window.");
                    }
                }
            },
            trailer,
            appendTrailer: !string.IsNullOrWhiteSpace(trailer));
    }

    private static string BoundOutput(
        Action<BoundedTextBuilder> appendBody,
        string? trailer = null,
        bool appendTrailer = false)
    {
        string suffix = appendTrailer ? Environment.NewLine + (trailer ?? string.Empty) : string.Empty;
        int bodyLimit = (int)Math.Max(
            0,
            (long)McpOutputLimits.MaxFormattedOutputCharacters - suffix.Length);
        int bodyByteLimit = Math.Max(
            0,
            McpOutputLimits.MaxFormattedOutputBytes - Encoding.UTF8.GetByteCount(suffix));

        var body = new BoundedTextBuilder(
            bodyLimit,
            bodyByteLimit,
            McpOutputLimits.AggregateOutputTruncationMarker);
        appendBody(body);

        string text = appendTrailer ? body.ToString() + suffix : body.ToString();
        return text.TrimEnd();
    }
}
