using System.Text.Json.Serialization;

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
    public bool IsApiShared { get; set; } = true;
    public bool IsSshShared { get; set; } = true;
}

public sealed class TerminalSessionProfile
{
    [JsonPropertyName("profile_id")]
    public string ProfileId { get; set; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("kind")]
    public TerminalSessionKind Kind { get; set; }

    [JsonPropertyName("program")]
    public string Program { get; set; } = "pwsh";

    [JsonPropertyName("arguments")]
    public string Arguments { get; set; } = "-NoLogo -NoProfile";

    [JsonPropertyName("working_directory")]
    public string WorkingDirectory { get; set; } = Environment.CurrentDirectory;

    [JsonPropertyName("host")]
    public string Host { get; set; } = string.Empty;

    [JsonPropertyName("port")]
    public int Port { get; set; } = 22;

    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    [JsonPropertyName("password")]
    public string Password { get; set; } = string.Empty;

    [JsonPropertyName("port_name")]
    public string PortName { get; set; } = string.Empty;

    [JsonPropertyName("baud_rate")]
    public int BaudRate { get; set; } = 115200;

    [JsonPropertyName("parity")]
    public string Parity { get; set; } = "None";

    [JsonPropertyName("data_bits")]
    public int DataBits { get; set; } = 8;

    [JsonPropertyName("stop_bits")]
    public string StopBits { get; set; } = "One";

    [JsonPropertyName("new_line")]
    public string NewLine { get; set; } = "\r\n";

    [JsonPropertyName("ssh_shared")]
    public bool SshShared { get; set; } = true;

    [JsonPropertyName("api_shared")]
    public bool ApiShared { get; set; } = true;

    [JsonPropertyName("created_at")]
    public string CreatedAt { get; set; } = DateTime.UtcNow.ToString("O");

    [JsonPropertyName("last_used_at")]
    public string LastUsedAt { get; set; } = string.Empty;

    public string Descriptor => Kind switch
    {
        TerminalSessionKind.PowerShell => string.IsNullOrWhiteSpace(WorkingDirectory) ? Program : $"{Program} | {WorkingDirectory}",
        TerminalSessionKind.Ssh => $"{Username}@{Host}:{Port}",
        TerminalSessionKind.Telnet => $"{Host}:{Port}",
        TerminalSessionKind.Serial => $"{PortName} @ {BaudRate}",
        _ => Title,
    };

    public string KindLabel => Kind switch
    {
        TerminalSessionKind.PowerShell => "PowerShell",
        TerminalSessionKind.Ssh => "SSH",
        TerminalSessionKind.Telnet => "Telnet",
        TerminalSessionKind.Serial => "Serial",
        _ => Kind.ToString(),
    };
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
    public bool ApiShared { get; set; } = true;
    public bool SshShared { get; set; } = true;
}

public sealed class TerminalSshOptions
{
    public string Title { get; set; } = "SSH";
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 22;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public bool ApiShared { get; set; } = true;
    public bool SshShared { get; set; } = true;
}

public sealed class TerminalTelnetOptions
{
    public string Title { get; set; } = "Telnet";
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 23;
    public bool ApiShared { get; set; } = true;
    public bool SshShared { get; set; } = true;
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
    public bool ApiShared { get; set; } = true;
    public bool SshShared { get; set; } = true;
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