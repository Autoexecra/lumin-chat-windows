using System.Collections.Concurrent;
using System.IO.Ports;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using LuminChatWin.Core.Models;

namespace LuminChatWin.Core.Services;

public sealed class TerminalSessionManager : IAsyncDisposable
{
    private static readonly Regex AnsiControlSequenceRegex = new("\\x1B\\[[0-?]*[ -/]*[@-~]", RegexOptions.Compiled);
    private static readonly Regex AnsiOperatingSystemCommandRegex = new("\\x1B\\][^\\a]*(\\a|\\x1B\\\\)", RegexOptions.Compiled);
    private static readonly Regex DescriptorNumberRegex = new("(\\d+)", RegexOptions.Compiled);
    private const string DefaultLoginUsername = "root";
    private const string DefaultLoginPassword = "Ncti2023";
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
            var text = session.RenderedOutput.ToString();
            if (text.Length <= maxChars)
            {
                return text;
            }

            return text[^maxChars..];
        }
    }

    public string GetRecentRawOutput(string sessionId, int maxChars = 16000)
    {
        var session = GetRequiredSession(sessionId);
        lock (session.SyncRoot)
        {
            var text = session.RawOutput.ToString();
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

    public async Task SendInputAsync(string sessionId, string text, CancellationToken cancellationToken = default, bool recordInHistory = true)
    {
        var session = GetRequiredSession(sessionId);
        await session.Backend.SendAsync(text, cancellationToken).ConfigureAwait(false);
        if (recordInHistory)
        {
            AppendHistory(session, TerminalHistoryEntryKind.Command, text.TrimEnd('\r', '\n'));
        }
    }

    public async Task<TerminalCommandResult> ExecuteCommandAsync(string sessionId, string command, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var session = GetRequiredSession(sessionId);
        await session.CommandLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        var startedAt = DateTime.UtcNow;
        try
        {
            var readiness = await EnsureSessionReadyForCommandAsync(session, timeout, cancellationToken).ConfigureAwait(false);
            if (!readiness.Success)
            {
                return new TerminalCommandResult
                {
                    Success = false,
                    TimedOut = false,
                    Command = command,
                    Output = readiness.Output.TrimEnd(),
                    StartedAt = startedAt.ToString("O"),
                    CompletedAt = DateTime.UtcNow.ToString("O"),
                    Error = readiness.Error,
                };
            }

            AppendHistory(session, TerminalHistoryEntryKind.Command, command);
            BeginCommandCapture(session, command, startedAt);

            await session.Backend.SendAsync(command + Environment.NewLine, cancellationToken).ConfigureAwait(false);

            var timedOut = await WaitForCommandCompletionAsync(session, readiness.PromptMarker, timeout, cancellationToken).ConfigureAwait(false);

            if (timedOut)
            {
                await session.Backend.SendInterruptAsync(cancellationToken).ConfigureAwait(false);
                AppendHistory(session, TerminalHistoryEntryKind.System, $"Command timed out after {timeout.TotalSeconds:F1}s; sent Ctrl+C.");

                // Give the remote shell a short chance to surface the prompt again after Ctrl+C.
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
                await session.Backend.SendAsync(Environment.NewLine, cancellationToken).ConfigureAwait(false);
                await WaitForPromptProbeAsync(session, readiness.PromptMarker, TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            }

            var output = EndCommandCapture(session);

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
            ResetActiveCapture(session);
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
            if (bridge.SessionOutputHandler is not null)
            {
                OutputReceived -= bridge.SessionOutputHandler;
            }

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
        if (!IPAddress.TryParse(host, out var bindAddress))
        {
            throw new InvalidOperationException($"Invalid serial bridge host: {host}");
        }

        var port = requestedPort ?? ResolveBridgePort(session.Info);
        var bridgeServer = new SerialSshBridgeServer(
            sessionId,
            session.Info.Title,
            bindAddress,
            port,
            config.SerialSshBridge,
            (text, token) => session.Backend.SendAsync(text, token),
            () => GetRecentRawOutput(sessionId, 16000),
            (commandText, timeout, token) => ExecuteCommandAsync(sessionId, commandText, timeout, token));

        var bridgeState = new SerialBridgeState(bridgeServer, sessionId, session);
        if (!_bridges.TryAdd(sessionId, bridgeState))
        {
            await bridgeServer.DisposeAsync().ConfigureAwait(false);
            return _bridges[sessionId].Info;
        }

        bridgeState.SessionOutputHandler = (_, args) => bridgeState.Bridge.HandleTerminalOutput(args);
        OutputReceived += bridgeState.SessionOutputHandler;
        bridgeServer.ExceptionRaised += (_, ex) => AppendHistory(session, TerminalHistoryEntryKind.System, $"Serial SSH bridge error: {ex.Message}");

        try
        {
            bridgeServer.Start();
            AppendHistory(session, TerminalHistoryEntryKind.System, $"Serial SSH bridge listening on {host}:{port} for {config.SerialSshBridge.Username}");
            return bridgeState.Info;
        }
        catch
        {
            OutputReceived -= bridgeState.SessionOutputHandler;
            _bridges.TryRemove(sessionId, out _);
            await bridgeState.DisposeAsync().ConfigureAwait(false);
            throw;
        }
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

        var normalizedText = kind is TerminalHistoryEntryKind.Output or TerminalHistoryEntryKind.Error
            ? NormalizeCommandOutput(text)
            : NormalizeNonTerminalText(text);

        lock (session.SyncRoot)
        {
            if (kind is TerminalHistoryEntryKind.Output or TerminalHistoryEntryKind.Error)
            {
                session.RawOutput.Append(text);
                ApplyTerminalChunk(session, text);
                TrimRecentOutput(session);
            }
            else
            {
                session.RenderedOutput.Append(normalizedText);
                var lastNewline = session.RenderedOutput.ToString().LastIndexOf('\n');
                session.CurrentLineStartIndex = lastNewline >= 0 ? lastNewline + 1 : 0;
                TrimRecentOutput(session);
            }

            session.Info.LastActivityAt = DateTime.UtcNow.ToString("O");
            session.History.Add(new TerminalHistoryEntry
            {
                Kind = kind,
                Text = normalizedText,
            });
            TrimHistory(session.History);
            if (session.ActiveCommandBuffer is not null && kind is TerminalHistoryEntryKind.Output or TerminalHistoryEntryKind.Error)
            {
                session.ActiveCommandBuffer.Append(NormalizeCommandOutput(text));
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

    private void TrimRecentOutput(SessionState session)
    {
        var maxChars = Math.Max(16000, Math.Max(100, Config.RecentOutputMaxLines) * 256);
        var overflow = session.RenderedOutput.Length - maxChars;
        if (overflow > 0)
        {
            session.RenderedOutput.Remove(0, overflow);
            session.CurrentLineStartIndex = Math.Max(0, session.CurrentLineStartIndex - overflow);
            var lastNewline = session.RenderedOutput.ToString().LastIndexOf('\n');
            session.CurrentLineStartIndex = lastNewline >= 0 ? lastNewline + 1 : 0;
        }

        var rawOverflow = session.RawOutput.Length - maxChars * 2;
        if (rawOverflow > 0)
        {
            session.RawOutput.Remove(0, rawOverflow);
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

    private int ResolveBridgePort(TerminalSessionInfo info)
    {
        var config = Config;
        if (config.SerialSshBridge.PortOverrides.TryGetValue(info.SessionId, out var explicitPort))
        {
            return explicitPort;
        }

        var descriptorKey = ResolveBridgeOverrideKey(info.Descriptor);
        if (!string.IsNullOrWhiteSpace(descriptorKey) && config.SerialSshBridge.PortOverrides.TryGetValue(descriptorKey, out explicitPort))
        {
            return explicitPort;
        }

        return BuildDefaultBridgePort(config.SerialSshBridge.PortPrefix, info.Descriptor, info.SessionId);
    }

    internal static string ResolveBridgeOverrideKey(string descriptor)
    {
        if (string.IsNullOrWhiteSpace(descriptor))
        {
            return string.Empty;
        }

        return descriptor.Split('@', 2, StringSplitOptions.TrimEntries)[0].Trim();
    }

    internal static int BuildDefaultBridgePort(string portPrefix, string descriptor, string sessionId)
    {
        var prefix = int.TryParse(portPrefix, out var parsedPrefix) ? parsedPrefix : 22;
        var numericSuffix = ResolveDescriptorPortSuffix(descriptor);
        if (numericSuffix >= 0)
        {
            return int.Parse($"{prefix}{numericSuffix:00}");
        }

        return int.Parse($"{prefix}{Math.Abs(sessionId.GetHashCode()) % 100:00}");
    }

    internal static int ResolveDescriptorPortSuffix(string descriptor)
    {
        if (string.IsNullOrWhiteSpace(descriptor))
        {
            return -1;
        }

        var descriptorHead = descriptor.Split('@', 2, StringSplitOptions.TrimEntries)[0];
        var matches = DescriptorNumberRegex.Matches(descriptorHead);
        if (matches.Count == 0)
        {
            return -1;
        }

        var digits = matches[^1].Value;
        if (!int.TryParse(digits, out var numericValue))
        {
            return -1;
        }

        return Math.Abs(numericValue % 100);
    }

    internal static string RenderTerminalPreview(params string[] chunks)
    {
        var buffer = new StringBuilder();
        var currentLineStartIndex = 0;
        var pendingCarriageReturn = false;
        foreach (var chunk in chunks)
        {
            ApplyTerminalChunk(buffer, ref currentLineStartIndex, ref pendingCarriageReturn, chunk);
        }

        return buffer.ToString();
    }

    internal static bool IsLoginPrompt(string text)
    {
        var lastLine = GetLastMeaningfulLine(text);
        return lastLine.EndsWith("login:", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsPasswordPrompt(string text)
    {
        var lastLine = GetLastMeaningfulLine(text);
        return lastLine.EndsWith("password:", StringComparison.OrdinalIgnoreCase);
    }

    internal static string? TryExtractShellPromptMarker(string text)
    {
        var lastLine = GetLastMeaningfulLine(text);
        if (string.IsNullOrWhiteSpace(lastLine) || IsLoginPrompt(lastLine) || IsPasswordPrompt(lastLine))
        {
            return null;
        }

        var trimmed = lastLine.TrimEnd();
        if (trimmed.Length == 0)
        {
            return null;
        }

        return trimmed[^1] is '#' or '$' or '>' or '%' ? trimmed : null;
    }

    private static void ApplyTerminalChunk(SessionState session, string text)
    {
        var currentLineStartIndex = session.CurrentLineStartIndex;
        var pendingCarriageReturn = session.PendingCarriageReturn;
        ApplyTerminalChunk(session.RenderedOutput, ref currentLineStartIndex, ref pendingCarriageReturn, text);
        session.CurrentLineStartIndex = currentLineStartIndex;
        session.PendingCarriageReturn = pendingCarriageReturn;
    }

    private static void ApplyTerminalChunk(StringBuilder buffer, ref int currentLineStartIndex, ref bool pendingCarriageReturn, string text)
    {
        if (pendingCarriageReturn)
        {
            text = "\r" + text;
            pendingCarriageReturn = false;
        }

        if (text.EndsWith('\r'))
        {
            pendingCarriageReturn = true;
            text = text[..^1];
        }

        var stripped = StripAnsiSequences(text).Replace("\r\n", "\n", StringComparison.Ordinal);
        foreach (var ch in stripped)
        {
            switch (ch)
            {
                case '\a':
                    break;
                case '\b':
                    if (buffer.Length > currentLineStartIndex)
                    {
                        buffer.Length--;
                    }
                    break;
                case '\r':
                    if (currentLineStartIndex <= buffer.Length)
                    {
                        buffer.Length = currentLineStartIndex;
                    }
                    break;
                default:
                    buffer.Append(ch);
                    if (ch == '\n')
                    {
                        currentLineStartIndex = buffer.Length;
                    }
                    break;
            }
        }
    }

    private static string NormalizeCommandOutput(string text)
    {
        return StripAnsiSequences(text)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\a", string.Empty, StringComparison.Ordinal)
            .Replace("\b", string.Empty, StringComparison.Ordinal);
    }

    private static string NormalizeNonTerminalText(string text)
    {
        return StripAnsiSequences(text)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal)
            .Replace("\a", string.Empty, StringComparison.Ordinal);
    }

    private static string StripAnsiSequences(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        return AnsiControlSequenceRegex.Replace(
            AnsiOperatingSystemCommandRegex.Replace(text, string.Empty),
            string.Empty);
    }

    private async Task<CommandReadinessResult> EnsureSessionReadyForCommandAsync(SessionState session, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var probeBudget = TimeSpan.FromSeconds(Math.Min(Math.Max(3, timeout.TotalSeconds / 4), 10));
        var initialProbe = await SendAndCaptureAsync(session, Environment.NewLine, probeBudget, cancellationToken).ConfigureAwait(false);
        if (IsLoginPrompt(initialProbe))
        {
            AppendHistory(session, TerminalHistoryEntryKind.System, "Detected login prompt; attempting default login.");
            return await TryLoginAsync(session, probeBudget, cancellationToken).ConfigureAwait(false);
        }

        var promptMarker = TryExtractShellPromptMarker(initialProbe);
        if (!string.IsNullOrWhiteSpace(promptMarker))
        {
            return new CommandReadinessResult(true, promptMarker, string.Empty, initialProbe);
        }

        var followUpProbe = await SendAndCaptureAsync(session, Environment.NewLine, TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        promptMarker = TryExtractShellPromptMarker(followUpProbe);
        return !string.IsNullOrWhiteSpace(promptMarker)
            ? new CommandReadinessResult(true, promptMarker, string.Empty, initialProbe + followUpProbe)
            : new CommandReadinessResult(false, string.Empty, "无法确认当前会话已经登录到命令行。", initialProbe + followUpProbe);
    }

    private async Task<CommandReadinessResult> TryLoginAsync(SessionState session, TimeSpan probeBudget, CancellationToken cancellationToken)
    {
        var loginOutput = await SendAndCaptureAsync(session, DefaultLoginUsername + Environment.NewLine, probeBudget, cancellationToken).ConfigureAwait(false);
        var combinedOutput = loginOutput;

        if (IsPasswordPrompt(loginOutput))
        {
            var passwordOutput = await SendAndCaptureAsync(session, DefaultLoginPassword + Environment.NewLine, probeBudget, cancellationToken).ConfigureAwait(false);
            combinedOutput += passwordOutput;
        }

        var verificationOutput = await SendAndCaptureAsync(session, Environment.NewLine, TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
        combinedOutput += verificationOutput;

        if (IsLoginPrompt(verificationOutput) || IsLoginPrompt(combinedOutput) || IsPasswordPrompt(verificationOutput))
        {
            return new CommandReadinessResult(false, string.Empty, "当前设置无法正常登录。", combinedOutput);
        }

        var promptMarker = TryExtractShellPromptMarker(verificationOutput) ?? TryExtractShellPromptMarker(combinedOutput);
        return !string.IsNullOrWhiteSpace(promptMarker)
            ? new CommandReadinessResult(true, promptMarker, string.Empty, combinedOutput)
            : new CommandReadinessResult(false, string.Empty, "登录后仍无法识别命令行提示符。", combinedOutput);
    }

    private static void BeginCommandCapture(SessionState session, string command, DateTime startedAt)
    {
        lock (session.SyncRoot)
        {
            session.ActiveCommandText = command;
            session.ActiveCommandStartedAt = startedAt.ToString("O");
            session.ActiveCommandBuffer = new StringBuilder();
            session.LastCommandOutputAt = null;
        }
    }

    private static string EndCommandCapture(SessionState session)
    {
        lock (session.SyncRoot)
        {
            var output = session.ActiveCommandBuffer?.ToString() ?? string.Empty;
            session.ActiveCommandBuffer = null;
            session.ActiveCommandText = string.Empty;
            session.ActiveCommandStartedAt = string.Empty;
            session.LastCommandOutputAt = null;
            return output;
        }
    }

    private static void ResetActiveCapture(SessionState session)
    {
        lock (session.SyncRoot)
        {
            session.ActiveCommandBuffer = null;
            session.ActiveCommandText = string.Empty;
            session.ActiveCommandStartedAt = string.Empty;
            session.LastCommandOutputAt = null;
        }
    }

    private async Task<string> SendAndCaptureAsync(SessionState session, string text, TimeSpan maxWait, CancellationToken cancellationToken)
    {
        BeginCommandCapture(session, string.Empty, DateTime.UtcNow);
        try
        {
            await session.Backend.SendAsync(text, cancellationToken).ConfigureAwait(false);
            await WaitForPromptProbeAsync(session, null, maxWait, cancellationToken).ConfigureAwait(false);
            return EndCommandCapture(session);
        }
        finally
        {
            ResetActiveCapture(session);
        }
    }

    private async Task<bool> WaitForCommandCompletionAsync(SessionState session, string promptMarker, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var startedAt = DateTime.UtcNow;
        var quietWindow = TimeSpan.FromMilliseconds(700);
        var emptyWait = TimeSpan.FromMilliseconds(900);
        var promptProbeSent = false;

        while (DateTime.UtcNow - startedAt < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(150, cancellationToken).ConfigureAwait(false);

            var snapshot = GetActiveCommandSnapshot(session, out var lastOutputAt);
            if (EndsWithShellPrompt(snapshot, promptMarker))
            {
                return false;
            }

            if (snapshot.Length == 0)
            {
                if (DateTime.UtcNow - startedAt >= emptyWait)
                {
                    promptProbeSent = true;
                    await session.Backend.SendAsync(Environment.NewLine, cancellationToken).ConfigureAwait(false);
                }

                continue;
            }

            if (lastOutputAt.HasValue && DateTime.UtcNow - lastOutputAt.Value >= quietWindow && !promptProbeSent)
            {
                promptProbeSent = true;
                await session.Backend.SendAsync(Environment.NewLine, cancellationToken).ConfigureAwait(false);
            }
        }

        return !EndsWithShellPrompt(GetActiveCommandSnapshot(session, out _), promptMarker);
    }

    private async Task WaitForPromptProbeAsync(SessionState session, string? promptMarker, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var startedAt = DateTime.UtcNow;
        var quietWindow = TimeSpan.FromMilliseconds(450);
        var noOutputWindow = TimeSpan.FromMilliseconds(650);

        while (DateTime.UtcNow - startedAt < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(120, cancellationToken).ConfigureAwait(false);

            var snapshot = GetActiveCommandSnapshot(session, out var lastOutputAt);
            if (!string.IsNullOrWhiteSpace(promptMarker) && EndsWithShellPrompt(snapshot, promptMarker))
            {
                return;
            }

            if (snapshot.Length == 0)
            {
                if (DateTime.UtcNow - startedAt >= noOutputWindow)
                {
                    return;
                }

                continue;
            }

            if (lastOutputAt.HasValue && DateTime.UtcNow - lastOutputAt.Value >= quietWindow)
            {
                return;
            }
        }
    }

    private static string GetActiveCommandSnapshot(SessionState session, out DateTime? lastOutputAt)
    {
        lock (session.SyncRoot)
        {
            lastOutputAt = session.LastCommandOutputAt;
            return session.ActiveCommandBuffer?.ToString() ?? string.Empty;
        }
    }

    private static bool EndsWithShellPrompt(string text, string promptMarker)
    {
        var detectedPrompt = TryExtractShellPromptMarker(text);
        return !string.IsNullOrWhiteSpace(detectedPrompt) && string.Equals(detectedPrompt, promptMarker, StringComparison.Ordinal);
    }

    private static string GetLastMeaningfulLine(string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var index = lines.Length - 1; index >= 0; index--)
        {
            var line = lines[index].TrimEnd();
            if (!string.IsNullOrWhiteSpace(line))
            {
                return line;
            }
        }

        return string.Empty;
    }

    private readonly record struct CommandReadinessResult(bool Success, string PromptMarker, string Error, string Output);

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
        public StringBuilder RenderedOutput { get; } = new();
        public StringBuilder RawOutput { get; } = new();
        public SemaphoreSlim CommandLock { get; } = new(1, 1);
        public StringBuilder? ActiveCommandBuffer { get; set; }
        public string ActiveCommandText { get; set; } = string.Empty;
        public string ActiveCommandStartedAt { get; set; } = string.Empty;
        public DateTime? LastCommandOutputAt { get; set; }
        public int CurrentLineStartIndex { get; set; }
        public bool PendingCarriageReturn { get; set; }
    }

    private sealed class SerialBridgeState : IAsyncDisposable
    {
        public SerialBridgeState(SerialSshBridgeServer bridge, string sessionId, SessionState session)
        {
            Bridge = bridge;
            SessionId = sessionId;
            Session = session;
            Info = bridge.Info;
        }

        public SerialSshBridgeServer Bridge { get; }
        public string SessionId { get; }
        public SessionState Session { get; }
        public TerminalBridgeInfo Info { get; }
        public EventHandler<TerminalOutputEventArgs>? SessionOutputHandler { get; set; }

        public async ValueTask DisposeAsync()
        {
            await Bridge.DisposeAsync().ConfigureAwait(false);
        }
    }
}