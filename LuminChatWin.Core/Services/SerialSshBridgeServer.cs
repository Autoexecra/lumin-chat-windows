using System.Collections.Concurrent;
using System.Net;
using System.Text;
using FxSsh;
using FxSsh.Services;
using LuminChatWin.Core.Models;

namespace LuminChatWin.Core.Services;

internal sealed class SerialSshBridgeServer : IAsyncDisposable
{
    private readonly SshServer _server;
    private readonly Func<string, CancellationToken, Task<bool>> _sendInputAsync;
    private readonly Func<string, TimeSpan, CancellationToken, Task<TerminalCommandResult>> _executeCommandAsync;
    private readonly Func<string> _recentOutputAccessor;
    private readonly ConcurrentDictionary<int, ShellConnection> _shells = new();
    private readonly string _sessionId;
    private readonly string _sessionTitle;
    private readonly string _username;
    private readonly string _password;
    private readonly TimeSpan _execTimeout;
    private int _shellCounter;
    private bool _started;

    public SerialSshBridgeServer(
        string sessionId,
        string sessionTitle,
        IPAddress bindAddress,
        int port,
        TerminalSerialBridgeConfig config,
        Func<string, CancellationToken, Task<bool>> sendInputAsync,
        Func<string> recentOutputAccessor,
        Func<string, TimeSpan, CancellationToken, Task<TerminalCommandResult>> executeCommandAsync)
    {
        _sessionId = sessionId;
        _sessionTitle = sessionTitle;
        _sendInputAsync = sendInputAsync;
        _recentOutputAccessor = recentOutputAccessor;
        _executeCommandAsync = executeCommandAsync;
        _username = config.Username?.Trim() ?? string.Empty;
        _password = config.Password ?? string.Empty;
        _execTimeout = TimeSpan.FromSeconds(Math.Max(3, config.ExecTimeoutSeconds));

        var hostKeyPath = ExpandPath(config.HostKeyPath);
        var hostKeyPem = LoadOrCreateHostKey(hostKeyPath);
        _server = new SshServer(new StartingInfo(bindAddress, port, "SSH-2.0-LuminChatTerminal"));
        _server.AddHostKey("rsa-sha2-256", hostKeyPem);
        _server.AddHostKey("rsa-sha2-512", hostKeyPem);
        _server.ConnectionAccepted += Server_ConnectionAccepted;
        _server.ExceptionRasied += Server_ExceptionRaised;

        Info = new TerminalBridgeInfo
        {
            SessionId = sessionId,
            Host = bindAddress.ToString(),
            Port = port,
            Protocol = "ssh",
            Message = string.IsNullOrWhiteSpace(_username)
                ? $"Use ssh -p {port} <username>@{bindAddress} (any username, empty password allowed by default)."
                : string.IsNullOrEmpty(_password)
                    ? $"Use ssh -p {port} {_username}@{bindAddress} and press Enter at the password prompt."
                    : $"Use ssh -p {port} {_username}@{bindAddress}.",
        };
    }

    public TerminalBridgeInfo Info { get; }

    public event EventHandler<Exception>? ExceptionRaised;

    public void Start()
    {
        if (_started)
        {
            return;
        }

        _server.Start();
        _started = true;
    }

    public void HandleTerminalOutput(TerminalOutputEventArgs args)
    {
        if (!string.Equals(args.SessionId, _sessionId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (args.Kind is not (TerminalHistoryEntryKind.Output or TerminalHistoryEntryKind.Error) || string.IsNullOrEmpty(args.Text))
        {
            return;
        }

        var payload = Encoding.UTF8.GetBytes(args.Text);
        foreach (var shell in _shells.Values.ToArray())
        {
            shell.SendOutput(payload);
        }
    }

    public ValueTask DisposeAsync()
    {
        foreach (var shell in _shells.Values.ToArray())
        {
            shell.Dispose();
        }

        if (_started)
        {
            _server.Dispose();
            _started = false;
        }

        return ValueTask.CompletedTask;
    }

    private void Server_ConnectionAccepted(object? sender, Session session)
    {
        session.ServiceRegistered += (_, service) => Session_ServiceRegistered(service);
    }

    private void Session_ServiceRegistered(FxSsh.Services.SshService service)
    {
        var userAuthService = service as UserauthService;
        if (userAuthService is not null)
        {
            userAuthService.Userauth += UserAuthService_UserAuth;
            return;
        }

        var connectionService = service as ConnectionService;
        if (connectionService is not null)
        {
            connectionService.CommandOpened += ConnectionService_CommandOpened;
        }
    }

    private void UserAuthService_UserAuth(object? sender, UserauthArgs e)
    {
        var usernameAllowed = string.IsNullOrWhiteSpace(_username) || string.Equals(e.Username, _username, StringComparison.Ordinal);
        var passwordAllowed = string.Equals(e.Password ?? string.Empty, _password, StringComparison.Ordinal);
        e.Result = usernameAllowed && passwordAllowed;
    }

    private void ConnectionService_CommandOpened(object? sender, CommandRequestedArgs e)
    {
        if (string.Equals(e.ShellType, "shell", StringComparison.OrdinalIgnoreCase))
        {
            AttachShell(e.Channel);
            return;
        }

        if (string.Equals(e.ShellType, "exec", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(e.CommandText))
        {
            _ = RunExecAsync(e.Channel, e.CommandText);
        }
    }

    private void AttachShell(SessionChannel channel)
    {
        var shellId = Interlocked.Increment(ref _shellCounter);
        var shell = new ShellConnection(shellId, channel, _sendInputAsync, RemoveShell);
        if (!_shells.TryAdd(shellId, shell))
        {
            shell.Dispose();
            return;
        }

        shell.SendOutput(Encoding.UTF8.GetBytes($"[luminTerminal] Serial SSH bridge attached to {_sessionTitle}\r\n"));

        var snapshot = _recentOutputAccessor();
        if (!string.IsNullOrWhiteSpace(snapshot))
        {
            shell.SendOutput(Encoding.UTF8.GetBytes(snapshot));
        }

        _ = _sendInputAsync("\n", CancellationToken.None);
    }

    private async Task RunExecAsync(SessionChannel channel, string commandText)
    {
        try
        {
            var result = await _executeCommandAsync(commandText, _execTimeout, CancellationToken.None).ConfigureAwait(false);
            var output = result.Output;
            if (!string.IsNullOrWhiteSpace(result.Error))
            {
                output = string.IsNullOrWhiteSpace(output)
                    ? result.Error
                    : output + Environment.NewLine + result.Error;
            }

            if (!string.IsNullOrEmpty(output))
            {
                channel.SendData(Encoding.UTF8.GetBytes(output));
            }

            channel.SendEof();
            channel.SendClose(result.Success ? 0u : 1u);
        }
        catch (Exception ex)
        {
            channel.SendData(Encoding.UTF8.GetBytes(ex.Message + Environment.NewLine));
            channel.SendEof();
            channel.SendClose(1u);
        }
    }

    private void RemoveShell(int shellId)
    {
        _shells.TryRemove(shellId, out _);
    }

    private void Server_ExceptionRaised(object? sender, Exception e)
    {
        ExceptionRaised?.Invoke(this, e);
    }

    private static string LoadOrCreateHostKey(string hostKeyPath)
    {
        var directory = Path.GetDirectoryName(hostKeyPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (File.Exists(hostKeyPath))
        {
            return File.ReadAllText(hostKeyPath);
        }

        var pem = KeyGenerator.GenerateRsaKeyPem(2048);
        File.WriteAllText(hostKeyPath, pem);
        return pem;
    }

    private static string ExpandPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            path = "~/.lumin-chat-win/terminal-serial-bridge-hostkey.pem";
        }

        if (path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, path[2..].Replace('/', Path.DirectorySeparatorChar));
        }

        return Path.GetFullPath(path);
    }

    private sealed class ShellConnection : IDisposable
    {
        private readonly int _shellId;
        private readonly SessionChannel _channel;
        private readonly Func<string, CancellationToken, Task<bool>> _sendInputAsync;
        private readonly Action<int> _disposeCallback;
        private DateTime _lastReadonlyNoticeAt = DateTime.MinValue;
        private bool _disposed;

        public ShellConnection(int shellId, SessionChannel channel, Func<string, CancellationToken, Task<bool>> sendInputAsync, Action<int> disposeCallback)
        {
            _shellId = shellId;
            _channel = channel;
            _sendInputAsync = sendInputAsync;
            _disposeCallback = disposeCallback;
            _channel.DataReceived += Channel_DataReceived;
            _channel.CloseReceived += Channel_CloseReceived;
            _channel.EofReceived += Channel_EofReceived;
        }

        public void SendOutput(byte[] payload)
        {
            if (_disposed || payload.Length == 0)
            {
                return;
            }

            try
            {
                _channel.SendData(payload);
            }
            catch
            {
                Dispose();
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _channel.DataReceived -= Channel_DataReceived;
            _channel.CloseReceived -= Channel_CloseReceived;
            _channel.EofReceived -= Channel_EofReceived;
            _disposeCallback(_shellId);
        }

        private void Channel_DataReceived(object? sender, byte[] data)
        {
            if (_disposed || data.Length == 0)
            {
                return;
            }

            _ = ForwardInputAsync(Encoding.Latin1.GetString(data));
        }

        private async Task ForwardInputAsync(string text)
        {
            var accepted = await _sendInputAsync(text, CancellationToken.None).ConfigureAwait(false);
            if (accepted || DateTime.UtcNow - _lastReadonlyNoticeAt < TimeSpan.FromSeconds(1))
            {
                return;
            }

            _lastReadonlyNoticeAt = DateTime.UtcNow;
            SendOutput(Encoding.UTF8.GetBytes("\r\n[luminTerminal] 主窗口最近 5 秒内有输入，当前 SSH 共享暂时只读。\r\n"));
        }

        private void Channel_CloseReceived(object? sender, EventArgs e)
        {
            Dispose();
        }

        private void Channel_EofReceived(object? sender, EventArgs e)
        {
            Dispose();
        }
    }
}