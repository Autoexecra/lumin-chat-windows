namespace LuminChatWin.Core.Models;

public enum TerminalSessionKind
{
    PowerShell,
    Ssh,
    Telnet,
    Serial,
}

public enum TerminalHistoryEntryKind
{
    System,
    Command,
    Output,
    Error,
    Agent,
}

public sealed class TerminalSessionInfo
{
    public string SessionId { get; init; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = string.Empty;
    public TerminalSessionKind Kind { get; init; }
    public string Descriptor { get; set; } = string.Empty;
    public string CreatedAt { get; init; } = DateTime.UtcNow.ToString("O");
    public string LastActivityAt { get; set; } = DateTime.UtcNow.ToString("O");
    public bool IsConnected { get; set; }
    public bool SupportsBridge { get; init; }
}

public sealed class TerminalHistoryEntry
{
    public string Timestamp { get; init; } = DateTime.UtcNow.ToString("O");
    public TerminalHistoryEntryKind Kind { get; init; }
    public string Text { get; init; } = string.Empty;
}

public sealed class TerminalCommandResult
{
    public bool Success { get; init; }
    public bool TimedOut { get; init; }
    public string Command { get; init; } = string.Empty;
    public string Output { get; init; } = string.Empty;
    public string StartedAt { get; init; } = DateTime.UtcNow.ToString("O");
    public string CompletedAt { get; init; } = DateTime.UtcNow.ToString("O");
    public string Error { get; init; } = string.Empty;
}

public sealed class ActiveTerminalCommandSnapshot
{
    public bool IsRunning { get; init; }
    public string Command { get; init; } = string.Empty;
    public string StartedAt { get; init; } = string.Empty;
    public string Output { get; init; } = string.Empty;
}

public sealed class TerminalOutputEventArgs : EventArgs
{
    public string SessionId { get; init; } = string.Empty;
    public TerminalHistoryEntryKind Kind { get; init; }
    public string Text { get; init; } = string.Empty;
}

public sealed class TerminalPowerShellOptions
{
    public string Title { get; set; } = "PowerShell";
    public string Program { get; set; } = "pwsh";
    public string Arguments { get; set; } = "-NoLogo -NoProfile";
    public string WorkingDirectory { get; set; } = Environment.CurrentDirectory;
}

public sealed class TerminalSshOptions
{
    public string Title { get; set; } = "SSH";
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 22;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

public sealed class TerminalTelnetOptions
{
    public string Title { get; set; } = "Telnet";
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 23;
}

public sealed class TerminalSerialOptions
{
    public string Title { get; set; } = "Serial";
    public string PortName { get; set; } = "COM1";
    public int BaudRate { get; set; } = 115200;
    public string Parity { get; set; } = "None";
    public int DataBits { get; set; } = 8;
    public string StopBits { get; set; } = "One";
    public string NewLine { get; set; } = "\r\n";
}

public sealed class TerminalBridgeInfo
{
    public string SessionId { get; init; } = string.Empty;
    public string Host { get; init; } = "127.0.0.1";
    public int Port { get; init; }
    public string Protocol { get; init; } = "tcp-raw";
    public string Message { get; init; } = string.Empty;
}

public sealed class TerminalAgentDialogueItem
{
    public string Role { get; init; } = "user";
    public string Content { get; init; } = string.Empty;
    public string Timestamp { get; init; } = DateTime.UtcNow.ToString("O");
}

public sealed class TerminalAgentPlan
{
    public bool Success { get; init; }
    public bool Executed { get; init; }
    public string Analysis { get; init; } = string.Empty;
    public string SuggestedCommand { get; init; } = string.Empty;
    public string RawResponse { get; init; } = string.Empty;
    public string Error { get; init; } = string.Empty;
    public TerminalCommandResult? ExecutionResult { get; init; }
}