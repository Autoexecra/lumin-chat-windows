using System.Text;
using System.Text.Json;
using LuminChatWin.Core.Models;

namespace LuminChatWin.Core.Services;

public sealed class TerminalAgentService
{
    private static readonly HashSet<string> AllowedToolNames =
    [
        "get_environment",
        "ssh_execute_command",
        "ssh_upload_file",
        "ssh_download_file",
        "ssh_list_directory",
        "ssh_read_file",
        "ssh_write_file",
        "ssh_make_directory",
        "ssh_remove_path",
        "ssh_path_exists",
        "fetch_web_page",
        "search_web",
        "list_knowledge_documents",
        "read_knowledge_document",
        "write_knowledge_document",
    ];

    private readonly IChatCompletionClient _chatClient;
    private readonly Func<AppConfig> _configAccessor;
    private readonly TerminalSessionManager _sessionManager;
    private readonly Func<string> _workspaceRootAccessor;

    public TerminalAgentService(IChatCompletionClient chatClient, Func<AppConfig> configAccessor, TerminalSessionManager sessionManager, Func<string> workspaceRootAccessor)
    {
        _chatClient = chatClient;
        _configAccessor = configAccessor;
        _sessionManager = sessionManager;
        _workspaceRootAccessor = workspaceRootAccessor;
    }

    public async Task<TerminalAgentPlan> RunLoopAsync(
        string sessionId,
        string objective,
        IList<TerminalAgentDialogueItem> dialogue,
        string? selectedModel,
        TerminalAgentMode mode,
        IProgress<AgentEvent>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var config = _configAccessor();
        var session = _sessionManager.GetSession(sessionId) ?? throw new KeyNotFoundException($"Unknown terminal session: {sessionId}");
        var modelLevel = ResolveModelLevel(config, selectedModel);
        var maxRounds = Math.Max(1, config.App.MaxToolRounds);

        for (var round = 0; round < maxRounds; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new AgentEvent { Type = AgentEventType.Info, Message = $"第 {round + 1} 轮规划开始。" });

            var turn = await PlanTurnAsync(session, objective, dialogue, modelLevel, progress, cancellationToken).ConfigureAwait(false);
            if (!turn.Success)
            {
                return turn;
            }

            if (!string.IsNullOrWhiteSpace(turn.Analysis))
            {
                dialogue.Add(new TerminalAgentDialogueItem { Role = "assistant", Content = turn.Analysis });
            }

            if (turn.Completed || (string.IsNullOrWhiteSpace(turn.SuggestedCommand) && turn.NeedInput))
            {
                if (!string.IsNullOrWhiteSpace(turn.FinalMessage))
                {
                    dialogue.Add(new TerminalAgentDialogueItem { Role = "assistant", Content = turn.FinalMessage });
                    progress?.Report(new AgentEvent { Type = AgentEventType.Content, Message = turn.FinalMessage });
                }

                return turn;
            }

            if (string.IsNullOrWhiteSpace(turn.SuggestedCommand))
            {
                return new TerminalAgentPlan
                {
                    Success = false,
                    Analysis = turn.Analysis,
                    Error = "LLM 未给出下一条命令，也没有声明任务完成。",
                    RawResponse = turn.RawResponse,
                };
            }

            if (mode == TerminalAgentMode.Prompt)
            {
                progress?.Report(new AgentEvent { Type = AgentEventType.Info, Message = $"提示模式建议命令: {turn.SuggestedCommand}" });
                return turn;
            }

            var executionResult = await _sessionManager.ExecuteCommandAsync(
                sessionId,
                turn.SuggestedCommand,
                TimeSpan.FromSeconds(Math.Max(3, config.Terminal.ExecApi.DefaultTimeoutSeconds)),
                cancellationToken).ConfigureAwait(false);

            dialogue.Add(new TerminalAgentDialogueItem
            {
                Role = "system",
                Content = BuildExecutionNote(executionResult),
            });

            progress?.Report(new AgentEvent
            {
                Type = AgentEventType.ToolResult,
                ToolName = "terminal_command",
                Message = BuildExecutionNote(executionResult),
            });

            if (!executionResult.Success && !executionResult.TimedOut && string.IsNullOrWhiteSpace(executionResult.Output))
            {
                return new TerminalAgentPlan
                {
                    Success = false,
                    Executed = true,
                    Analysis = turn.Analysis,
                    SuggestedCommand = turn.SuggestedCommand,
                    ExecutionResult = executionResult,
                    Error = "命令执行失败，且没有返回可继续分析的输出。",
                };
            }
        }

        return new TerminalAgentPlan
        {
            Success = false,
            Error = "达到最大规划轮次，任务已停止。",
        };
    }

    private async Task<TerminalAgentPlan> PlanTurnAsync(
        TerminalSessionInfo session,
        string objective,
        IList<TerminalAgentDialogueItem> dialogue,
        int modelLevel,
        IProgress<AgentEvent>? progress,
        CancellationToken cancellationToken)
    {
        var config = _configAccessor();
        var executor = new ToolExecutor(config, _workspaceRootAccessor(), "auto");
        var messages = new List<PersistedChatMessage>
        {
            new()
            {
                Role = "system",
                Content = BuildSystemPrompt(session),
            },
            new()
            {
                Role = "user",
                Content = BuildUserPrompt(session, objective, dialogue, _sessionManager.GetRecentOutput(session.SessionId, 10000)),
            },
        };

        while (true)
        {
            var response = await _chatClient.CompleteAsync(config, modelLevel, messages, FilterDefinitions(executor.Definitions()), cancellationToken).ConfigureAwait(false);
            if (!response.Success)
            {
                return new TerminalAgentPlan
                {
                    Success = false,
                    Error = response.Error,
                    RawResponse = response.Content,
                };
            }

            if (!string.IsNullOrWhiteSpace(response.ReasoningContent))
            {
                progress?.Report(new AgentEvent { Type = AgentEventType.Reasoning, Message = response.ReasoningContent });
            }

            if (response.ToolCalls.Count == 0)
            {
                return ParsePlan(response.Content);
            }

            messages.Add(BuildAssistantToolCallMessage(response));
            foreach (var toolCall in response.ToolCalls)
            {
                progress?.Report(new AgentEvent
                {
                    Type = AgentEventType.ToolCall,
                    ToolName = toolCall.Name,
                    Arguments = toolCall.Arguments,
                    Message = JsonSerializer.Serialize(toolCall.Arguments),
                });

                var result = AllowedToolNames.Contains(toolCall.Name)
                    ? await executor.ExecuteAsync(toolCall, cancellationToken).ConfigureAwait(false)
                    : new ToolExecutionResult(toolCall.Name, false, "该工具未对终端 Agent 开放。");

                dialogue.Add(new TerminalAgentDialogueItem
                {
                    Role = "tool",
                    Content = $"{toolCall.Name}: {result.Output}",
                });

                progress?.Report(new AgentEvent
                {
                    Type = AgentEventType.ToolResult,
                    ToolName = toolCall.Name,
                    ToolResult = result,
                    Message = result.Output,
                });

                messages.Add(new PersistedChatMessage
                {
                    Role = "tool",
                    ToolCallId = toolCall.Id,
                    Name = toolCall.Name,
                    Content = result.Output,
                });
            }
        }
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

    private static string BuildSystemPrompt(TerminalSessionInfo session)
    {
        var builder = new StringBuilder();
        builder.AppendLine("你是串口终端执行代理。目标是控制当前终端会话，持续规划直到任务完成。")
            .AppendLine("你的职责是：先分析当前串口/终端输出，再决定下一条终端命令，必要时使用远程资料工具补充上下文。")
            .AppendLine()
            .AppendLine("强约束:")
            .AppendLine("- 当前终端是主要执行面，不要调用本机文件、git、本机 shell 等无关工具。")
            .AppendLine("- 只允许使用远程 SSH、网页搜索、网页抓取、知识库和环境信息工具。")
            .AppendLine("- 如果任务已经完成，必须返回 complete=true，并给出 final_message。")
            .AppendLine("- 如果需要继续在当前终端执行命令，返回 command。")
            .AppendLine("- 如果需要用户补充信息，返回 need_input=true，command 为空。")
            .AppendLine("- 不要把解释性文本放在 JSON 外面。")
            .AppendLine()
            .AppendLine("输出必须是 JSON: {\"analysis\":string,\"command\":string,\"complete\":bool,\"need_input\":bool,\"final_message\":string}")
            .AppendLine()
            .AppendLine($"目标会话: {session.Title} | {session.Kind} | {session.Descriptor}");
        return builder.ToString();
    }

    private static string BuildUserPrompt(TerminalSessionInfo session, string objective, IList<TerminalAgentDialogueItem> dialogue, string transcript)
    {
        var builder = new StringBuilder();
        builder.AppendLine("任务目标:")
            .AppendLine(string.IsNullOrWhiteSpace(objective) ? "<empty>" : objective)
            .AppendLine()
            .AppendLine("当前终端会话窗口最新内容(最多 10000 字符):")
            .AppendLine(string.IsNullOrWhiteSpace(transcript) ? "<empty>" : transcript)
            .AppendLine()
            .AppendLine("最近对话与过程记录:");

        foreach (var item in dialogue.TakeLast(20))
        {
            builder.AppendLine($"[{item.Role}] {item.Content}");
        }

        builder.AppendLine()
            .AppendLine("请基于当前终端窗口内容判断任务是否完成，并给出下一步。不要输出 JSON 以外内容。");
        return builder.ToString();
    }

    private static IReadOnlyList<Dictionary<string, object?>> FilterDefinitions(IReadOnlyList<Dictionary<string, object?>> definitions)
    {
        return definitions
            .Where(definition =>
            {
                if (!definition.TryGetValue("function", out var functionObject) || functionObject is not Dictionary<string, object?> function)
                {
                    return false;
                }

                return function.TryGetValue("name", out var nameObject) && nameObject is string name && AllowedToolNames.Contains(name);
            })
            .ToList();
    }

    private static PersistedChatMessage BuildAssistantToolCallMessage(LlmResponse response)
    {
        return new PersistedChatMessage
        {
            Role = "assistant",
            Content = response.Content,
            ToolCalls = response.ToolCalls.Select(call => new PersistedToolCall
            {
                Id = call.Id,
                Type = "function",
                Function = new PersistedToolFunction
                {
                    Name = call.Name,
                    Arguments = JsonSerializer.Serialize(call.Arguments),
                },
            }).ToList(),
        };
    }

    private static string BuildExecutionNote(TerminalCommandResult result)
    {
        var output = string.IsNullOrWhiteSpace(result.Output) ? "<empty>" : result.Output;
        return $"执行命令: {result.Command}\n成功: {result.Success}\n超时: {result.TimedOut}\n输出:\n{output}";
    }

    private static TerminalAgentPlan ParsePlan(string raw)
    {
        try
        {
            var json = ExtractJson(raw);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var finalMessage = root.TryGetProperty("final_message", out var finalMessageElement) ? finalMessageElement.GetString() ?? string.Empty : string.Empty;
            var complete = root.TryGetProperty("complete", out var completeElement) && completeElement.ValueKind == JsonValueKind.True;
            var needInput = root.TryGetProperty("need_input", out var needInputElement) && needInputElement.ValueKind == JsonValueKind.True;
            return new TerminalAgentPlan
            {
                Success = true,
                Completed = complete,
                NeedInput = needInput,
                Analysis = root.TryGetProperty("analysis", out var analysis) ? analysis.GetString() ?? string.Empty : string.Empty,
                SuggestedCommand = root.TryGetProperty("command", out var command) ? command.GetString() ?? string.Empty : string.Empty,
                FinalMessage = finalMessage,
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