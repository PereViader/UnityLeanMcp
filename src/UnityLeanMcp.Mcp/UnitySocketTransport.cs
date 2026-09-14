using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace UnityLeanMcp.Mcp;

public interface IUnitySocketTransport
{
    Task<string?> SendCommandAsync(int port, string command, int timeoutSeconds = 10, CancellationToken cancellationToken = default);
    Task<bool> IsSocketReadyAsync(int port, int timeoutSeconds = 2, CancellationToken cancellationToken = default);
}

public class UnitySocketTransport : IUnitySocketTransport
{
    private readonly ILogger? _logger;

    public UnitySocketTransport(ILogger? logger = null)
    {
        _logger = logger;
    }

    public virtual async Task<string?> SendCommandAsync(int port, string command, int timeoutSeconds = 10, CancellationToken cancellationToken = default)
    {
        if (port <= 0 || port > 65535)
        {
            return null;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port, cts.Token);
            client.ReceiveTimeout = timeoutSeconds * 1000;
            client.SendTimeout = timeoutSeconds * 1000;

            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };

            await writer.WriteLineAsync(command.AsMemory(), cts.Token);
            string? line = await reader.ReadLineAsync(cts.Token);
            return line?.Trim();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogTrace(ex, "Socket command failed on port {Port}: {Command}", port, command);
            return null;
        }
    }

    public virtual async Task<bool> IsSocketReadyAsync(int port, int timeoutSeconds = 2, CancellationToken cancellationToken = default)
    {
        string? response = await SendCommandAsync(port, "PING", timeoutSeconds, cancellationToken);
        return response == "PONG";
    }
}
