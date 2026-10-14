using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Demo.DotnetCore.ConfigService.Client.Tests.Infrastructure;

internal sealed record LoopbackResponse(
    HttpStatusCode StatusCode,
    string Body = "",
    IReadOnlyDictionary<string, string>? Headers = null);

internal sealed class ScriptedLoopbackServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Queue<LoopbackResponse> _responses;
    private readonly List<string> _requests = [];
    private readonly Task _serveTask;

    internal ScriptedLoopbackServer(params LoopbackResponse[] responses)
    {
        _responses = new Queue<LoopbackResponse>(responses);
        _listener.Start();
        int port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        BaseAddress = new Uri($"http://127.0.0.1:{port}/");
        _serveTask = ServeAsync();
    }

    internal Uri BaseAddress { get; }

    internal IReadOnlyList<string> Requests
    {
        get
        {
            lock (_requests)
            {
                return _requests.ToArray();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        _listener.Stop();
        await _serveTask;
        _shutdown.Dispose();
    }

    private async Task ServeAsync()
    {
        try
        {
            while (_responses.TryDequeue(out LoopbackResponse? response))
            {
                using TcpClient client = await _listener.AcceptTcpClientAsync(_shutdown.Token);
                await using NetworkStream stream = client.GetStream();
                string request = await ReadRequestAsync(stream, _shutdown.Token);
                lock (_requests)
                {
                    _requests.Add(request);
                }

                await WriteResponseAsync(stream, response, _shutdown.Token);
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (SocketException) when (_shutdown.IsCancellationRequested)
        {
        }
    }

    private static async Task<string> ReadRequestAsync(
        NetworkStream stream,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[4096];
        int headerEnd = -1;
        int contentLength = 0;

        while (headerEnd < 0 || buffer.Length < headerEnd + 4L + contentLength)
        {
            int read = await stream.ReadAsync(chunk, cancellationToken);
            if (read == 0)
            {
                break;
            }

            buffer.Write(chunk, 0, read);
            if (headerEnd < 0)
            {
                string received = Encoding.ASCII.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
                headerEnd = received.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (headerEnd >= 0)
                {
                    string? contentLengthHeader = received[..headerEnd]
                        .Split("\r\n", StringSplitOptions.None)
                        .FirstOrDefault(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
                    if (contentLengthHeader is not null)
                    {
                        contentLength = int.Parse(
                            contentLengthHeader["Content-Length:".Length..].Trim(),
                            System.Globalization.CultureInfo.InvariantCulture);
                    }
                }
            }
        }

        return Encoding.ASCII.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
    }

    private static async Task WriteResponseAsync(
        NetworkStream stream,
        LoopbackResponse response,
        CancellationToken cancellationToken)
    {
        byte[] body = Encoding.UTF8.GetBytes(response.Body);
        var header = new StringBuilder()
            .Append("HTTP/1.1 ")
            .Append((int)response.StatusCode)
            .Append(' ')
            .Append(response.StatusCode)
            .Append("\r\nContent-Length: ")
            .Append(body.Length)
            .Append("\r\nConnection: close\r\n");
        if (response.Headers is not null)
        {
            foreach ((string name, string value) in response.Headers)
            {
                header.Append(name).Append(": ").Append(value).Append("\r\n");
            }
        }

        header.Append("\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(header.ToString()), cancellationToken);
        await stream.WriteAsync(body, cancellationToken);
    }
}
