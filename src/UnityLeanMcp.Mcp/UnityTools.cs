using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text.Json;
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
    private readonly IUnityPathResolver _pathResolver;
    private readonly UnityToolOutputFormatter _formatter;

    public UnityTools(
        IUnityClient client,
        IUnityPathResolver pathResolver,
        IDiagnosticFormatter? diagnosticFormatter = null)
    {
        _client = client;
        _pathResolver = pathResolver;
        _formatter = new UnityToolOutputFormatter(diagnosticFormatter);
    }

    private static CallToolResult Result(string text, bool isError = false) => new()
    {
        Content = [new TextContentBlock { Text = text }],
        IsError = isError
    };

    private static CallToolResult Error(string message) =>
        Result(message.StartsWith("Error:") ? message : $"Error: {message}", isError: true);

    [McpServerTool(Name = "unity_refresh")]
    [Description("Refreshes AssetDatabase and returns compiler and asset import diagnostics. C# compiler errors persist across no-op refreshes, while non-C# diagnostics (shaders, import failures) are captured only during active imports and not re-emitted if assets are unchanged. Normal refresh is fast when unchanged; set clean to true only when a full script recompilation is needed to recover from a stale or corrupted compiler cache; clean refreshes are more expensive.")]
    public async Task<CallToolResult> UnityRefreshAsync(
        [Description("Optional. If true, forces a more expensive full script recompilation by clearing the assembly compiler cache. Defaults to false; use only when recovering from corrupted cache or stale errors.")]
        bool clean = false,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _client.RefreshAsync(isRecompile: clean, progress, cancellationToken);
            string text = _formatter.FormatRefreshResult(result, clean, _pathResolver.ProjectRoot);
            return Result(text, isError: !result.Success);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Error(ex.Message);
        }
    }

    [McpServerTool(Name = "unity_eval")]
    [Description("Evaluates C# top-level statements in the active Unity Editor with top-level await. Automatically ensures compilation and refresh are settled prior to execution, so manual pre-refresh is unnecessary. Use 'return <value>;' to return results. Call 'return Inspect.Help();' for cheatsheet.")]
    public async Task<CallToolResult> UnityEvalAsync(
        [Description("Raw C# source text to evaluate. Send plain text directly—do not wrap in JSON.")] string code,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            string resolvedCode = UnwrapJsonCodeIfPresent(code);
            var result = await _client.EvalAsync(resolvedCode, progress, cancellationToken);
            string text = _formatter.FormatEvalResult(result, _pathResolver.ProjectRoot);
            return Result(text, isError: !result.Success);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Error(ex.Message);
        }
    }

    [McpServerTool(Name = "unity_test")]
    [Description("Runs Unity tests in 'editmode' or 'playmode'. Automatically ensures compilation and refresh are settled prior to execution, so manual pre-refresh is unnecessary. The mode parameter is required; callers must determine whether target tests are EditMode or PlayMode before calling. Use 'editmode' for fast unit tests, editor utilities, and tests without player/runtime lifecycle. Use 'playmode' for integration tests, tests that load scenes, use MonoBehaviour lifecycle, or yield frames via UnityTest/IEnumerator. Use testNames for exact fully qualified name filters and groupNames for .NET regex filters; categoryNames and assemblyNames are also supported as string arrays. To discover available tests or verify exact test names before running, evaluate Inspect.Tests(\"<filter>\") via unity_eval. Set failedOnly to re-run only failed tests (recommended for slow suites). Set coverage to true to capture in-memory code coverage.")]
    public async Task<CallToolResult> UnityTestAsync(
        [Description("Test execution mode: 'editmode' or 'playmode' (required). Must be specified explicitly. Use 'editmode' for unit tests and editor utilities; use 'playmode' for integration tests, scene loading, and MonoBehaviour runtime tests.")]
        UnityTestMode mode,

        [Description("Exact fully qualified test names in 'FixtureName.MethodName' or 'Namespace.FixtureName.MethodName' format. Matches exact names only.")]
        string[]? testNames = null,

        [Description(".NET Regular Expression patterns to match test names, fixtures, or namespaces (e.g. ['.*Movement.*']). Evaluated as .NET Regex (do not use glob syntax like *Test*).")]
        string[]? groupNames = null,

        [Description("Test category filters to include or exclude (prefix with '!' to exclude, e.g. '!Integration').")]
        string[]? categoryNames = null,

        [Description("Test assembly names without the .dll extension to run.")]
        string[]? assemblyNames = null,

        [Description("Only run tests that previously failed. Recommended for slow test suites; skip if tests run quickly.")]
        bool failedOnly = false,

        [Description("If true, records in-memory code coverage during test execution. Supported for 'editmode' tests across all Unity versions, and for 'playmode' tests on Unity 6.7+. Coverage can then be queried via unity_coverage.")]
        bool coverage = false,

        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!TestModeParser.TryNormalize(mode, out string normalizedMode, out string? modeError))
            {
                return Result(modeError ?? TestModeParser.InvalidModeMessage, isError: true);
            }

            if (!TestFilterValidation.TryValidate(testNames, groupNames, categoryNames, assemblyNames, out string filterError))
            {
                return Result(filterError, isError: true);
            }

            var result = await _client.RunTestsAsync(
                testNames,
                groupNames,
                categoryNames,
                assemblyNames,
                normalizedMode,
                failedOnly,
                coverage,
                progress,
                cancellationToken);
            var (text, success) = _formatter.FormatTestRunResult(
                result,
                normalizedMode,
                testNames,
                groupNames,
                categoryNames,
                assemblyNames,
                failedOnly,
                _pathResolver.ProjectRoot);

            return Result(text, isError: !success);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Error(ex.Message);
        }
    }

    [McpServerTool(Name = "unity_coverage")]
    [Description("Queries in-memory code coverage for specified files or directories from the latest test run. Does not run tests. Accepts individual C# files and directory paths (which include all scripts in that folder and its subdirectories). Returns line coverage percentages and lists uncovered line spans for each file.")]
    public async Task<CallToolResult> UnityCoverageAsync(
        [Description("File or directory paths to inspect coverage for (e.g. ['Assets/Scripts/PlayerController.cs'] or ['Assets/Scripts/Combat/']). Directory paths recursively match all contained C# files. Accepts a single string or an array of strings. Required.")]
        string[] paths,

        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (paths == null || paths.Length == 0)
            {
                return Error("The 'paths' parameter is required. Specify one or more file or directory paths to inspect coverage for.");
            }

            for (int i = 0; i < paths.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(paths[i]))
                {
                    return Error("The 'paths' parameter contains an empty or whitespace-only path.");
                }
            }

            var result = await _client.GetCoverageAsync(paths, progress, cancellationToken);
            var (text, isError) = _formatter.FormatCoverageResult(result);
            return Result(text, isError: isError);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Error($"Coverage query failed: {ex.Message}");
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

    internal static void NormalizeToolArguments(CallToolRequestParams? @params)
    {
        if (@params?.Arguments == null) return;

        NormalizeArrayOrSingle(@params.Arguments, "testNames");
        NormalizeArrayOrSingle(@params.Arguments, "groupNames");
        NormalizeArrayOrSingle(@params.Arguments, "categoryNames");
        NormalizeArrayOrSingle(@params.Arguments, "assemblyNames");
        NormalizeArrayOrSingle(@params.Arguments, "paths");
    }

    private static void NormalizeArrayOrSingle(IDictionary<string, JsonElement> arguments, string key)
    {
        if (!arguments.TryGetValue(key, out var elem)) return;

        if (elem.ValueKind == JsonValueKind.String)
        {
            string? s = elem.GetString();
            arguments[key] = JsonSerializer.SerializeToElement(s != null ? new[] { s } : Array.Empty<string>());
        }
    }
}
