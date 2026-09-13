using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace UnityLeanMcp.Mcp;

public interface IDiagnosticFormatter
{
    (string? filePath, int? lineNumber, string? fileUri) ExtractSourceLocation(string? stackTrace, string? projectRoot);
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
        {
            string uriAnchor = lineNumber.HasValue && !rawFile.Contains("#L") ? $"#L{lineNumber.Value}" : "";
            return $"{rawFile}{uriAnchor}";
        }

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
        return normalizedPath.StartsWith('/')
            ? $"file://{normalizedPath}{anchor}"
            : $"file:///{normalizedPath}{anchor}";
    }

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
        var diagnostics = ParseCompilerDiagnostics(diagnosticText);
        if (diagnostics.Count == 0)
        {
            if (isSuccess)
            {
                if (string.IsNullOrWhiteSpace(diagnosticText) || diagnosticText.Trim() == "AssetDatabase refresh completed successfully.")
                {
                    return successTrailer ?? "";
                }
                return string.IsNullOrWhiteSpace(successTrailer)
                    ? diagnosticText.TrimEnd()
                    : $"{diagnosticText.TrimEnd()}{Environment.NewLine}{successTrailer}";
            }
            else
            {
                if (string.IsNullOrWhiteSpace(diagnosticText))
                {
                    return failureTrailer ?? "";
                }
                if (failureTrailer != null && !diagnosticText.Contains(failureTrailer))
                {
                    return $"{diagnosticText.TrimEnd()}{Environment.NewLine}{failureTrailer}";
                }
                return diagnosticText.TrimEnd();
            }
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

        bool showHeaders = warnings.Count > 0 && errors.Count > 0;
        var sb = new StringBuilder();

        if (warnings.Count > 0)
        {
            if (showHeaders)
            {
                sb.AppendLine("Warnings:");
            }

            int warningsToReport = Math.Min(warnings.Count, maxWarnings);
            for (int i = 0; i < warningsToReport; i++)
            {
                sb.AppendLine(FormatDiagnostic(warnings[i], projectRoot, isEval));
            }

            if (warnings.Count > maxWarnings)
            {
                int omitted = warnings.Count - maxWarnings;
                sb.AppendLine($"... and {omitted} more warning(s) omitted to preserve context window.");
            }
        }

        if (errors.Count > 0)
        {
            if (warnings.Count > 0)
            {
                sb.AppendLine();
            }

            if (showHeaders)
            {
                sb.AppendLine("Errors:");
            }

            foreach (var error in errors)
            {
                sb.AppendLine(FormatDiagnostic(error, projectRoot, isEval));
            }
        }

        bool failed = errors.Count > 0 || !isSuccess;
        if (failed)
        {
            if (!string.IsNullOrWhiteSpace(failureTrailer))
            {
                sb.AppendLine();
                sb.Append(failureTrailer);
            }
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(successTrailer))
            {
                string trailer = successTrailer;
                if (warnings.Count > 0)
                {
                    string warningPart = warnings.Count == 1 ? "1 warning" : $"{warnings.Count} warnings";
                    if (trailer.EndsWith('.'))
                    {
                        trailer = trailer[..^1] + $" ({warningPart}).";
                    }
                    else
                    {
                        trailer = $"{trailer} ({warningPart})";
                    }
                }

                sb.AppendLine();
                sb.Append(trailer);
            }
        }

        return sb.ToString().TrimEnd();
    }
}
