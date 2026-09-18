using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace UnityLeanMcp.Mcp;

public interface IDiagnosticFormatter
{
    (string? filePath, int? lineNumber, string? fileUri) ExtractSourceLocation(string? stackTrace, string? projectRoot);
    string SanitizeTestStackTrace(string? stackTrace);
    List<StructuredCompilerDiagnostic> ParseCompilerDiagnostics(string? diagnosticText);
    string FormatCompilerDiagnostics(
        string? diagnosticText,
        string? projectRoot,
        string? successTrailer = null,
        string? failureTrailer = null,
        bool isSuccess = false,
        int maxWarnings = DiagnosticFormatter.DefaultMaxWarnings,
        bool isEval = false);
    string FormatDiagnostic(StructuredCompilerDiagnostic diagnostic, string? projectRoot, bool isEval = false);
}

public class DiagnosticFormatter : IDiagnosticFormatter
{
    public const int DefaultMaxWarnings = 10;
    public static IDiagnosticFormatter Default { get; } = new DiagnosticFormatter();

    private static readonly Regex s_StackTraceRegex = new(
        @"(?:(?:in|\bat\b|\()\s*)?(?<file>(?:[a-zA-Z]:[\\/]|/|[A-Za-z0-9_.\-]+[\\/])[^:\r\n()]+):(?:line\s+)?(?<line>\d+)\)?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex s_CompilerDiagnosticRegex = new(
        @"^(?<file>.+?)\((?<line>\d+),(?<col>\d+)\):\s*(?<severity>error|warning)\s+(?<code>[A-Z0-9]+):\s*(?<msg>.+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static bool IsPathRootedCrossPlatform(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        if (Path.IsPathRooted(path))
            return true;

        if (path.StartsWith('/') || path.StartsWith('\\'))
            return true;

        if (path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':')
        {
            if (path.Length == 2 || path[2] == '/' || path[2] == '\\')
                return true;
        }

        return false;
    }

    public static bool IsEvalSynthetic(string? file) =>
        string.Equals(file, "eval", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(file, "snippet", StringComparison.OrdinalIgnoreCase) ||
        (file != null && file.StartsWith('<'));

    public static string BuildFileUri(string rawFile, int? lineNumber, string? projectRoot)
    {
        if (string.IsNullOrWhiteSpace(rawFile) || IsEvalSynthetic(rawFile))
            return string.Empty;

        if (rawFile.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            return BuildExistingFileUri(rawFile, lineNumber);

        string resolvedPath = rawFile;
        if (!IsPathRootedCrossPlatform(resolvedPath) && !string.IsNullOrWhiteSpace(projectRoot))
        {
            string root = projectRoot.Replace('\\', '/').TrimEnd('/');
            string rel = resolvedPath.Replace('\\', '/').TrimStart('/');
            while (rel.StartsWith("./", StringComparison.Ordinal))
            {
                rel = rel[2..];
            }
            resolvedPath = $"{root}/{rel}";
        }

        string normalizedPath = resolvedPath.Replace('\\', '/');
        string anchor = lineNumber.HasValue ? $"#L{lineNumber.Value}" : "";

        // A Windows UNC path is represented by the URI authority (the server),
        // not by an extra pair of leading slashes in the URI path.
        if (normalizedPath.StartsWith("//", StringComparison.Ordinal))
        {
            string uncPath = normalizedPath[2..];
            int separatorIndex = uncPath.IndexOf('/');
            if (separatorIndex > 0)
            {
                string authority = EncodeUriAuthority(uncPath[..separatorIndex], preserveEscapes: false);
                string path = EncodeUriPath(uncPath[separatorIndex..], preserveEscapes: false);
                return $"file://{authority}{path}{anchor}";
            }
        }

        string encodedPath = EncodeUriPath(normalizedPath, preserveEscapes: false);
        return normalizedPath.StartsWith('/')
            ? $"file://{encodedPath}{anchor}"
            : $"file:///{encodedPath}{anchor}";
    }

    private static string BuildExistingFileUri(string rawUri, int? lineNumber)
    {
        int uriStart = "file://".Length;
        string remainder = rawUri[uriStart..];

        int fragmentIndex = remainder.IndexOf('#');
        string fragment = fragmentIndex >= 0 ? remainder[fragmentIndex..] : string.Empty;
        string withoutFragment = fragmentIndex >= 0 ? remainder[..fragmentIndex] : remainder;

        int queryIndex = withoutFragment.IndexOf('?');
        string query = queryIndex >= 0 ? withoutFragment[queryIndex..] : string.Empty;
        string withoutQuery = queryIndex >= 0 ? withoutFragment[..queryIndex] : withoutFragment;

        string authority;
        string path;
        if (withoutQuery.StartsWith("/", StringComparison.Ordinal))
        {
            authority = string.Empty;
            path = withoutQuery;
        }
        else
        {
            int separatorIndex = withoutQuery.IndexOf('/');
            if (separatorIndex < 0)
            {
                authority = withoutQuery;
                path = string.Empty;
            }
            else
            {
                authority = withoutQuery[..separatorIndex];
                path = withoutQuery[separatorIndex..];
            }
        }

        string encodedAuthority = EncodeUriAuthority(authority, preserveEscapes: true);
        string encodedPath = EncodeUriPath(path.Replace('\\', '/'), preserveEscapes: true);
        string lineAnchor = fragment.Length == 0 && lineNumber.HasValue ? $"#L{lineNumber.Value}" : string.Empty;
        return $"file://{encodedAuthority}{encodedPath}{query}{fragment}{lineAnchor}";
    }

    private static string EncodeUriAuthority(string authority, bool preserveEscapes)
    {
        return PercentEncode(authority, IsUriAuthorityCharacter, preserveEscapes, preservePathSeparators: false);
    }

    private static string EncodeUriPath(string path, bool preserveEscapes)
    {
        return PercentEncode(path, IsUriPathCharacter, preserveEscapes, preservePathSeparators: true);
    }

    private static string PercentEncode(
        string value,
        Func<char, bool> isAllowed,
        bool preserveEscapes,
        bool preservePathSeparators)
    {
        var builder = new StringBuilder(value.Length);
        Span<byte> utf8Bytes = stackalloc byte[4];
        for (int i = 0; i < value.Length; i++)
        {
            char character = value[i];
            if (preservePathSeparators && character == '/')
            {
                builder.Append('/');
                continue;
            }

            if (isAllowed(character))
            {
                builder.Append(character);
                continue;
            }

            if (preserveEscapes && character == '%' && i + 2 < value.Length &&
                IsHexDigit(value[i + 1]) && IsHexDigit(value[i + 2]))
            {
                builder.Append('%');
                builder.Append(char.ToUpperInvariant(value[i + 1]));
                builder.Append(char.ToUpperInvariant(value[i + 2]));
                i += 2;
                continue;
            }

            int characterLength = char.IsHighSurrogate(character) && i + 1 < value.Length &&
                                  char.IsLowSurrogate(value[i + 1]) ? 2 : 1;
            int written = Encoding.UTF8.GetBytes(value.AsSpan(i, characterLength), utf8Bytes);
            foreach (byte utf8Byte in utf8Bytes[..written])
            {
                builder.Append('%');
                builder.Append(GetHexDigit(utf8Byte >> 4));
                builder.Append(GetHexDigit(utf8Byte & 0x0F));
            }
            i += characterLength - 1;
        }

        return builder.ToString();
    }

    private static bool IsUriAuthorityCharacter(char character) =>
        IsUnreserved(character) || "!$&'()*+,;=".Contains(character, StringComparison.Ordinal);

    private static bool IsUriPathCharacter(char character) =>
        IsUnreserved(character) || "!$&'()*+,;=:@".Contains(character, StringComparison.Ordinal);

    private static bool IsUnreserved(char character) =>
        char.IsAsciiLetterOrDigit(character) || character is '-' or '.' or '_' or '~';

    private static bool IsHexDigit(char character) =>
        char.IsAsciiDigit(character) || character is >= 'A' and <= 'F' or >= 'a' and <= 'f';

    private static char GetHexDigit(int value) =>
        (char)(value < 10 ? '0' + value : 'A' + value - 10);

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

            var entries = trimmed.Contains(" | ")
                ? trimmed.Split(new[] { " | " }, StringSplitOptions.RemoveEmptyEntries)
                : new[] { trimmed };

            foreach (var entry in entries)
            {
                var match = s_CompilerDiagnosticRegex.Match(entry.Trim());
                if (match.Success)
                {
                    diagnostics.Add(new StructuredCompilerDiagnostic
                    {
                        File = match.Groups["file"].Value,
                        Line = int.Parse(match.Groups["line"].Value),
                        Column = int.Parse(match.Groups["col"].Value),
                        Severity = match.Groups["severity"].Value.ToLowerInvariant(),
                        Code = match.Groups["code"].Value,
                        Message = match.Groups["msg"].Value.Trim(),
                        Assembly = null
                    });
                }
            }
        }

        return diagnostics;
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
        if (diagnostics.Count == 0)
        {
            if (isSuccess)
            {
                if (string.IsNullOrWhiteSpace(diagnosticText) ||
                    diagnosticText.AsSpan().Trim().SequenceEqual("AssetDatabase refresh completed successfully."))
                {
                    return BoundOutput(builder => builder.Append(successTrailer));
                }

                return BoundOutput(
                    builder => builder.AppendTrimmedBounded(
                        diagnosticText,
                        McpOutputLimits.MaxFormattedOutputCharacters,
                        McpOutputLimits.AggregateOutputTruncationMarker),
                    successTrailer,
                    appendTrailer: !string.IsNullOrWhiteSpace(successTrailer));
            }

            if (string.IsNullOrWhiteSpace(diagnosticText))
            {
                return BoundOutput(builder => builder.Append(failureTrailer));
            }

            bool appendFailureTrailer = failureTrailer != null && !diagnosticText.Contains(failureTrailer);
            return BoundOutput(
                builder => builder.AppendTrimmedBounded(
                    diagnosticText,
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

                if (warnings.Count > 0)
                {
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

                if (errors.Count > 0)
                {
                    if (warnings.Count > 0)
                    {
                        builder.AppendLine();
                    }

                    if (showHeaders)
                    {
                        builder.AppendLine("Errors:");
                    }

                    foreach (var error in errors)
                    {
                        builder.AppendLine(FormatDiagnostic(error, projectRoot, isEval));
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
