using System.Text.Json.Serialization;

namespace LuminChatWin.Core.Models;

public sealed class AppConfig
{
    [JsonPropertyName("app")]
    public AppSection App { get; set; } = new();

    [JsonPropertyName("command_policy")]
    public CommandPolicyConfig CommandPolicy { get; set; } = new();

    [JsonPropertyName("secondary_server")]
    public RemoteServerConfig SecondaryServer { get; set; } = new();

    [JsonPropertyName("model_escalation")]
    public ModelEscalationConfig ModelEscalation { get; set; } = new();

    [JsonPropertyName("knowledge_base")]
    public KnowledgeBaseConfig KnowledgeBase { get; set; } = new();

    [JsonPropertyName("license")]
    public LicenseConfig License { get; set; } = new();

    [JsonPropertyName("deploy")]
    public DeployConfig Deploy { get; set; } = new();

    [JsonPropertyName("build_server")]
    public BuildServerConfig BuildServer { get; set; } = new();

    [JsonPropertyName("log")]
    public LogConfig Log { get; set; } = new();

    [JsonPropertyName("ai")]
    public Dictionary<string, AiModelConfig> Ai { get; set; } = CreateDefaultAi();

    public static AppConfig CreateDefault() => new();

    public int GetMaxModelLevel() => Ai.Keys
        .Select(static key => key.StartsWith("level", StringComparison.OrdinalIgnoreCase) && int.TryParse(key[5..], out var level) ? level : 0)
        .DefaultIfEmpty(0)
        .Max();

    public AiModelConfig GetModel(int level)
    {
        if (!Ai.TryGetValue($"level{level}", out var model))
        {
            throw new InvalidOperationException($"未找到模型级别 level{level}。");
        }

        return model;
    }

    private static Dictionary<string, AiModelConfig> CreateDefaultAi()
    {
        return new(StringComparer.OrdinalIgnoreCase)
        {
            ["level1"] = new AiModelConfig
            {
                Name = "qwen3-235b-a22b-thinking-2507",
                Provider = "iflow",
                Model = "qwen3-235b-a22b-thinking-2507",
                BaseUrl = "https://apis.iflow.cn/v1",
                ApiKeyEnv = "LUMIN_CHAT_LEVEL1_API_KEY",
                Temperature = 0,
                MaxTokens = 131072,
                EnableThinking = true,
            },
            ["level2"] = new AiModelConfig
            {
                Name = "qwen3-235b-a22b-instruct",
                Provider = "iflow",
                Model = "qwen3-235b-a22b-instruct",
                BaseUrl = "https://apis.iflow.cn/v1",
                ApiKeyEnv = "LUMIN_CHAT_LEVEL2_API_KEY",
                Temperature = 0,
                MaxTokens = 131072,
                EnableThinking = true,
            },
            ["level3"] = new AiModelConfig
            {
                Name = "DeepSeek-V3",
                Provider = "siliconflow",
                Model = "Pro/deepseek-ai/DeepSeek-V3.2",
                BaseUrl = "https://api.siliconflow.cn/v1",
                ApiKeyEnv = "LUMIN_CHAT_LEVEL3_API_KEY",
                Temperature = 0.1,
                MaxTokens = 163840,
                EnableThinking = true,
            },
            ["level4"] = new AiModelConfig
            {
                Name = "DeepSeek-V3",
                Provider = "siliconflow",
                Model = "Pro/deepseek-ai/DeepSeek-V3.2",
                BaseUrl = "https://api.siliconflow.cn/v1",
                ApiKeyEnv = "LUMIN_CHAT_LEVEL4_API_KEY",
                Temperature = 0,
                MaxTokens = 163840,
                EnableThinking = true,
            },
            ["level5"] = new AiModelConfig
            {
                Name = "Qwen3-8B",
                Provider = "siliconflow",
                Model = "Qwen/Qwen3-8B",
                BaseUrl = "https://api.siliconflow.cn/v1",
                ApiKeyEnv = "LUMIN_CHAT_LEVEL5_API_KEY",
                Temperature = 0.1,
                MaxTokens = 131072,
                EnableThinking = true,
            },
        };
    }
}

public sealed class AppSection
{
    [JsonPropertyName("default_model_level")]
    public int DefaultModelLevel { get; set; } = 1;

    [JsonPropertyName("default_approval_policy")]
    public string DefaultApprovalPolicy { get; set; } = "auto";

    [JsonPropertyName("max_tool_rounds")]
    public int MaxToolRounds { get; set; } = 8;

    [JsonPropertyName("show_thinking")]
    public bool ShowThinking { get; set; } = true;

    [JsonPropertyName("session_dir")]
    public string SessionDir { get; set; } = "~/.lumin-chat-win/sessions";

    [JsonPropertyName("memory_dir")]
    public string MemoryDir { get; set; } = "~/.lumin-chat-win/memory";

    [JsonPropertyName("report_dir")]
    public string ReportDir { get; set; } = "~/lumin-chat-win-reports";

    [JsonPropertyName("memory_recall_limit")]
    public int MemoryRecallLimit { get; set; } = 5;

    [JsonPropertyName("memory_max_chars")]
    public int MemoryMaxChars { get; set; } = 1600;

    [JsonPropertyName("workspace_context_enabled")]
    public bool WorkspaceContextEnabled { get; set; } = true;

    [JsonPropertyName("workspace_context_max_depth")]
    public int WorkspaceContextMaxDepth { get; set; } = 2;

    [JsonPropertyName("workspace_context_max_entries")]
    public int WorkspaceContextMaxEntries { get; set; } = 40;
}

public sealed class CommandPolicyConfig
{
    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "blacklist";

    [JsonPropertyName("blacklist")]
    public List<string> Blacklist { get; set; } =
    [
        "rm -rf /",
        "mkfs",
        "dd if=",
        "shutdown",
        "reboot",
        "poweroff",
        "halt",
        "init 0",
        "init 6",
        "systemctl poweroff",
        "systemctl reboot",
        "chmod -R 777 /",
        "chown -R root /",
        ":(){ :|:& };:",
    ];

    [JsonPropertyName("whitelist")]
    public List<string> Whitelist { get; set; } =
    [
        "Get-ChildItem",
        "Get-Content",
        "Select-String",
        "git",
        "python",
        "dotnet",
        "cmd",
        "powershell",
        "pwsh",
        "ssh",
        "scp",
        "mkdir",
        "copy",
        "move",
        "type",
        "dir",
    ];

    [JsonPropertyName("extension_rules")]
    public List<string> ExtensionRules { get; set; } = [];
}

public sealed class RemoteServerConfig
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("host")]
    public string Host { get; set; } = string.Empty;

    [JsonPropertyName("port")]
    public int Port { get; set; } = 22;

    [JsonPropertyName("user")]
    public string User { get; set; } = string.Empty;

    [JsonPropertyName("password")]
    public string Password { get; set; } = string.Empty;
}

public sealed class ModelEscalationConfig
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("repeat_command_threshold")]
    public int RepeatCommandThreshold { get; set; } = 3;

    [JsonPropertyName("consecutive_error_threshold")]
    public int ConsecutiveErrorThreshold { get; set; } = 4;

    [JsonPropertyName("upgrade_on_llm_error")]
    public bool UpgradeOnLlmError { get; set; } = true;

    [JsonPropertyName("empty_response_retry_limit")]
    public int EmptyResponseRetryLimit { get; set; } = 2;
}

public sealed class KnowledgeBaseConfig
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("host")]
    public string Host { get; set; } = string.Empty;

    [JsonPropertyName("port")]
    public int Port { get; set; } = 22;

    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    [JsonPropertyName("password")]
    public string Password { get; set; } = string.Empty;

    [JsonPropertyName("root_dir")]
    public string RootDir { get; set; } = string.Empty;

    [JsonPropertyName("patterns")]
    public List<string> Patterns { get; set; } = ["*.md", "*.txt"];
}

public sealed class LicenseConfig
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("subject")]
    public string Subject { get; set; } = "lumin-chat-win";

    [JsonPropertyName("license_file")]
    public string LicenseFile { get; set; } = "~/.lumin-chat-win/license.json";

    [JsonPropertyName("secret_env")]
    public string SecretEnv { get; set; } = "LUMIN_CHAT_LICENSE_SECRET";

    [JsonPropertyName("secret")]
    public string Secret { get; set; } = string.Empty;
}

public sealed class DeployConfig
{
    [JsonPropertyName("host")]
    public string Host { get; set; } = string.Empty;

    [JsonPropertyName("port")]
    public int Port { get; set; } = 22;

    [JsonPropertyName("user")]
    public string User { get; set; } = string.Empty;

    [JsonPropertyName("remote_dir")]
    public string RemoteDir { get; set; } = "/root/lumin-chat";
}

public sealed class BuildServerConfig
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("host")]
    public string Host { get; set; } = string.Empty;

    [JsonPropertyName("port")]
    public int Port { get; set; } = 22;

    [JsonPropertyName("user")]
    public string User { get; set; } = "root";

    [JsonPropertyName("password")]
    public string Password { get; set; } = string.Empty;

    [JsonPropertyName("remote_dir")]
    public string RemoteDir { get; set; } = "/root/lumin-chat-build";
}

public sealed class LogConfig
{
    [JsonPropertyName("debug_mode")]
    public DebugModeConfig DebugMode { get; set; } = new();
}

public sealed class DebugModeConfig
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("show_llm_prompts")]
    public bool ShowLlmPrompts { get; set; }

    [JsonPropertyName("show_llm_responses")]
    public bool ShowLlmResponses { get; set; }
}

public sealed class AiModelConfig
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("provider")]
    public string Provider { get; set; } = string.Empty;

    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("base_url")]
    public string BaseUrl { get; set; } = string.Empty;

    [JsonPropertyName("api_key")]
    public string ApiKey { get; set; } = string.Empty;

    [JsonPropertyName("api_key_env")]
    public string ApiKeyEnv { get; set; } = string.Empty;

    [JsonPropertyName("temperature")]
    public double Temperature { get; set; } = 0.1;

    [JsonPropertyName("max_tokens")]
    public int MaxTokens { get; set; } = 8192;

    [JsonPropertyName("enable_thinking")]
    public bool EnableThinking { get; set; } = true;
}