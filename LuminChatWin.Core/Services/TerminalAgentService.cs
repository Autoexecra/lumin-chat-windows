using System.Text;
using System.Text.Json;
using LuminChatWin.Core.Models;

namespace LuminChatWin.Core.Services;

public sealed class TerminalAgentService
{
    private readonly IChatCompletionClient _chatClient;
    private readonly Func<AppConfig> _configAccessor;
    private readonly TerminalSessionManager _sessionManager;

    public TerminalAgentService(IChatCompletionClient chatClient, Func<AppConfig> configAccessor, TerminalSessionManager sessionManager)
    {
        _chatClient = chatClient;
        _configAccessor = configAccessor;
        _sessionManager = sessionManager;
    }

    public async Task<TerminalAgentPlan> RunStepAsync(
        string sessionId,
        string objective,
        IReadOnlyList<TerminalAgentDialogueItem> dialogue,
        string? selectedModel = null,
        bool autoExecute = false,
        CancellationToken cancellationToken = default)
    {
        var config = _configAccessor();
        var session = _sessionManager.GetSession(sessionId) ?? throw new KeyNotFoundException($"Unknown terminal session: {sessionId}");
        var modelLevel = ResolveModelLevel(config, selectedModel);
        var prompt = BuildUserPrompt(session, objective, dialogue, _sessionManager.GetHistory(sessionId, 40), _sessionManager.GetRecentOutput(sessionId, 10000));
        var messages = new List<PersistedChatMessage>
        {
            new()
            {
                Role = "system",
                Content = "你是一个负责控制终端会话的执行代理。目标是给出下一条最合适的命令。必须保守、精确，不要虚构执行结果。只输出 JSON，格式为 {\"analysis\":string,\"command\":string,\"need_input\":bool}。如果需要用户确认或缺少信息，command 为空字符串，need_input=true。",
            },
            new()
            {
                Role = "user",
                Content = prompt,
            },
        };

        var response = await _chatClient.CompleteAsync(config, modelLevel, messages, null, cancellationToken).ConfigureAwait(false);
        if (!response.Success)
        {
            return new TerminalAgentPlan
            {
                Success = false,
                Error = response.Error,
                RawResponse = response.Content,
            };
        }

        var plan = ParsePlan(response.Content);
        if (!plan.Success || !autoExecute || string.IsNullOrWhiteSpace(plan.SuggestedCommand))
        {
            return new TerminalAgentPlan
            {
                Success = plan.Success,
                Executed = false,
                Analysis = plan.Analysis,
                SuggestedCommand = plan.SuggestedCommand,
                RawResponse = response.Content,
                Error = plan.Error,
            };
        }

        var executionResult = await _sessionManager.ExecuteCommandAsync(
            sessionId,
            plan.SuggestedCommand,
            TimeSpan.FromSeconds(Math.Max(3, config.Terminal.ExecApi.DefaultTimeoutSeconds)),
            cancellationToken).ConfigureAwait(false);
        return new TerminalAgentPlan
        {
            Success = true,
            Executed = true,
            Analysis = plan.Analysis,
            SuggestedCommand = plan.SuggestedCommand,
            RawResponse = response.Content,
            ExecutionResult = executionResult,
        };
    }

    private static int ResolveModelLevel(AppConfig config, string? selectedModel)
    {
        if (!string.IsNullOrWhiteSpace(selectedModel) &&
            selectedModel.StartsWith("level", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(selectedModel[5..], out var explicitLevel))
        {
            return explicitLevel;
        }

        return Math.Max(1, config.Terminal.Agent.DefaultModelLevel > 0 ? config.Terminal.Agent.DefaultModelLevel : config.App.DefaultModelLevel);
    }

    private static string BuildUserPrompt(
        TerminalSessionInfo session,
        string objective,
        IReadOnlyList<TerminalAgentDialogueItem> dialogue,
        IReadOnlyList<TerminalHistoryEntry> history,
        string recentOutput)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Session kind: {session.Kind}");
        builder.AppendLine($"Session title: {session.Title}");
        builder.AppendLine($"Session descriptor: {session.Descriptor}");
        builder.AppendLine();
        builder.AppendLine("Objective:");
        builder.AppendLine(string.IsNullOrWhiteSpace(objective) ? "No objective provided." : objective);
        builder.AppendLine();
        builder.AppendLine("Recent terminal output:");
        builder.AppendLine(string.IsNullOrWhiteSpace(recentOutput) ? "<empty>" : recentOutput);
        builder.AppendLine();
        builder.AppendLine("Recent command history:");
        foreach (var entry in history.TakeLast(16))
        {
            builder.AppendLine($"[{entry.Kind}] {entry.Text.Trim()}" );
        }
        builder.AppendLine();
        builder.AppendLine("User and agent dialogue:");
        foreach (var item in dialogue.TakeLast(12))
        {
            builder.AppendLine($"{item.Role}: {item.Content}");
        }
        builder.AppendLine();
        builder.AppendLine("Return only JSON.");
        return builder.ToString();
    }

    private static TerminalAgentPlan ParsePlan(string raw)
    {
        try
        {
            var json = ExtractJson(raw);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            return new TerminalAgentPlan
            {
                Success = true,
                Analysis = root.TryGetProperty("analysis", out var analysis) ? analysis.GetString() ?? string.Empty : string.Empty,
                SuggestedCommand = root.TryGetProperty("command", out var command) ? command.GetString() ?? string.Empty : string.Empty,
                RawResponse = raw,
            };
        }
        catch (Exception ex)
        {
            return new TerminalAgentPlan
            {
                Success = false,
                Error = ex.Message,
                RawResponse = raw,
            };
        }
    }

    private static string ExtractJson(string raw)
    {
        var trimmed = raw.Trim();
        if (trimmed.StartsWith("```") && trimmed.Contains('{'))
        {
            var start = trimmed.IndexOf('{');
            var end = trimmed.LastIndexOf('}');
            if (start >= 0 && end > start)
            {
                return trimmed[start..(end + 1)];
            }
        }

        return trimmed;
    }
}