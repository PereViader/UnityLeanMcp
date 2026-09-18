using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;

namespace UnityLeanMcp.Mcp.Tests;

public sealed class MockUnityServer : IAsyncDisposable, IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts;
    private readonly Task _serverTask;
    private readonly Func<MockUnityServer, string, Task<string?>>? _asyncHandler;
    private readonly ConcurrentDictionary<Task, byte> _activeConnections = new();
    private bool _disposed;

    public string ProjectRoot { get; }
    public string UnityTempDir => Path.Combine(ProjectRoot, "Temp");
    public int Port { get; }
    public UnityProcessManager ProcessManager { get; }
    public UnityClient Client { get; }
    public IUnityPathResolver PathResolver => ProcessManager.PathResolver;

    private MockUnityServer(
        string projectRoot,
        TcpListener listener,
        int port,
        UnityProcessManager processManager,
        UnityClient client,
        CancellationTokenSource cts,
        Func<MockUnityServer, string, Task<string?>>? asyncHandler)
    {
        ProjectRoot = projectRoot;
        _listener = listener;
        Port = port;
        ProcessManager = processManager;
        Client = client;
        _cts = cts;
        _asyncHandler = asyncHandler;

        _serverTask = Task.Run(() => RunAcceptLoopAsync(_cts.Token));
    }

    public static Task<MockUnityServer> StartAsync(
        Func<string, string?>? commandHandler = null,
        UnityClientOptions? clientOptions = null)
    {
        return StartCoreAsync(commandHandler == null ? null : (_, cmd) => Task.FromResult(commandHandler(cmd)), clientOptions);
    }

    public static Task<MockUnityServer> StartAsync(
        Func<MockUnityServer, string, string?> commandHandler,
        UnityClientOptions? clientOptions = null)
    {
        return StartCoreAsync((srv, cmd) => Task.FromResult(commandHandler(srv, cmd)), clientOptions);
    }

    public static Task<MockUnityServer> StartAsync(
        Func<MockUnityServer, string, Task<string?>> commandHandler,
        UnityClientOptions? clientOptions = null)
    {
        return StartCoreAsync(commandHandler, clientOptions);
    }

    private static Task<MockUnityServer> StartCoreAsync(
        Func<MockUnityServer, string, Task<string?>>? asyncHandler,
        UnityClientOptions? clientOptions = null)
    {
        string projectRoot = Path.Combine(Path.GetTempPath(), "unity_mock_srv_" + Guid.NewGuid().ToString("N"));
        string unityTemp = Path.Combine(projectRoot, "Temp");
        Directory.CreateDirectory(unityTemp);
        Directory.CreateDirectory(Path.Combine(projectRoot, "Assets"));
        Directory.CreateDirectory(Path.Combine(projectRoot, "ProjectSettings"));

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        File.WriteAllText(Path.Combine(unityTemp, "unity_lean_mcp_port.txt"), port.ToString());
        TestProcessProvider.WriteTrustedPidFile(projectRoot);

        var procManager = new UnityProcessManager(projectRoot, NullLogger<UnityProcessManager>.Instance)
            .WithTrustedTestProcessProvider();

        var client = new UnityClient(
            procManager,
            NullLogger<UnityClient>.Instance,
            clientOptions ?? new UnityClientOptions(PollIntervalMs: 50));

        var cts = new CancellationTokenSource();
        var server = new MockUnityServer(projectRoot, listener, port, procManager, client, cts, asyncHandler);
        return Task.FromResult(server);
    }

    public UnityClient CreateClient(UnityClientOptions? options = null)
    {
        return new UnityClient(
            ProcessManager,
            NullLogger<UnityClient>.Instance,
            options ?? new UnityClientOptions(PollIntervalMs: 50));
    }

    private async Task RunAcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient tcp;
            try
            {
                tcp = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                break;
            }

            Task connectionTask = HandleConnectionAsync(tcp, cancellationToken);
            _activeConnections.TryAdd(connectionTask, 0);
            _ = connectionTask.ContinueWith(t => _activeConnections.TryRemove(t, out _));
        }
    }

    private async Task HandleConnectionAsync(TcpClient tcp, CancellationToken cancellationToken)
    {
        using (tcp)
        using (var stream = tcp.GetStream())
        using (var reader = new StreamReader(stream, Encoding.UTF8))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true })
        {
            try
            {
                string? line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line == null) return;

                string? response = null;
                if (_asyncHandler != null)
                {
                    response = await _asyncHandler(this, line).ConfigureAwait(false);
                }

                if (response == null)
                {
                    response = HandleDefaultCommand(line);
                }

                if (response != null)
                {
                    await writer.WriteLineAsync(response.AsMemory(), cancellationToken).ConfigureAwait(false);
                }
            }
            catch
            {
                // Disconnection or cancellation
            }
        }
    }

    public string? HandleDefaultCommand(string line)
    {
        if (line == "PING")
        {
            return "PONG";
        }

        if (line.StartsWith("REFRESH", StringComparison.OrdinalIgnoreCase))
        {
            return "REFRESHING";
        }

        if (line.StartsWith("RECOMPILE", StringComparison.OrdinalIgnoreCase))
        {
            return "RECOMPILING";
        }

        if (line.StartsWith("POLL_REFRESH", StringComparison.OrdinalIgnoreCase))
        {
            if (TestProcessProvider.TryGetRefreshOperationId(line, out string operationId))
            {
                string path = PathResolver.GetResultFilePath(UnityOperationKind.Refresh, operationId);
                if (!File.Exists(path))
                {
                    WriteRefreshResult(operationId);
                }
            }
            return "READY";
        }

        if (line.StartsWith("POLL_EVAL", StringComparison.OrdinalIgnoreCase))
        {
            return "READY";
        }

        return null;
    }

    public void WriteRefreshResult(string operationId, bool success = true, string message = "Refresh completed")
    {
        var result = new UnityRefreshResult
        {
            OperationId = operationId,
            Success = success,
            Message = message
        };
        string path = PathResolver.GetResultFilePath(UnityOperationKind.Refresh, operationId);
        File.WriteAllText(path, JsonSerializer.Serialize(result));
    }

    public void WriteEvalResult(string operationId, string payload, bool success = true, string message = "Evaluation completed")
    {
        var result = new UnityEvalResult
        {
            OperationId = operationId,
            Success = success,
            Message = message,
            Payload = payload
        };
        string path = PathResolver.GetResultFilePath(UnityOperationKind.Eval, operationId);
        File.WriteAllText(path, JsonSerializer.Serialize(result));
    }

    public void WriteTestResult(string operationId, UnityTestRunResult result)
    {
        string path = PathResolver.GetResultFilePath(UnityOperationKind.Test, operationId);
        File.WriteAllText(path, JsonSerializer.Serialize(result));
    }

    public void WriteTestRunning(UnityTestRunState state)
    {
        File.WriteAllText(PathResolver.TestRunningFile, JsonSerializer.Serialize(state));
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _cts.Cancel();
        _listener.Stop();

        try
        {
            await _serverTask.ConfigureAwait(false);
        }
        catch
        {
            // Expected on cancellation
        }

        try
        {
            await Task.WhenAll(_activeConnections.Keys).ConfigureAwait(false);
        }
        catch
        {
            // Expected
        }

        _cts.Dispose();
        _listener.Dispose();

        DeleteProjectDir();
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private void DeleteProjectDir()
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Directory.Exists(ProjectRoot))
                {
                    Directory.Delete(ProjectRoot, recursive: true);
                }
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == 4) break;
                Thread.Sleep(20);
            }
        }
    }
}
