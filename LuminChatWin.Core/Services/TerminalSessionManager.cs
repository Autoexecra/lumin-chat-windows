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
    private const string TerminalEnter = "\n";
    private static readonly TimeSpan BridgeInputProtectionWindow = TimeSpan.FromSeconds(5);
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
        return await AddSessionAsync(options.Title, TerminalSessionKind.PowerShell, options.WorkingDirectory, true, options.ApiShared, options.SshShared, backend, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TerminalSessionInfo> CreateSshSessionAsync(TerminalSshOptions options, CancellationToken cancellationToken = default)
    {
        var backend = new SshTerminalBackend(options.Host, options.Port, options.Username, options.Password);
        return await AddSessionAsync(options.Title, TerminalSessionKind.Ssh, $"{options.Username}@{options.Host}:{options.Port}", true, options.ApiShared, options.SshShared, backend, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TerminalSessionInfo> CreateTelnetSessionAsync(TerminalTelnetOptions options, CancellationToken cancellationToken = default)
    {
        var backend = new TelnetTerminalBackend(options.Host, options.Port);
        return await AddSessionAsync(options.Title, TerminalSessionKind.Telnet, $"{options.Host}:{options.Port}", true, options.ApiShared, options.SshShared, backend, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TerminalSessionInfo> CreateSerialSessionAsync(TerminalSerialOptions options, CancellationToken cancellationToken = default)
    {
        var parity = Enum.TryParse<Parity>(options.Parity, true, out var parsedParity) ? parsedParity : Parity.None;
        var stopBits = Enum.TryParse<StopBits>(options.StopBits, true, out var parsedStopBits) ? parsedStopBits : StopBits.One;
        var backend = new SerialTerminalBackend(options.PortName, options.BaudRate, parity, options.DataBits, stopBits, options.NewLine);
        return await AddSessionAsync(options.Title, TerminalSessionKind.Serial, $"{options.PortName} @ {options.BaudRate}", true, options.ApiShared, options.SshShared, backend, cancellationToken).ConfigureAwait(false);
    }

    internal Task<TerminalSessionInfo> CreateSessionForTestsAsync(string title, TerminalSessionKind kind, string descriptor, bool supportsBridge, ITerminalBackend backend, CancellationToken cancellationToken = default)
    {
        return AddSessionAsync(title, kind, descriptor, supportsBridge, false, false, backend, cancellationToken);
    }

    public async Task SendInputAsync(string sessionId, string text, CancellationToken cancellationToken = default, bool recordInHistory = true)
    {
        var session = GetRequiredSession(sessionId);
        MarkLocalInput(session);
        await session.Backend.SendAsync(text, cancellationToken).ConfigureAwait(false);
        if (recordInHistory)
        {
            AppendHistory(session, TerminalHistoryEntryKind.Command, text.TrimEnd('\r', '\n'));
        }
    }

    internal async Task<bool> TrySendBridgeInputAsync(string sessionId, string text, CancellationToken cancellationToken = default)
    {
        var session = GetRequiredSession(sessionId);
        lock (session.SyncRoot)
        {
            if (session.LocalInputProtectedUntilUtc > DateTime.UtcNow)
            {
                return false;
            }
        }

        await session.Backend.SendAsync(text, cancellationToken).ConfigureAwait(false);
        return true;
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
            MarkLocalInput(session);
            BeginCommandCapture(session, command, startedAt);

            // Queue a second Enter after the command so the shell prompt response becomes an explicit completion marker.
            await session.Backend.SendAsync(command + TerminalEnter, cancellationToken).ConfigureAwait(false);
            await session.Backend.SendAsync(TerminalEnter, cancellationToken).ConfigureAwait(false);

            var promptMarker = readiness.PromptMarker;
            var timedOut = await WaitForCommandCompletionAsync(session, promptMarker, timeout, cancellationToken).ConfigureAwait(false);
            var error = string.Empty;

            if (timedOut)
            {
                var probeBudget = ResolveProbeBudget(timeout, Config.CommandExecution);
                var postTimeoutPromptDetected = await SendPromptProbeDuringCaptureAsync(session, promptMarker, probeBudget, cancellationToken).ConfigureAwait(false);
                if (postTimeoutPromptDetected)
                {
                    timedOut = false;
                }
                else
                {
                    await session.Backend.SendInterruptAsync(cancellationToken).ConfigureAwait(false);
                    AppendHistory(session, TerminalHistoryEntryKind.System, $"Command timed out after {timeout.TotalSeconds:F1}s; sent Ctrl+C.");
                    await Task.Delay(250, cancellationToken).ConfigureAwait(false);
                    var recoveredAfterInterrupt = await SendPromptProbeDuringCaptureAsync(session, promptMarker, probeBudget, cancellationToken).ConfigureAwait(false);
                    error = recoveredAfterInterrupt
                        ? "命令超时，已发送 Ctrl+C 中断阻塞任务。"
                        : "命令超时，且发送 Ctrl+C 后仍未恢复命令提示符。";
                }
            }

            var output = EndCommandCapture(session);

            return new TerminalCommandResult
            {
                Success = !timedOut && string.IsNullOrWhiteSpace(error),
                TimedOut = timedOut,
                Command = command,
                Output = output.TrimEnd(),
                StartedAt = startedAt.ToString("O"),
                CompletedAt = DateTime.UtcNow.ToString("O"),
                Error = error,
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
        if (!session.Info.SupportsBridge)
        {
            throw new InvalidOperationException("This session type does not support SSH bridge.");
        }

        if (_bridges.TryGetValue(sessionId, out var existing))
        {
            return existing.Info;
        }

        var config = Config;
        var host = config.SerialSshBridge.BindHost;
        if (!IPAddress.TryParse(host, out var bindAddress))
        {
            throw new InvalidOperationException($"Invalid SSH bridge host: {host}");
        }

        var port = requestedPort ?? ResolveBridgePort(session.Info);
        var bridgeServer = new SerialSshBridgeServer(
            sessionId,
            session.Info.Title,
            bindAddress,
            port,
            config.SerialSshBridge,
            (text, token) => TrySendBridgeInputAsync(sessionId, text, token),
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
        bridgeServer.ExceptionRaised += (_, ex) => AppendHistory(session, TerminalHistoryEntryKind.System, $"SSH bridge error: {ex.Message}");

        try
        {
            bridgeServer.Start();
            AppendHistory(session, TerminalHistoryEntryKind.System, $"SSH bridge listening on {host}:{port} for {config.SerialSshBridge.Username}");
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

    private static void MarkLocalInput(SessionState session)
    {
        lock (session.SyncRoot)
        {
            session.LocalInputProtectedUntilUtc = DateTime.UtcNow + BridgeInputProtectionWindow;
        }
    }

    private int ResolveBridgePort(TerminalSessionInfo info)
    {
        var config = Config;
        if (config.SerialSshBridge.PortOverrides.TryGetValue(info.SessionId, out var explicitPort))
        {
            return explicitPort;
        }

        var descriptorKey = ResolveBridgeOverrideKey(info.Kind, info.Descriptor);
        if (!string.IsNullOrWhiteSpace(descriptorKey) && config.SerialSshBridge.PortOverrides.TryGetValue(descriptorKey, out explicitPort))
        {
            return explicitPort;
        }

        foreach (var legacyKey in ResolveLegacyBridgeOverrideKeys(info.Kind, info.Descriptor))
        {
            if (config.SerialSshBridge.PortOverrides.TryGetValue(legacyKey, out explicitPort))
            {
                return explicitPort;
            }
        }

        var occupiedPorts = config.SerialSshBridge.PortOverrides
            .Where(entry => !string.Equals(entry.Key, info.SessionId, StringComparison.OrdinalIgnoreCase) &&
                            !string.Equals(entry.Key, descriptorKey, StringComparison.OrdinalIgnoreCase))
            .Select(entry => entry.Value)
            .Concat(_bridges
                .Where(entry => !string.Equals(entry.Key, info.SessionId, StringComparison.OrdinalIgnoreCase))
                .Select(entry => entry.Value.Info.Port))
            .ToList();

        return BuildDefaultBridgePort(info.Kind, config.SerialSshBridge.PortPrefix, occupiedPorts);
    }

    internal static string ResolveBridgeOverrideKey(TerminalSessionKind kind, string descriptor)
    {
        if (string.IsNullOrWhiteSpace(descriptor))
        {
            return string.Empty;
        }

        return $"{kind}:{descriptor.Trim()}";
    }

    internal static int BuildDefaultBridgePort(TerminalSessionKind kind, string serialPortPrefix, IEnumerable<int>? occupiedPorts = null)
    {
        var prefix = ResolveBridgePortPrefix(kind, serialPortPrefix);
        var occupied = new HashSet<int>((occupiedPorts ?? []).Where(port => port / 100 == prefix));
        for (var suffix = 1; suffix <= 99; suffix++)
        {
            var candidate = prefix * 100 + suffix;
            if (!occupied.Contains(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException($"No free SSH bridge port left in range {prefix}01-{prefix}99.");
    }

    internal static int ResolveBridgePortPrefix(TerminalSessionKind kind, string serialPortPrefix)
    {
        return kind switch
        {
            TerminalSessionKind.Telnet => 23,
            TerminalSessionKind.Ssh => 24,
            TerminalSessionKind.PowerShell => 25,
            TerminalSessionKind.Serial when int.TryParse(serialPortPrefix, out var parsedPrefix) => parsedPrefix,
            TerminalSessionKind.Serial => 22,
            _ => 22,
        };
    }

    internal static IEnumerable<string> ResolveLegacyBridgeOverrideKeys(TerminalSessionKind kind, string descriptor)
    {
        if (kind != TerminalSessionKind.Serial || string.IsNullOrWhiteSpace(descriptor))
        {
            yield break;
        }

        var portName = descriptor.Split('@', 2, StringSplitOptions.TrimEntries)[0].Trim();
        if (!string.IsNullOrWhiteSpace(portName))
        {
            yield return portName;
        }
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
        return TryExtractShellPromptMarker(text, [@".+[#$>%]\s*$"]);
    }

    internal static string? TryExtractShellPromptMarker(string text, IEnumerable<string>? promptPatterns)
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

        foreach (var promptRegex in BuildPromptRegexes(promptPatterns))
        {
            if (promptRegex.IsMatch(trimmed))
            {
                return trimmed;
            }
        }

        return null;
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
        var executionConfig = Config.CommandExecution;
        var maxAttempts = Math.Max(1, executionConfig.MaxLoginAttempts);
        var probeBudget = ResolveProbeBudget(timeout, executionConfig);
        var combinedOutput = new StringBuilder();

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            var probe = await SendAndCaptureAsync(session, TerminalEnter, probeBudget, cancellationToken).ConfigureAwait(false);
            combinedOutput.Append(probe);

            var promptMarker = TryExtractShellPromptMarker(probe, executionConfig.PromptPatterns);
            if (!string.IsNullOrWhiteSpace(promptMarker))
            {
                return new CommandReadinessResult(true, promptMarker, string.Empty, combinedOutput.ToString());
            }

            if (IsLoginPrompt(probe))
            {
                var loginResult = await TryLoginAsync(session, executionConfig, probeBudget, cancellationToken).ConfigureAwait(false);
                combinedOutput.Append(loginResult.Output);
                if (loginResult.Success)
                {
                    return new CommandReadinessResult(true, loginResult.PromptMarker, string.Empty, combinedOutput.ToString());
                }

                if (attempt == maxAttempts - 1)
                {
                    return new CommandReadinessResult(false, string.Empty, loginResult.Error, combinedOutput.ToString());
                }

                continue;
            }

            if (IsPasswordPrompt(probe))
            {
                var passwordResult = await CompletePasswordLoginAsync(session, executionConfig, probeBudget, cancellationToken).ConfigureAwait(false);
                combinedOutput.Append(passwordResult.Output);
                if (passwordResult.Success)
                {
                    return new CommandReadinessResult(true, passwordResult.PromptMarker, string.Empty, combinedOutput.ToString());
                }

                if (attempt == maxAttempts - 1)
                {
                    return new CommandReadinessResult(false, string.Empty, passwordResult.Error, combinedOutput.ToString());
                }

                continue;
            }

            var recoveryResult = await TryRecoverBlockedSessionAsync(session, executionConfig, probeBudget, cancellationToken).ConfigureAwait(false);
            combinedOutput.Append(recoveryResult.Output);
            if (recoveryResult.Success)
            {
                return new CommandReadinessResult(true, recoveryResult.PromptMarker, string.Empty, combinedOutput.ToString());
            }

            if (attempt == maxAttempts - 1)
            {
                return new CommandReadinessResult(false, string.Empty, recoveryResult.Error, combinedOutput.ToString());
            }
        }

        return new CommandReadinessResult(false, string.Empty, "连续多次探测后仍未正确登录。", combinedOutput.ToString());
    }

    private async Task<CommandReadinessResult> TryLoginAsync(SessionState session, TerminalCommandExecutionConfig executionConfig, TimeSpan probeBudget, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(executionConfig.LoginUsername))
        {
            return new CommandReadinessResult(false, string.Empty, "当前处于 login: 界面，但未配置默认登录账号。", string.Empty);
        }

        var loginOutput = await SendAndCaptureAsync(session, executionConfig.LoginUsername + TerminalEnter, probeBudget, cancellationToken).ConfigureAwait(false);
        var combinedOutput = loginOutput;

        if (TryExtractShellPromptMarker(loginOutput, executionConfig.PromptPatterns) is string promptMarker)
        {
            return new CommandReadinessResult(true, promptMarker, string.Empty, combinedOutput);
        }

        if (IsPasswordPrompt(loginOutput))
        {
            var passwordResult = await CompletePasswordLoginAsync(session, executionConfig, probeBudget, cancellationToken).ConfigureAwait(false);
            return passwordResult with { Output = combinedOutput + passwordResult.Output };
        }

        var verificationOutput = await SendAndCaptureAsync(session, TerminalEnter, probeBudget, cancellationToken).ConfigureAwait(false);
        combinedOutput += verificationOutput;
        string? verifiedPromptMarker = TryExtractShellPromptMarker(verificationOutput, executionConfig.PromptPatterns)
            ?? TryExtractShellPromptMarker(combinedOutput, executionConfig.PromptPatterns);
        return !string.IsNullOrWhiteSpace(verifiedPromptMarker)
            ? new CommandReadinessResult(true, verifiedPromptMarker, string.Empty, combinedOutput)
            : new CommandReadinessResult(false, string.Empty, "输入登录账号后仍未进入命令行。", combinedOutput);
    }

    private async Task<CommandReadinessResult> CompletePasswordLoginAsync(SessionState session, TerminalCommandExecutionConfig executionConfig, TimeSpan probeBudget, CancellationToken cancellationToken)
    {
        var passwordOutput = await SendAndCaptureAsync(session, (executionConfig.LoginPassword ?? string.Empty) + TerminalEnter, probeBudget, cancellationToken).ConfigureAwait(false);
        var combinedOutput = passwordOutput;
        if (TryExtractShellPromptMarker(passwordOutput, executionConfig.PromptPatterns) is string promptMarker)
        {
            return new CommandReadinessResult(true, promptMarker, string.Empty, combinedOutput);
        }

        var verificationOutput = await SendAndCaptureAsync(session, TerminalEnter, probeBudget, cancellationToken).ConfigureAwait(false);
        combinedOutput += verificationOutput;

        if (IsLoginPrompt(verificationOutput) || IsPasswordPrompt(verificationOutput))
        {
            return new CommandReadinessResult(false, string.Empty, "当前设置无法正常登录。", combinedOutput);
        }

        string? verifiedPromptMarker = TryExtractShellPromptMarker(verificationOutput, executionConfig.PromptPatterns)
            ?? TryExtractShellPromptMarker(combinedOutput, executionConfig.PromptPatterns);
        return !string.IsNullOrWhiteSpace(verifiedPromptMarker)
            ? new CommandReadinessResult(true, verifiedPromptMarker, string.Empty, combinedOutput)
            : new CommandReadinessResult(false, string.Empty, "登录后仍无法识别命令行提示符。", combinedOutput);
    }

    private async Task<CommandReadinessResult> TryRecoverBlockedSessionAsync(SessionState session, TerminalCommandExecutionConfig executionConfig, TimeSpan probeBudget, CancellationToken cancellationToken)
    {
        await session.Backend.SendInterruptAsync(cancellationToken).ConfigureAwait(false);
        AppendHistory(session, TerminalHistoryEntryKind.System, "探测到终端可能被阻塞，已发送 Ctrl+C。\n");
        await Task.Delay(250, cancellationToken).ConfigureAwait(false);

        var recoveryOutput = await SendAndCaptureAsync(session, TerminalEnter, probeBudget, cancellationToken).ConfigureAwait(false);
        if (TryExtractShellPromptMarker(recoveryOutput, executionConfig.PromptPatterns) is string promptMarker)
        {
            return new CommandReadinessResult(true, promptMarker, string.Empty, recoveryOutput);
        }

        if (IsLoginPrompt(recoveryOutput))
        {
            var loginResult = await TryLoginAsync(session, executionConfig, probeBudget, cancellationToken).ConfigureAwait(false);
            return loginResult with { Output = recoveryOutput + loginResult.Output };
        }

        if (IsPasswordPrompt(recoveryOutput))
        {
            var passwordResult = await CompletePasswordLoginAsync(session, executionConfig, probeBudget, cancellationToken).ConfigureAwait(false);
            return passwordResult with { Output = recoveryOutput + passwordResult.Output };
        }

        return new CommandReadinessResult(false, string.Empty, "发送 Ctrl+C 后仍未恢复命令提示符。", recoveryOutput);
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
        var quietWindow = TimeSpan.FromMilliseconds(500);

        while (DateTime.UtcNow - startedAt < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(150, cancellationToken).ConfigureAwait(false);

            var snapshot = GetActiveCommandSnapshot(session, out var lastOutputAt);
            var promptCount = CountPromptOccurrences(snapshot, promptMarker);
            if (promptCount >= 2)
            {
                return false;
            }

            if (promptCount >= 1 && lastOutputAt.HasValue && DateTime.UtcNow - lastOutputAt.Value >= quietWindow)
            {
                return false;
            }
        }

        return CountPromptOccurrences(GetActiveCommandSnapshot(session, out _), promptMarker) < 2;
    }

    private async Task<bool> SendPromptProbeDuringCaptureAsync(SessionState session, string promptMarker, TimeSpan timeout, CancellationToken cancellationToken)
    {
        await session.Backend.SendAsync(TerminalEnter, cancellationToken).ConfigureAwait(false);
        await WaitForPromptProbeAsync(session, promptMarker, timeout, cancellationToken).ConfigureAwait(false);
        return CountPromptOccurrences(GetActiveCommandSnapshot(session, out _), promptMarker) >= 1;
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
            if (!string.IsNullOrWhiteSpace(promptMarker) && CountPromptOccurrences(snapshot, promptMarker) >= 1)
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

    private static int CountPromptOccurrences(string text, string promptMarker)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(promptMarker))
        {
            return 0;
        }

        return text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Count(line => string.Equals(line.TrimEnd(), promptMarker, StringComparison.Ordinal));
    }

    private static TimeSpan ResolveProbeBudget(TimeSpan timeout, TerminalCommandExecutionConfig executionConfig)
    {
        var configuredSeconds = executionConfig.ProbeTimeoutSeconds > 0 ? executionConfig.ProbeTimeoutSeconds : 3;
        return TimeSpan.FromSeconds(Math.Min(Math.Max(1, configuredSeconds), Math.Max(1, timeout.TotalSeconds)));
    }

    private static IReadOnlyList<Regex> BuildPromptRegexes(IEnumerable<string>? promptPatterns)
    {
        var patterns = promptPatterns?
            .Where(static pattern => !string.IsNullOrWhiteSpace(pattern))
            .ToList() ?? [];

        if (patterns.Count == 0)
        {
            patterns.Add(@".+[#$>%]\s*$");
        }

        var regexes = new List<Regex>(patterns.Count);
        foreach (var pattern in patterns)
        {
            try
            {
                regexes.Add(new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
            }
            catch (ArgumentException)
            {
            }
        }

        if (regexes.Count == 0)
        {
            regexes.Add(new Regex(@".+[#$>%]\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
        }

        return regexes;
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
        public DateTime LocalInputProtectedUntilUtc { get; set; } = DateTime.MinValue;
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