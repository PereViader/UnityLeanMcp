using System.Collections.Generic;

namespace UnityLeanMcp.Mcp;

public interface IDiagnosticFormatter
{
    (string? filePath, int? lineNumber, string? fileUri) ExtractSourceLocation(string? stackTrace, string? projectRoot);

    string SanitizeTestStackTrace(string? stackTrace);

    string CompactTestStackTrace(string? stackTrace, int maxLines = 3);

    string FormatEvalException(string? message);

    List<StructuredCompilerDiagnostic> ParseCompilerDiagnostics(string? diagnosticText);

    string FormatCompilerDiagnostics(
        string? diagnosticText,
        string? projectRoot,
        string? successTrailer = null,
        string? failureTrailer = null,
        bool isSuccess = false,
        int maxWarnings = DiagnosticFormatter.DefaultMaxWarnings,
        bool isEval = false);

    string FormatCompilerDiagnostics(
        IReadOnlyList<StructuredCompilerDiagnostic> diagnostics,
        string? rawDiagnosticText,
        string? projectRoot,
        string? successTrailer = null,
        string? failureTrailer = null,
        bool isSuccess = false,
        int maxWarnings = DiagnosticFormatter.DefaultMaxWarnings,
        bool isEval = false) =>
        FormatCompilerDiagnostics(
            rawDiagnosticText,
            projectRoot,
            successTrailer,
            failureTrailer,
            isSuccess,
            maxWarnings,
            isEval);

    string FormatDiagnostic(StructuredCompilerDiagnostic diagnostic, string? projectRoot, bool isEval = false);
}
