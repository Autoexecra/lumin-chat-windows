using System.Text.Json.Serialization;

namespace LuminChatWin.Core.Models;

public sealed class SessionState
{
    [JsonPropertyName("session_id")]
    public string SessionId { get; set; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("created_at")]
    public string CreatedAt { get; set; } = DateTime.UtcNow.ToString("O");

    [JsonPropertyName("model_level")]
    public int ModelLevel { get; set; } = 1;

    [JsonPropertyName("approval_policy")]
    public string ApprovalPolicy { get; set; } = "auto";

    [JsonPropertyName("cwd")]
    public string Cwd { get; set; } = Environment.CurrentDirectory;

    [JsonPropertyName("messages")]
    public List<PersistedChatMessage> Messages { get; set; } = [];
}

public sealed class PersistedChatMessage
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = string.Empty;

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;

    [JsonPropertyName("tool_call_id")]
    public string? ToolCallId { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("tool_calls")]
    public List<PersistedToolCall>? ToolCalls { get; set; }
}

public sealed class PersistedToolCall
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = "function";

    [JsonPropertyName("function")]
    public PersistedToolFunction Function { get; set; } = new();
}

public sealed class PersistedToolFunction
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("arguments")]
    public string Arguments { get; set; } = "{}";
}

public sealed record ToolCall(string Id, string Name, Dictionary<string, object?> Arguments);

public sealed record ToolExecutionResult(string Name, bool Ok, string Output, Dictionary<string, object?>? Metadata = null);

public sealed class LlmResponse
{
    public bool Success { get; init; }
    public string Content { get; init; } = string.Empty;
    public string ReasoningContent { get; init; } = string.Empty;
    public List<ToolCall> ToolCalls { get; init; } = [];
    public string FinishReason { get; init; } = string.Empty;
    public Dictionary<string, object?> Usage { get; init; } = [];
    public string Error { get; init; } = string.Empty;
}

public sealed class AgentRunResult
{
    public bool Success { get; init; }
    public string Content { get; init; } = string.Empty;
    public string Error { get; init; } = string.Empty;
    public List<ToolRecord> ToolRecords { get; init; } = [];
    public string SessionId { get; init; } = string.Empty;
    public string Cwd { get; init; } = string.Empty;
}

public sealed class ToolRecord
{
    public string Name { get; init; } = string.Empty;
    public Dictionary<string, object?> Arguments { get; init; } = [];
    public bool Ok { get; init; }
    public string Output { get; init; } = string.Empty;
}

public enum AgentEventType
{
    Info,
    Warning,
    Error,
    Reasoning,
    Content,
    ToolCall,
    ToolResult,
    SessionChanged,
}

public sealed class AgentEvent
{
    public AgentEventType Type { get; init; }
    public string Message { get; init; } = string.Empty;
    public string? ToolName { get; init; }
    public Dictionary<string, object?>? Arguments { get; init; }
    public ToolExecutionResult? ToolResult { get; init; }
}

public sealed class LicenseValidationResult
{
    public bool Ok { get; init; }
    public string Message { get; init; } = string.Empty;

    public static LicenseValidationResult Success() => new() { Ok = true };
    public static LicenseValidationResult Fail(string message) => new() { Ok = false, Message = message };
}

public sealed class MemoryItem
{
    public long MemoryId { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Summary { get; init; } = string.Empty;
    public string CreatedAt { get; init; } = string.Empty;
    public double Score { get; init; }
}

public sealed class BatchTaskResult
{
    public int Index { get; init; }
    public string Task { get; init; } = string.Empty;
    public bool NewSession { get; init; }
    public string StartedAt { get; init; } = string.Empty;
    public string FinishedAt { get; init; } = string.Empty;
    public bool Success { get; init; }
    public string Content { get; init; } = string.Empty;
    public string Error { get; init; } = string.Empty;
    public List<ToolRecord> ToolRecords { get; init; } = [];
    public string SessionId { get; init; } = string.Empty;
    public string Cwd { get; init; } = string.Empty;
    public string ReportPath { get; set; } = string.Empty;
}