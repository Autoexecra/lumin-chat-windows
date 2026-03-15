using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using LuminChatWin.Core.Models;

namespace LuminChatWin.Core.Services;

public sealed class TerminalApiServer : IAsyncDisposable
{
    private readonly TerminalSessionManager _sessionManager;
    private readonly Func<TerminalFeatureConfig> _configAccessor;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    public TerminalApiServer(TerminalSessionManager sessionManager, Func<TerminalFeatureConfig> configAccessor)
    {
        _sessionManager = sessionManager;
        _configAccessor = configAccessor;
    }

    private TerminalFeatureConfig Config => _configAccessor();

    public bool IsRunning => _listener is not null;

    public string BaseUrl => $"http://{Config.ExecApi.BindHost}:{Config.ExecApi.Port}/";

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (IsRunning)
        {
            return Task.CompletedTask;
        }

        _listener = new TcpListener(IPAddress.Parse(Config.ExecApi.BindHost), Config.ExecApi.Port);
        _listener.Start();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loopTask = Task.Run(() => AcceptLoopAsync(_cts.Token), _cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (_cts is not null && !_cts.IsCancellationRequested)
        {
            _cts.Cancel();
        }

        _listener?.Stop();
        if (_loopTask is not null)
        {
            try
            {
                await _loopTask.ConfigureAwait(false);
            }
            catch
            {
            }
        }

        _listener = null;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _cts?.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _listener is not null)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            _ = Task.Run(() => HandleClientAsync(client, cancellationToken), cancellationToken);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        NetworkStream? stream = null;
        try
        {
            stream = client.GetStream();
            var requestBytes = await ReadRequestAsync(stream, cancellationToken).ConfigureAwait(false);
            if (requestBytes.Length == 0)
            {
                return;
            }

            var headerEnd = FindHeaderEnd(requestBytes);
            if (headerEnd < 0)
            {
                await WriteResponseAsync(stream, 400, new { error = "Invalid HTTP request" }, cancellationToken).ConfigureAwait(false);
                return;
            }

            var headerText = Encoding.ASCII.GetString(requestBytes, 0, headerEnd);
            var headerLines = headerText.Split(["\r\n"], StringSplitOptions.None);
            var requestLine = headerLines[0];
            var contentLength = ParseContentLength(headerLines);
            var bodyStart = headerEnd + 4;
            var isChunked = IsChunked(headerLines);
            var body = isChunked
                ? Encoding.UTF8.GetString(DecodeChunkedBody(requestBytes, bodyStart))
                : (Math.Max(0, requestBytes.Length - bodyStart) > 0
                    ? Encoding.UTF8.GetString(requestBytes, bodyStart, Math.Min(contentLength, Math.Max(0, requestBytes.Length - bodyStart)))
                    : string.Empty);

            var response = await RouteAsync(requestLine, body, cancellationToken).ConfigureAwait(false);
            await WriteResponseAsync(stream, response.statusCode, response.payload, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            try
            {
                if (stream is not null)
                {
                    await WriteResponseAsync(stream, 500, new { error = ex.Message }, cancellationToken).ConfigureAwait(false);
                }
            }
            catch
            {
            }
        }
        finally
        {
            client.Dispose();
        }
    }

    private static async Task<byte[]> ReadRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        using var bufferStream = new MemoryStream();
        var buffer = new byte[4096];
        var headerEnd = -1;
        var expectedBodyLength = 0;

        while (true)
        {
            var bytesRead = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (bytesRead <= 0)
            {
                break;
            }

            bufferStream.Write(buffer, 0, bytesRead);
            var data = bufferStream.ToArray();
            if (headerEnd < 0)
            {
                headerEnd = FindHeaderEnd(data);
                if (headerEnd >= 0)
                {
                    var headerText = Encoding.ASCII.GetString(data, 0, headerEnd);
                    var headerLines = headerText.Split(["\r\n"], StringSplitOptions.None);
                    expectedBodyLength = ParseContentLength(headerLines);
                    if (IsChunked(headerLines))
                    {
                        expectedBodyLength = -1;
                    }
                }
            }

            if (headerEnd >= 0)
            {
                var currentBodyLength = data.Length - (headerEnd + 4);
                if (expectedBodyLength >= 0 && currentBodyLength >= expectedBodyLength)
                {
                    return data;
                }

                if (expectedBodyLength < 0 && FindChunkedTerminator(data, headerEnd + 4) >= 0)
                {
                    return data;
                }
            }
        }

        return bufferStream.ToArray();
    }

    private static int FindHeaderEnd(byte[] data)
    {
        for (var index = 0; index <= data.Length - 4; index++)
        {
            if (data[index] == '\r' && data[index + 1] == '\n' && data[index + 2] == '\r' && data[index + 3] == '\n')
            {
                return index;
            }
        }

        return -1;
    }

    private static int ParseContentLength(IEnumerable<string> headerLines)
    {
        foreach (var line in headerLines)
        {
            if (!line.StartsWith("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var separator = line.IndexOf(':');
            if (separator <= 0)
            {
                continue;
            }

            if (int.TryParse(line[(separator + 1)..].Trim(), out var length))
            {
                return Math.Max(0, length);
            }
        }

        return 0;
    }

    private static bool IsChunked(IEnumerable<string> headerLines)
    {
        return headerLines.Any(line => line.StartsWith("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) && line.Contains("chunked", StringComparison.OrdinalIgnoreCase));
    }

    private static int FindChunkedTerminator(byte[] data, int startIndex)
    {
        var terminator = Encoding.ASCII.GetBytes("\r\n0\r\n\r\n");
        for (var index = startIndex; index <= data.Length - terminator.Length; index++)
        {
            var matched = true;
            for (var offset = 0; offset < terminator.Length; offset++)
            {
                if (data[index + offset] != terminator[offset])
                {
                    matched = false;
                    break;
                }
            }

            if (matched)
            {
                return index;
            }
        }

        return -1;
    }

    private static byte[] DecodeChunkedBody(byte[] data, int bodyStart)
    {
        using var output = new MemoryStream();
        var index = bodyStart;
        while (index < data.Length)
        {
            var lineEnd = IndexOfAscii(data, index, "\r\n");
            if (lineEnd < 0)
            {
                break;
            }

            var lengthText = Encoding.ASCII.GetString(data, index, lineEnd - index).Trim();
            if (!int.TryParse(lengthText, System.Globalization.NumberStyles.HexNumber, null, out var chunkLength))
            {
                break;
            }

            index = lineEnd + 2;
            if (chunkLength == 0)
            {
                break;
            }

            output.Write(data, index, chunkLength);
            index += chunkLength + 2;
        }

        return output.ToArray();
    }

    private static int IndexOfAscii(byte[] data, int startIndex, string token)
    {
        var bytes = Encoding.ASCII.GetBytes(token);
        for (var index = startIndex; index <= data.Length - bytes.Length; index++)
        {
            var matched = true;
            for (var offset = 0; offset < bytes.Length; offset++)
            {
                if (data[index + offset] != bytes[offset])
                {
                    matched = false;
                    break;
                }
            }

            if (matched)
            {
                return index;
            }
        }

        return -1;
    }

    private async Task<(int statusCode, object payload)> RouteAsync(string requestLine, string body, CancellationToken cancellationToken)
    {
        var parts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return (400, new { error = "Invalid request line" });
        }

        var method = parts[0].Trim();
        var path = parts[1].Split('?', 2)[0].TrimEnd('/');

        if (string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase) && string.Equals(path, "/api/sessions", StringComparison.OrdinalIgnoreCase))
        {
            return (200, new { sessions = _sessionManager.ListApiSharedSessions() });
        }

        if (string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase) && path.StartsWith("/api/sessions/", StringComparison.OrdinalIgnoreCase))
        {
            var remainder = path["/api/sessions/".Length..];
            var routeParts = remainder.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (routeParts.Length == 2 && string.Equals(routeParts[1], "history", StringComparison.OrdinalIgnoreCase))
            {
                _sessionManager.GetApiSharedSession(routeParts[0]);
                return (200, new { sessionId = routeParts[0], entries = _sessionManager.GetHistory(routeParts[0]) });
            }

            if (routeParts.Length == 2 && string.Equals(routeParts[1], "current-output", StringComparison.OrdinalIgnoreCase))
            {
                _sessionManager.GetApiSharedSession(routeParts[0]);
                return (200, _sessionManager.GetCurrentCommandOutput(routeParts[0]));
            }
        }

        if (string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase) && string.Equals(path, "/api/exec_cmd", StringComparison.OrdinalIgnoreCase))
        {
            var payload = JsonSerializer.Deserialize<ExecCommandRequest>(body, _jsonOptions) ?? new ExecCommandRequest();
            _sessionManager.GetApiSharedSession(payload.SessionId);
            var timeout = TimeSpan.FromSeconds(payload.TimeoutSeconds <= 0 ? Config.ExecApi.DefaultTimeoutSeconds : payload.TimeoutSeconds);
            var result = await _sessionManager.ExecuteCommandAsync(payload.SessionId, payload.Command, timeout, cancellationToken).ConfigureAwait(false);
            return (200, result);
        }

        if (string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase) && string.Equals(path, "/api/send_input", StringComparison.OrdinalIgnoreCase))
        {
            var payload = JsonSerializer.Deserialize<SendInputRequest>(body, _jsonOptions) ?? new SendInputRequest();
            _sessionManager.GetApiSharedSession(payload.SessionId);
            await _sessionManager.SendInputAsync(payload.SessionId, payload.Text, cancellationToken).ConfigureAwait(false);
            return (200, new { ok = true });
        }

        if (string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase) && string.Equals(path, "/api/bridge/open", StringComparison.OrdinalIgnoreCase))
        {
            var payload = JsonSerializer.Deserialize<BridgeRequest>(body, _jsonOptions) ?? new BridgeRequest();
            _sessionManager.GetApiSharedSession(payload.SessionId);
            var result = await _sessionManager.StartSerialBridgeAsync(payload.SessionId, payload.Port, cancellationToken).ConfigureAwait(false);
            return (200, result);
        }

        return (404, new { error = "Not found" });
    }

    private async Task WriteResponseAsync(NetworkStream stream, int statusCode, object payload, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(payload, _jsonOptions);
        var bodyBytes = Encoding.UTF8.GetBytes(json);
        var header = new StringBuilder()
            .Append($"HTTP/1.1 {statusCode} {GetReasonPhrase(statusCode)}\r\n")
            .Append("Content-Type: application/json; charset=utf-8\r\n")
            .Append($"Content-Length: {bodyBytes.Length}\r\n")
            .Append("Connection: close\r\n\r\n")
            .ToString();
        var headerBytes = Encoding.ASCII.GetBytes(header);
        await stream.WriteAsync(headerBytes, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(bodyBytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string GetReasonPhrase(int statusCode)
    {
        return statusCode switch
        {
            200 => "OK",
            400 => "Bad Request",
            404 => "Not Found",
            500 => "Internal Server Error",
            _ => "OK",
        };
    }

    private sealed class ExecCommandRequest
    {
        public string SessionId { get; set; } = string.Empty;
        public string Command { get; set; } = string.Empty;
        public double TimeoutSeconds { get; set; }
    }

    private sealed class SendInputRequest
    {
        public string SessionId { get; set; } = string.Empty;
        public string Text { get; set; } = string.Empty;
    }

    private sealed class BridgeRequest
    {
        public string SessionId { get; set; } = string.Empty;
        public int? Port { get; set; }
    }
}