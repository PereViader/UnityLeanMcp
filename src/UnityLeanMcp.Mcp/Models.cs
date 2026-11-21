using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace UnityLeanMcp.Mcp;

public class FailedTestInfo
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("fullName")]
    public string FullName { get; set; } = "";

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";

    [JsonPropertyName("stackTrace")]
    public string StackTrace { get; set; } = "";

    [JsonPropertyName("duration")]
    public double Duration { get; set; }
}

public class ConsoleLogEntry
{
    [JsonPropertyName("message")]
    public string Message { get; set; } = "";

    [JsonPropertyName("logType")]
    public string LogType { get; set; } = "";

    [JsonPropertyName("stackTrace")]
    public string? StackTrace { get; set; }
}

public interface IOperationResult
{
    string OperationId { get; set; }
    bool Success { get; set; }
    bool Interrupted { get; set; }
    string Message { get; set; }
}

public class UnityRefreshResult : IOperationResult
{
    [JsonPropertyName("operationId")]
    public string OperationId { get; set; } = "";

    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("interrupted")]
    public bool Interrupted { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";
}

public class UnityTestRunResult : IOperationResult
{
    [JsonPropertyName("runId")]
    public string RunId { get; set; } = "";

    [JsonIgnore]
    public string OperationId { get => RunId; set => RunId = value; }

    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonIgnore]
    public bool Interrupted
    {
        get => string.Equals(ResultState, "Interrupted", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(ResultState, "Cancelled", StringComparison.OrdinalIgnoreCase);
        set
        {
            if (value)
            {
                ResultState = "Interrupted";
            }
            else if (string.Equals(ResultState, "Interrupted", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(ResultState, "Cancelled", StringComparison.OrdinalIgnoreCase))
            {
                ResultState = Success ? "Passed" : (FailCount > 0 ? "Failed" : "");
            }
        }
    }

    [JsonPropertyName("failCount")]
    public int FailCount { get; set; }

    [JsonPropertyName("passCount")]
    public int PassCount { get; set; }

    [JsonPropertyName("skipCount")]
    public int SkipCount { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";

    [JsonPropertyName("resultState")]
    public string ResultState { get; set; } = "";

    [JsonPropertyName("coverageStatus")]
    public string? CoverageStatus { get; set; }

    [JsonPropertyName("coverageMessage")]
    public string? CoverageMessage { get; set; }

    [JsonPropertyName("failedTests")]
    public List<FailedTestInfo> FailedTests { get; set; } = new();
}

public class RunTestsArgs
{
    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "";

    [JsonPropertyName("testNames")]
    public string[]? TestNames { get; set; }

    [JsonPropertyName("groupNames")]
    public string[]? GroupNames { get; set; }

    [JsonPropertyName("categoryNames")]
    public string[]? CategoryNames { get; set; }

    [JsonPropertyName("assemblyNames")]
    public string[]? AssemblyNames { get; set; }

    [JsonPropertyName("failedOnly")]
    public bool FailedOnly { get; set; }

    [JsonPropertyName("coverage")]
    public bool Coverage { get; set; }
}

public class UnityTestRunState
{
    [JsonPropertyName("runId")]
    public string RunId { get; set; } = "";

    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "";

    [JsonPropertyName("testNames")]
    public string[]? TestNames { get; set; }

    [JsonPropertyName("groupNames")]
    public string[]? GroupNames { get; set; }

    [JsonPropertyName("categoryNames")]
    public string[]? CategoryNames { get; set; }

    [JsonPropertyName("assemblyNames")]
    public string[]? AssemblyNames { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    [JsonPropertyName("startedUtc")]
    public string StartedUtc { get; set; } = "";

    [JsonPropertyName("totalTests")]
    public int TotalTests { get; set; }

    [JsonPropertyName("completedTests")]
    public int CompletedTests { get; set; }

    [JsonPropertyName("passCount")]
    public int PassCount { get; set; }

    [JsonPropertyName("failCount")]
    public int FailCount { get; set; }

    [JsonPropertyName("skipCount")]
    public int SkipCount { get; set; }

    [JsonPropertyName("currentTestName")]
    public string CurrentTestName { get; set; } = "";
}


public class UnityOperationResult : IOperationResult
{
    [JsonPropertyName("operationId")]
    public string OperationId { get; set; } = "";

    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("interrupted")]
    public bool Interrupted { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";

    [JsonPropertyName("duration")]
    public double Duration { get; set; }

    [JsonPropertyName("payload")]
    public string? Payload { get; set; }

    [JsonPropertyName("logs")]
    public List<ConsoleLogEntry> Logs { get; set; } = new();
}

public class UnityEvalResult : UnityOperationResult
{
}

public class UnityCompilationException : System.Exception
{
    public List<string> ErrorLines { get; }
    public UnityCompilationException(string message, List<string> errorLines) : base(message)
    {
        ErrorLines = errorLines;
    }
}

public class StructuredTestFailure
{
    public string Name { get; set; } = "";
    public string FullName { get; set; } = "";
    public double Duration { get; set; }
    public string Message { get; set; } = "";
    public string StackTrace { get; set; } = "";
    public string? FilePath { get; set; }
    public int? LineNumber { get; set; }
    public string? FileUri { get; set; }
}

public class StructuredCompilerDiagnostic
{
    public string File { get; set; } = "";
    public int Line { get; set; }
    public int Column { get; set; }
    public string Severity { get; set; } = "";
    public string Code { get; set; } = "";
    public string Message { get; set; } = "";
    public string? Assembly { get; set; }
}

public class GetCoverageArgs
{
    [JsonPropertyName("paths")]
    public string[] Paths { get; set; } = System.Array.Empty<string>();
}

public class UncoveredMethodSpan
{
    [JsonPropertyName("method")]
    public string Method { get; set; } = "";

    [JsonPropertyName("startLine")]
    public int StartLine { get; set; }

    [JsonPropertyName("endLine")]
    public int EndLine { get; set; }
}

public class CoverageFileResult
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("totalPoints")]
    public int TotalPoints { get; set; }

    [JsonPropertyName("coveredPoints")]
    public int CoveredPoints { get; set; }

    [JsonPropertyName("uncoveredLines")]
    public int[] UncoveredLines { get; set; } = System.Array.Empty<int>();

    [JsonPropertyName("uncoveredSpans")]
    public List<UncoveredMethodSpan> UncoveredSpans { get; set; } = new();
}

public class CoverageResult
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("files")]
    public List<CoverageFileResult> Files { get; set; } = new();

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";
}
