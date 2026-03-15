using System.Collections.Concurrent;
using System.IO.Ports;
using System.Net;
using System.Net.Sockets;
using System.Text;
using LuminChatWin.Core.Models;

namespace LuminChatWin.Core.Services;

public sealed class TerminalSessionManager : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, SessionState> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<TerminalFeatureConfig> _configAccessor;
    private readonly ConcurrentDictionary<string, SerialBridgeState> _bridges = new(StringComparer.OrdinalIgnoreCase);

    public TerminalSessionManager(Func<TerminalFeatureConfig> configAccessor)
    {
        _configAccessor = configAccessor;
    }

    private TerminalFeatureConfig Config => _configAccessor();

    public event EventHandler<TerminalOutputEventArgs>? OutputReceived;

    public IReadOnlyList<TerminalSessionInfo> ListSessions()
    {
        return _sessions.Values
            .OrderByDescending(item => item.Info.LastActivityAt)
            .Select(item => CloneInfo(item.Info))
            .ToList();
    }

    public IReadOnlyList<TerminalSessionInfo> ListApiSharedSessions()
    {
        return _sessions.Values
            .Where(item => item.Info.IsApiShared)
            .OrderByDescending(item => item.Info.LastActivityAt)
            .Select(item => CloneInfo(item.Info))
            .ToList();
    }

    public TerminalSessionInfo? GetSession(string sessionId)
    {
        return _sessions.TryGetValue(sessionId, out var session) ? CloneInfo(session.Info) : null;
    }

    public TerminalSessionInfo GetApiSharedSession(string sessionId)
    {
        var session = GetRequiredSession(sessionId);
        if (!session.Info.IsApiShared)
        {
            throw new UnauthorizedAccessException($"Session is not shared through API: {sessionId}");
        }

        return CloneInfo(session.Info);
    }

    public IReadOnlyList<TerminalHistoryEntry> GetHistory(string sessionId, int maxEntries = 500)
    {
        var session = GetRequiredSession(sessionId);
        lock (session.SyncRoot)
        {
            return session.History.TakeLast(Math.Max(1, maxEntries)).ToList();
        }
    }

    public string GetRecentOutput(string sessionId, int maxChars = 16000)
    {
        var session = GetRequiredSession(sessionId);
        lock (session.SyncRoot)
        {
            var text = string.Concat(session.RecentOutputLines);
            if (text.Length <= maxChars)
            {
                return text;
            }

            return text[^maxChars..];
        }
    }

    public ActiveTerminalCommandSnapshot GetCurrentCommandOutput(string sessionId)
    {
        var session = GetRequiredSession(sessionId);
        lock (session.SyncRoot)
        {
            return new ActiveTerminalCommandSnapshot
            {
                IsRunning = session.ActiveCommandBuffer is not null,
                Command = session.ActiveCommandText,
                StartedAt = session.ActiveCommandStartedAt,
                Output = session.ActiveCommandBuffer?.ToString() ?? string.Empty,
            };
        }
    }

    public async Task<TerminalSessionInfo> CreatePowerShellSessionAsync(TerminalPowerShellOptions options, CancellationToken cancellationToken = default)
    {
        var backend = new PowerShellTerminalBackend(options.Program, options.Arguments, options.WorkingDirectory);
        return await AddSessionAsync(options.Title, TerminalSessionKind.PowerShell, options.WorkingDirectory, false, options.ApiShared, options.SshShared, backend, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TerminalSessionInfo> CreateSshSessionAsync(TerminalSshOptions options, CancellationToken cancellationToken = default)
    {
        var backend = new SshTerminalBackend(options.Host, options.Port, options.Username, options.Password);
        return await AddSessionAsync(options.Title, TerminalSessionKind.Ssh, $"{options.Username}@{options.Host}:{options.Port}", false, options.ApiShared, options.SshShared, backend, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TerminalSessionInfo> CreateTelnetSessionAsync(TerminalTelnetOptions options, CancellationToken cancellationToken = default)
    {
        var backend = new TelnetTerminalBackend(options.Host, options.Port);
        return await AddSessionAsync(options.Title, TerminalSessionKind.Telnet, $"{options.Host}:{options.Port}", false, options.ApiShared, options.SshShared, backend, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TerminalSessionInfo> CreateSerialSessionAsync(TerminalSerialOptions options, CancellationToken cancellationToken = default)
    {
        var parity = Enum.TryParse<Parity>(options.Parity, true, out var parsedParity) ? parsedParity : Parity.None;
        var stopBits = Enum.TryParse<StopBits>(options.StopBits, true, out var parsedStopBits) ? parsedStopBits : StopBits.One;
        var backend = new SerialTerminalBackend(options.PortName, options.BaudRate, parity, options.DataBits, stopBits, options.NewLine);
        return await AddSessionAsync(options.Title, TerminalSessionKind.Serial, $"{options.PortName} @ {options.BaudRate}", true, options.ApiShared, options.SshShared, backend, cancellationToken).ConfigureAwait(false);
    }

    public async Task SendInputAsync(string sessionId, string text, CancellationToken cancellationToken = default)
    {
        var session = GetRequiredSession(sessionId);
        await session.Backend.SendAsync(text, cancellationToken).ConfigureAwait(false);
        AppendHistory(session, TerminalHistoryEntryKind.Command, text.TrimEnd('\r', '\n'));
    }

    public async Task<TerminalCommandResult> ExecuteCommandAsync(string sessionId, string command, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var session = GetRequiredSession(sessionId);
        await session.CommandLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        var startedAt = DateTime.UtcNow;
        try
        {
            AppendHistory(session, TerminalHistoryEntryKind.Command, command);
            lock (session.SyncRoot)
            {
                session.ActiveCommandText = command;
                session.ActiveCommandStartedAt = startedAt.ToString("O");
                session.ActiveCommandBuffer = new StringBuilder();
                session.LastCommandOutputAt = null;
            }

            await session.Backend.SendAsync(command + Environment.NewLine, cancellationToken).ConfigureAwait(false);

            var quietWindow = TimeSpan.FromMilliseconds(700);
            var emptyWait = TimeSpan.FromMilliseconds(900);
            var timedOut = false;
            while (DateTime.UtcNow - startedAt < timeout)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(150, cancellationToken).ConfigureAwait(false);
                DateTime? lastOutputAt;
                var bufferLength = 0;
                lock (session.SyncRoot)
                {
                    lastOutputAt = session.LastCommandOutputAt;
                    bufferLength = session.ActiveCommandBuffer?.Length ?? 0;
                }

                if (bufferLength == 0)
                {
                    if (DateTime.UtcNow - startedAt >= emptyWait)
                    {
                        break;
                    }

                    continue;
                }

                if (lastOutputAt.HasValue && DateTime.UtcNow - lastOutputAt.Value >= quietWindow)
                {
                    break;
                }
            }

            if (DateTime.UtcNow - startedAt >= timeout)
            {
                timedOut = true;
                await session.Backend.SendInterruptAsync(cancellationToken).ConfigureAwait(false);
                AppendHistory(session, TerminalHistoryEntryKind.System, $"Command timed out after {timeout.TotalSeconds:F1}s");
            }

            string output;
            lock (session.SyncRoot)
            {
                output = session.ActiveCommandBuffer?.ToString() ?? string.Empty;
                session.ActiveCommandBuffer = null;
                session.ActiveCommandText = string.Empty;
                session.ActiveCommandStartedAt = string.Empty;
                session.LastCommandOutputAt = null;
            }

            return new TerminalCommandResult
            {
                Success = !timedOut,
                TimedOut = timedOut,
                Command = command,
                Output = output.TrimEnd(),
                StartedAt = startedAt.ToString("O"),
                CompletedAt = DateTime.UtcNow.ToString("O"),
                Error = timedOut ? "Command timed out." : string.Empty,
            };
        }
        finally
        {
            session.CommandLock.Release();
        }
    }

    public async Task StopSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryRemove(sessionId, out var session))
        {
            return;
        }

        if (_bridges.TryRemove(sessionId, out var bridge))
        {
            await bridge.DisposeAsync().ConfigureAwait(false);
        }

        AppendHistory(session, TerminalHistoryEntryKind.System, "Session closed.");
        await session.Backend.StopAsync(cancellationToken).ConfigureAwait(false);
        await session.Backend.DisposeAsync().ConfigureAwait(false);
        session.CommandLock.Dispose();
    }

    public async Task<TerminalBridgeInfo> StartSerialBridgeAsync(string sessionId, int? requestedPort = null, CancellationToken cancellationToken = default)
    {
        var session = GetRequiredSession(sessionId);
        if (session.Info.Kind != TerminalSessionKind.Serial)
        {
            throw new InvalidOperationException("Only serial sessions can be bridged.");
        }

        if (_bridges.TryGetValue(sessionId, out var existing))
        {
            return existing.Info;
        }

        var config = Config;
        var host = config.SerialSshBridge.BindHost;
        var port = requestedPort ?? ResolveBridgePort(session.Info);
        var listener = new TcpListener(IPAddress.Parse(host), port);
        listener.Start();

        var bridgeState = new SerialBridgeState(listener, sessionId, host, port, session);
        if (!_bridges.TryAdd(sessionId, bridgeState))
        {
            listener.Stop();
            return _bridges[sessionId].Info;
        }

        bridgeState.AcceptLoopTask = Task.Run(() => AcceptBridgeLoopAsync(bridgeState, cancellationToken), cancellationToken);
        AppendHistory(session, TerminalHistoryEntryKind.System, $"Serial bridge listening on {host}:{port}");
        return bridgeState.Info;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var sessionId in _sessions.Keys.ToList())
        {
            await StopSessionAsync(sessionId).ConfigureAwait(false);
        }
    }

    private async Task<TerminalSessionInfo> AddSessionAsync(string title, TerminalSessionKind kind, string descriptor, bool supportsBridge, bool apiShared, bool sshShared, ITerminalBackend backend, CancellationToken cancellationToken)
    {
        var info = new TerminalSessionInfo
        {
            Title = string.IsNullOrWhiteSpace(title) ? kind.ToString() : title,
            Kind = kind,
            Descriptor = descriptor,
            IsConnected = false,
            SupportsBridge = supportsBridge,
            IsApiShared = apiShared,
            IsSshShared = sshShared,
        };
        var session = new SessionState(info, backend);
        HookBackend(session);
        await backend.StartAsync(cancellationToken).ConfigureAwait(false);
        session.Info.IsConnected = true;
        AppendHistory(session, TerminalHistoryEntryKind.System, $"Connected: {descriptor}");
        _sessions[info.SessionId] = session;
        return CloneInfo(session.Info);
    }

    private void HookBackend(SessionState session)
    {
        session.Backend.OutputReceived += (_, text) => HandleBackendOutput(session, TerminalHistoryEntryKind.Output, text);
        session.Backend.ErrorReceived += (_, text) => HandleBackendOutput(session, TerminalHistoryEntryKind.Error, text);
        session.Backend.StatusReceived += (_, text) => HandleBackendOutput(session, TerminalHistoryEntryKind.System, text);
    }

    private void HandleBackendOutput(SessionState session, TerminalHistoryEntryKind kind, string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        lock (session.SyncRoot)
        {
            session.Info.LastActivityAt = DateTime.UtcNow.ToString("O");
            session.History.Add(new TerminalHistoryEntry
            {
                Kind = kind,
                Text = text,
            });
            TrimHistory(session.History);
            foreach (var line in SplitLinesPreservingDelimiter(text))
            {
                session.RecentOutputLines.Add(line);
            }
            TrimRecentOutput(session.RecentOutputLines);
            if (session.ActiveCommandBuffer is not null)
            {
                session.ActiveCommandBuffer.Append(text);
                session.LastCommandOutputAt = DateTime.UtcNow;
            }
        }

        OutputReceived?.Invoke(this, new TerminalOutputEventArgs
        {
            SessionId = session.Info.SessionId,
            Kind = kind,
            Text = text,
        });
    }

    private void AppendHistory(SessionState session, TerminalHistoryEntryKind kind, string text)
    {
        HandleBackendOutput(session, kind, text + Environment.NewLine);
    }

    private void TrimHistory(List<TerminalHistoryEntry> history)
    {
        var overflow = history.Count - Math.Max(100, Config.HistoryMaxEntries);
        if (overflow > 0)
        {
            history.RemoveRange(0, overflow);
        }
    }

    private void TrimRecentOutput(List<string> lines)
    {
        var overflow = lines.Count - Math.Max(100, Config.RecentOutputMaxLines);
        if (overflow > 0)
        {
            lines.RemoveRange(0, overflow);
        }
    }

    private SessionState GetRequiredSession(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            throw new KeyNotFoundException($"Unknown terminal session: {sessionId}");
        }

        return session;
    }

    private static TerminalSessionInfo CloneInfo(TerminalSessionInfo source)
    {
        return new TerminalSessionInfo
        {
            SessionId = source.SessionId,
            Title = source.Title,
            Kind = source.Kind,
            Descriptor = source.Descriptor,
            CreatedAt = source.CreatedAt,
            LastActivityAt = source.LastActivityAt,
            IsConnected = source.IsConnected,
            SupportsBridge = source.SupportsBridge,
            IsApiShared = source.IsApiShared,
            IsSshShared = source.IsSshShared,
        };
    }

    private async Task AcceptBridgeLoopAsync(SerialBridgeState state, CancellationToken cancellationToken)
    {
        state.SessionOutputHandler = (_, args) =>
        {
            if (!string.Equals(args.SessionId, state.SessionId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var payload = Encoding.UTF8.GetBytes(args.Text);
            lock (state.Clients)
            {
                foreach (var client in state.Clients.ToList())
                {
                    try
                    {
                        client.GetStream().Write(payload, 0, payload.Length);
                    }
                    catch
                    {
                        client.Dispose();
                        state.Clients.Remove(client);
                    }
                }
            }
        };
        OutputReceived += state.SessionOutputHandler;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await state.Listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                lock (state.Clients)
                {
                    state.Clients.Add(client);
                }

                _ = Task.Run(() => BridgeClientLoopAsync(state, client, cancellationToken), cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            if (state.SessionOutputHandler is not null)
            {
                OutputReceived -= state.SessionOutputHandler;
            }
        }
    }

    private async Task BridgeClientLoopAsync(SerialBridgeState state, TcpClient client, CancellationToken cancellationToken)
    {
        try
        {
            var stream = client.GetStream();
            var buffer = new byte[2048];
            var welcome = Encoding.UTF8.GetBytes($"Serial bridge for session {state.SessionId}. Raw TCP relay preview.\r\n");
            await stream.WriteAsync(welcome, cancellationToken).ConfigureAwait(false);
            while (!cancellationToken.IsCancellationRequested && client.Connected)
            {
                var bytesRead = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (bytesRead <= 0)
                {
                    break;
                }

                var text = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                await state.Session.Backend.SendAsync(text, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
        }
        finally
        {
            lock (state.Clients)
            {
                state.Clients.Remove(client);
            }
            client.Dispose();
        }
    }

    private int ResolveBridgePort(TerminalSessionInfo info)
    {
        var config = Config;
        if (config.SerialSshBridge.PortOverrides.TryGetValue(info.SessionId, out var explicitPort))
        {
            return explicitPort;
        }

        var prefix = int.TryParse(config.SerialSshBridge.PortPrefix, out var parsedPrefix) ? parsedPrefix : 22;
        return int.Parse($"{prefix}{Math.Abs(info.SessionId.GetHashCode()) % 1000:000}");
    }

    private static IEnumerable<string> SplitLinesPreservingDelimiter(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            yield break;
        }

        using var reader = new StringReader(text);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            yield return line + Environment.NewLine;
        }

        if (!text.EndsWith("\n", StringComparison.Ordinal) && !text.EndsWith("\r", StringComparison.Ordinal))
        {
            yield return string.Empty;
        }
    }

    private sealed class SessionState
    {
        public SessionState(TerminalSessionInfo info, ITerminalBackend backend)
        {
            Info = info;
            Backend = backend;
        }

        public object SyncRoot { get; } = new();
        public TerminalSessionInfo Info { get; }
        public ITerminalBackend Backend { get; }
        public List<TerminalHistoryEntry> History { get; } = [];
        public List<string> RecentOutputLines { get; } = [];
        public SemaphoreSlim CommandLock { get; } = new(1, 1);
        public StringBuilder? ActiveCommandBuffer { get; set; }
        public string ActiveCommandText { get; set; } = string.Empty;
        public string ActiveCommandStartedAt { get; set; } = string.Empty;
        public DateTime? LastCommandOutputAt { get; set; }
    }

    private sealed class SerialBridgeState : IAsyncDisposable
    {
        public SerialBridgeState(TcpListener listener, string sessionId, string host, int port, SessionState session)
        {
            Listener = listener;
            SessionId = sessionId;
            Session = session;
            Info = new TerminalBridgeInfo
            {
                SessionId = sessionId,
                Host = host,
                Port = port,
                Protocol = "tcp-raw",
                Message = "Raw TCP relay preview for serial sessions.",
            };
        }

        public TcpListener Listener { get; }
        public string SessionId { get; }
        public SessionState Session { get; }
        public TerminalBridgeInfo Info { get; }
        public List<TcpClient> Clients { get; } = [];
        public Task? AcceptLoopTask { get; set; }
        public EventHandler<TerminalOutputEventArgs>? SessionOutputHandler { get; set; }

        public async ValueTask DisposeAsync()
        {
            Listener.Stop();
            if (AcceptLoopTask is not null)
            {
                try
                {
                    await AcceptLoopTask.ConfigureAwait(false);
                }
                catch
                {
                }
            }

            lock (Clients)
            {
                foreach (var client in Clients)
                {
                    client.Dispose();
                }
                Clients.Clear();
            }
        }
    }
}