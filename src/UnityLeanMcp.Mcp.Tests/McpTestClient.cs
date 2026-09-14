using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace UnityLeanMcp.Mcp.Tests;

public record McpToolResult(bool IsError, string Text, JsonElement RawResult);

public class McpTestClient : IAsyncDisposable
{
    private const int MaxStderrTailCharacters = 8 * 1024;
    private readonly Process _process;
    private readonly StreamWriter _writer;
    private readonly StreamReader _reader;
    private readonly BoundedStderrTail _stderrTail = new(MaxStderrTailCharacters);
    private int _nextId = 1;
    private bool _initialized;

    public McpTestClient(string? projectRoot = null)
    {
        string root = GetRepoRoot();
        string unityRoot = projectRoot ?? Path.Combine(root, "src", "UnityLeanMcp.Unity3d");
        string dllPath = GetMcpServerDllPath();

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"\"{dllPath}\" --project \"{unityRoot}\"",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = System.Text.Encoding.UTF8,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        _process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start UnityLeanMcp.Mcp process.");
        
        // Drain stderr continuously to avoid pipe deadlock while retaining only
        // a bounded tail for actionable diagnostics when the server exits early.
        _ = DrainStderrAsync(_process.StandardError);

        _writer = _process.StandardInput;
        _reader = _process.StandardOutput;
    }

    public static string GetRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src", "UnityLeanMcp.Unity3d")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new DirectoryNotFoundException("Could not find repository root");
    }

    public static string GetUnityProjectRoot()
    {
        return Path.Combine(GetRepoRoot(), "src", "UnityLeanMcp.Unity3d");
    }

    public static string GetMcpServerDllPath()
    {
        string unityRoot = GetUnityProjectRoot();
        string publishedDll = Path.Combine(unityRoot, "Packages", "com.pereviader.unityleanmcp", "MCP~", "UnityLeanMcp.Mcp.dll");
        if (File.Exists(publishedDll)) return publishedDll;

        throw new FileNotFoundException(
            $"Could not find the published Release UnityLeanMcp.Mcp.dll at {publishedDll}. Run dotnet publish before starting integration tests.",
            publishedDll);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized) return;

        int id = Interlocked.Increment(ref _nextId);
        string initMsg = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = id,
            method = "initialize",
            paramsObj = new
            {
                protocolVersion = "2024-11-05",
                capabilities = new { },
                clientInfo = new { name = "xunit-test-client", version = "1.0.0" }
            }
        }).Replace("paramsObj", "params");

        await _writer.WriteLineAsync(initMsg.AsMemory(), cancellationToken);
        await _writer.FlushAsync(cancellationToken);

        string? response = await _reader.ReadLineAsync(cancellationToken);
        if (string.IsNullOrEmpty(response))
        {
            throw CreateUnexpectedEofException("during initialization");
        }

        // Send notifications/initialized
        string initializedNotif = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            method = "notifications/initialized"
        });

        await _writer.WriteLineAsync(initializedNotif.AsMemory(), cancellationToken);
        await _writer.FlushAsync(cancellationToken);
        _initialized = true;
    }

    public List<JsonElement> ReceivedNotifications { get; } = new();

    public async Task<McpToolResult> CallToolAsync(string toolName, object? arguments = null, TimeSpan? timeout = null, string? progressToken = null)
    {
        if (!_initialized)
        {
            await InitializeAsync();
        }

        int id = Interlocked.Increment(ref _nextId);
        object paramsObj = progressToken != null
            ? new { name = toolName, arguments = arguments ?? new { }, _meta = new { progressToken } }
            : new { name = toolName, arguments = arguments ?? new { } };

        string callMsg = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = id,
            method = "tools/call",
            paramsObj
        }).Replace("paramsObj", "params");

        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(180));

        await _writer.WriteLineAsync(callMsg.AsMemory(), cts.Token);
        await _writer.FlushAsync(cts.Token);

        while (true)
        {
            string? responseLine = await _reader.ReadLineAsync(cts.Token);
            if (string.IsNullOrEmpty(responseLine))
            {
                throw CreateUnexpectedEofException($"without response for tool '{toolName}'");
            }

            using var doc = JsonDocument.Parse(responseLine);
            var root = doc.RootElement.Clone();

            if (root.TryGetProperty("method", out var methodProp))
            {
                ReceivedNotifications.Add(root);
                continue;
            }

            if (root.TryGetProperty("id", out var idProp) && idProp.GetInt32() == id)
            {
                if (root.TryGetProperty("error", out var errorElem))
                {
                    return new McpToolResult(true, $"JSON-RPC Error: {errorElem.GetRawText()}", root);
                }

                if (root.TryGetProperty("result", out var resultElem))
                {
                    bool isError = resultElem.TryGetProperty("isError", out var isErrProp) && isErrProp.GetBoolean();
                    string text = "";
                    if (resultElem.TryGetProperty("content", out var contentElem) && contentElem.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in contentElem.EnumerateArray())
                        {
                            if (item.TryGetProperty("text", out var textProp))
                            {
                                text += textProp.GetString();
                            }
                        }
                    }

                    return new McpToolResult(isError, text, resultElem);
                }

                return new McpToolResult(true, $"Unexpected response: {responseLine}", root);
            }
        }
    }


    public async ValueTask DisposeAsync()
    {
        try
        {
            _writer.Close();
        }
        catch { }

        try
        {
            if (!_process.WaitForExit(2000))
            {
                _process.Kill(true);
            }
        }
        catch { }

        _process.Dispose();
        await Task.CompletedTask;
    }

    private async Task DrainStderrAsync(StreamReader stderr)
    {
        char[] buffer = new char[1024];
        try
        {
            int read;
            while ((read = await stderr.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
            {
                _stderrTail.Append(new string(buffer, 0, read));
            }
        }
        catch (ObjectDisposedException)
        {
            // Disposal is expected when the client tears down the child process.
        }
        catch (IOException)
        {
            // A broken child-process pipe must not prevent the test from reporting
            // the primary protocol failure and the stderr captured up to that point.
        }
    }

    private InvalidOperationException CreateUnexpectedEofException(string context)
    {
        string processState;
        try
        {
            processState = _process.HasExited
                ? $" Process exited with code {_process.ExitCode}."
                : " Process is still running.";
        }
        catch (InvalidOperationException)
        {
            processState = " Process state is unavailable.";
        }

        return new InvalidOperationException(
            $"MCP server closed connection {context}.{processState} Stderr tail:\n{_stderrTail.GetText()}");
    }
}

internal sealed class BoundedStderrTail
{
    private const string TruncationMarker = "... (stderr truncated; showing the most recent output)\n";
    private readonly object _gate = new();
    private readonly int _maxCharacters;
    private readonly StringBuilder _buffer;
    private bool _truncated;

    public BoundedStderrTail(int maxCharacters)
    {
        if (maxCharacters <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxCharacters));
        }

        _maxCharacters = maxCharacters;
        _buffer = new StringBuilder(Math.Min(maxCharacters, 1024));
    }

    public void Append(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        lock (_gate)
        {
            _buffer.Append(value);
            if (_buffer.Length > _maxCharacters)
            {
                _buffer.Remove(0, _buffer.Length - _maxCharacters);
                _truncated = true;
            }
        }
    }

    public string GetText()
    {
        lock (_gate)
        {
            string text = _buffer.ToString();
            return _truncated ? TruncationMarker + text : text;
        }
    }
}
