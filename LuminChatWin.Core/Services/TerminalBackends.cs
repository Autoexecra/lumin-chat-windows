using System.Diagnostics;
using System.IO.Ports;
using System.Net.Sockets;
using System.Text;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace LuminChatWin.Core.Services;

public interface ITerminalBackend : IAsyncDisposable
{
    event EventHandler<string>? OutputReceived;
    event EventHandler<string>? ErrorReceived;
    event EventHandler<string>? StatusReceived;

    bool IsConnected { get; }
    string Descriptor { get; }

    Task StartAsync(CancellationToken cancellationToken = default);
    Task SendAsync(string text, CancellationToken cancellationToken = default);
    Task SendInterruptAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}

internal sealed class PowerShellTerminalBackend : ITerminalBackend
{
    private readonly string _program;
    private readonly string _arguments;
    private readonly string _workingDirectory;
    private Process? _process;
    private CancellationTokenSource? _readerCts;
    private Task? _stdoutTask;
    private Task? _stderrTask;

    public PowerShellTerminalBackend(string program, string arguments, string workingDirectory)
    {
        _program = string.IsNullOrWhiteSpace(program) ? "pwsh" : program;
        _arguments = arguments;
        _workingDirectory = string.IsNullOrWhiteSpace(workingDirectory) ? Environment.CurrentDirectory : workingDirectory;
    }

    public event EventHandler<string>? OutputReceived;
    public event EventHandler<string>? ErrorReceived;
    public event EventHandler<string>? StatusReceived;

    public bool IsConnected => _process is { HasExited: false };

    public string Descriptor => _workingDirectory;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        var info = BuildStartInfo(_program, _arguments);
        try
        {
            _process = Process.Start(info);
        }
        catch (Exception) when (string.Equals(_program, "pwsh", StringComparison.OrdinalIgnoreCase))
        {
            info = BuildStartInfo("powershell", _arguments);
            _process = Process.Start(info);
        }

        if (_process is null)
        {
            throw new InvalidOperationException("Unable to start PowerShell process.");
        }

        _process.StandardInput.WriteLine("$PSStyle.OutputRendering='Ansi'");
        _process.StandardInput.Flush();
        _readerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _stdoutTask = Task.Run(() => ReadStreamAsync(_process.StandardOutput, OutputReceived, _readerCts.Token), _readerCts.Token);
        _stderrTask = Task.Run(() => ReadStreamAsync(_process.StandardError, ErrorReceived, _readerCts.Token), _readerCts.Token);
        StatusReceived?.Invoke(this, $"Connected to PowerShell in {_workingDirectory}");
        return Task.CompletedTask;
    }

    public async Task SendAsync(string text, CancellationToken cancellationToken = default)
    {
        if (_process is null || _process.HasExited)
        {
            throw new InvalidOperationException("PowerShell session is not running.");
        }

        await _process.StandardInput.WriteAsync(text.AsMemory(), cancellationToken).ConfigureAwait(false);
        await _process.StandardInput.FlushAsync().ConfigureAwait(false);
    }

    public Task SendInterruptAsync(CancellationToken cancellationToken = default)
    {
        return SendAsync("\u0003", cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_readerCts is not null && !_readerCts.IsCancellationRequested)
        {
            _readerCts.Cancel();
        }

        if (_process is { HasExited: false })
        {
            try
            {
                await SendAsync(Environment.NewLine + "exit" + Environment.NewLine, cancellationToken).ConfigureAwait(false);
                var waitTask = _process.WaitForExitAsync(cancellationToken);
                var completedTask = await Task.WhenAny(waitTask, Task.Delay(TimeSpan.FromSeconds(2), cancellationToken)).ConfigureAwait(false);
                if (!ReferenceEquals(completedTask, waitTask) || !_process.HasExited)
                {
                    _process.Kill(true);
                }
            }
            catch
            {
                _process.Kill(true);
            }
        }

        if (_stdoutTask is not null)
        {
            await SafeAwaitAsync(_stdoutTask).ConfigureAwait(false);
        }
        if (_stderrTask is not null)
        {
            await SafeAwaitAsync(_stderrTask).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _readerCts?.Dispose();
        _process?.Dispose();
    }

    private ProcessStartInfo BuildStartInfo(string program, string arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = program,
            Arguments = arguments,
            WorkingDirectory = _workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        startInfo.Environment["TERM"] = "xterm-256color";
        startInfo.Environment["COLORTERM"] = "truecolor";
        startInfo.Environment["CLICOLOR"] = "1";
        startInfo.Environment["CLICOLOR_FORCE"] = "1";
        return startInfo;
    }

    private static async Task ReadStreamAsync(StreamReader reader, EventHandler<string>? handler, CancellationToken cancellationToken)
    {
        var buffer = new char[256];
        while (!cancellationToken.IsCancellationRequested)
        {
            var charsRead = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (charsRead <= 0)
            {
                break;
            }

            handler?.Invoke(null!, new string(buffer, 0, charsRead));
        }
    }

    private static async Task SafeAwaitAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
        }
    }
}

internal sealed class SshTerminalBackend : ITerminalBackend
{
    private readonly string _host;
    private readonly int _port;
    private readonly string _username;
    private readonly string _password;
    private SshClient? _client;
    private ShellStream? _stream;
    private CancellationTokenSource? _readerCts;
    private Task? _readerTask;

    public SshTerminalBackend(string host, int port, string username, string password)
    {
        _host = host;
        _port = port;
        _username = username;
        _password = password;
    }

    public event EventHandler<string>? OutputReceived;
    public event EventHandler<string>? ErrorReceived;
    public event EventHandler<string>? StatusReceived;

    public bool IsConnected => _client?.IsConnected == true;

    public string Descriptor => $"{_username}@{_host}:{_port}";

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        _client = new SshClient(_host, _port, _username, _password);
        try
        {
            _client.Connect();
            _stream = _client.CreateShellStream("lumin-chat", 120, 40, 1200, 800, 1024);
            _readerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _readerTask = Task.Run(() => ReadLoopAsync(_readerCts.Token), _readerCts.Token);
            StatusReceived?.Invoke(this, $"Connected to {Descriptor}");
            return Task.CompletedTask;
        }
        catch (SshException ex)
        {
            ErrorReceived?.Invoke(this, ex.Message + Environment.NewLine);
            throw;
        }
    }

    public async Task SendAsync(string text, CancellationToken cancellationToken = default)
    {
        if (_stream is null)
        {
            throw new InvalidOperationException("SSH stream is not connected.");
        }

        _stream.Write(text);
        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task SendInterruptAsync(CancellationToken cancellationToken = default)
    {
        return SendAsync("\u0003", cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_readerCts is not null && !_readerCts.IsCancellationRequested)
        {
            _readerCts.Cancel();
        }

        if (_readerTask is not null)
        {
            try
            {
                await _readerTask.ConfigureAwait(false);
            }
            catch
            {
            }
        }

        _stream?.Dispose();
        if (_client?.IsConnected == true)
        {
            _client.Disconnect();
        }
        _client?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _readerCts?.Dispose();
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        while (!cancellationToken.IsCancellationRequested && _stream is not null)
        {
            if (!_stream.DataAvailable)
            {
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var bytesRead = await _stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (bytesRead <= 0)
            {
                break;
            }

            OutputReceived?.Invoke(this, Encoding.UTF8.GetString(buffer, 0, bytesRead));
        }
    }
}

internal sealed class TelnetTerminalBackend : ITerminalBackend
{
    private readonly string _host;
    private readonly int _port;
    private TcpClient? _client;
    private NetworkStream? _stream;
    private CancellationTokenSource? _readerCts;
    private Task? _readerTask;

    public TelnetTerminalBackend(string host, int port)
    {
        _host = host;
        _port = port;
    }

    public event EventHandler<string>? OutputReceived;
    public event EventHandler<string>? ErrorReceived;
    public event EventHandler<string>? StatusReceived;

    public bool IsConnected => _client?.Connected == true;

    public string Descriptor => $"{_host}:{_port}";

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        _client = new TcpClient();
        await _client.ConnectAsync(_host, _port, cancellationToken).ConfigureAwait(false);
        _stream = _client.GetStream();
        _readerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _readerTask = Task.Run(() => ReadLoopAsync(_readerCts.Token), _readerCts.Token);
        StatusReceived?.Invoke(this, $"Connected to Telnet {Descriptor}");
    }

    public async Task SendAsync(string text, CancellationToken cancellationToken = default)
    {
        if (_stream is null)
        {
            throw new InvalidOperationException("Telnet session is not connected.");
        }

        var bytes = Encoding.UTF8.GetBytes(text);
        await _stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task SendInterruptAsync(CancellationToken cancellationToken = default)
    {
        return SendAsync("\u0003", cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_readerCts is not null && !_readerCts.IsCancellationRequested)
        {
            _readerCts.Cancel();
        }

        if (_readerTask is not null)
        {
            try
            {
                await _readerTask.ConfigureAwait(false);
            }
            catch
            {
            }
        }

        _stream?.Dispose();
        _client?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _readerCts?.Dispose();
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        if (_stream is null)
        {
            return;
        }

        var buffer = new byte[4096];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var bytesRead = await _stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (bytesRead <= 0)
                {
                    break;
                }

                OutputReceived?.Invoke(this, Encoding.UTF8.GetString(buffer, 0, bytesRead));
            }
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            ErrorReceived?.Invoke(this, ex.Message + Environment.NewLine);
        }
    }
}

internal sealed class SerialTerminalBackend : ITerminalBackend
{
    private readonly SerialPort _serialPort;

    public SerialTerminalBackend(string portName, int baudRate, Parity parity, int dataBits, StopBits stopBits, string newLine)
    {
        _serialPort = new SerialPort(portName, baudRate, parity, dataBits, stopBits)
        {
            Encoding = Encoding.UTF8,
            NewLine = string.IsNullOrEmpty(newLine) ? "\r\n" : newLine,
        };
    }

    public event EventHandler<string>? OutputReceived;
    public event EventHandler<string>? ErrorReceived;
    public event EventHandler<string>? StatusReceived;

    public bool IsConnected => _serialPort.IsOpen;

    public string Descriptor => $"{_serialPort.PortName} @ {_serialPort.BaudRate}";

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        _serialPort.DataReceived += OnDataReceived;
        _serialPort.ErrorReceived += OnErrorReceived;
        _serialPort.Open();
        StatusReceived?.Invoke(this, $"Connected to {Descriptor}");
        return Task.CompletedTask;
    }

    public Task SendAsync(string text, CancellationToken cancellationToken = default)
    {
        _serialPort.Write(text);
        return Task.CompletedTask;
    }

    public Task SendInterruptAsync(CancellationToken cancellationToken = default)
    {
        _serialPort.Write("\u0003");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        _serialPort.DataReceived -= OnDataReceived;
        _serialPort.ErrorReceived -= OnErrorReceived;
        if (_serialPort.IsOpen)
        {
            _serialPort.Close();
        }

        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _serialPort.Dispose();
    }

    private void OnDataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        try
        {
            var text = _serialPort.ReadExisting();
            if (!string.IsNullOrEmpty(text))
            {
                OutputReceived?.Invoke(this, text);
            }
        }
        catch (Exception ex)
        {
            ErrorReceived?.Invoke(this, ex.Message + Environment.NewLine);
        }
    }

    private void OnErrorReceived(object sender, SerialErrorReceivedEventArgs e)
    {
        ErrorReceived?.Invoke(this, $"Serial error: {e.EventType}{Environment.NewLine}");
    }
}