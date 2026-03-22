using System.Text;
using System.Text.Json;
using LuminChatWin.Core.Models;

namespace LuminChatWin.Core.Services;

public sealed class TerminalAgentService
{
    private static readonly HashSet<string> AllowedToolNames =
    [
        "ssh_execute_command",
        "ssh_list_directory",
        "ssh_read_file",
        "ssh_write_file",
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
        var executor = new ToolExecutor(config, _workspaceRootAccessor(), "auto");

        for (var round = 0; round < maxRounds; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new AgentEvent { Type = AgentEventType.Info, Message = $"第 {round + 1} 轮规划开始。" });

            var turn = await PlanTurnAsync(session, objective, dialogue, modelLevel, executor, progress, cancellationToken).ConfigureAwait(false);
            if (!turn.Success)
            {
                return turn;
            }

            if (!string.IsNullOrWhiteSpace(turn.Analysis))
            {
                progress?.Report(new AgentEvent { Type = AgentEventType.Content, Message = turn.Analysis });
            }

            if (turn.Completed || (string.IsNullOrWhiteSpace(turn.SuggestedCommand) && turn.NeedInput))
            {
                if (!string.IsNullOrWhiteSpace(turn.FinalMessage))
                {
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

    public async Task<TerminalAgentPlan> RunStepAsync(
        string sessionId,
        string objective,
        IReadOnlyList<TerminalAgentDialogueItem> dialogue,
        string? selectedModel = null,
        bool autoExecute = false,
        CancellationToken cancellationToken = default)
    {
        var mutableDialogue = dialogue.ToList();
        return await RunLoopAsync(
            sessionId,
            objective,
            mutableDialogue,
            selectedModel,
            autoExecute ? TerminalAgentMode.Auto : TerminalAgentMode.Prompt,
            progress: null,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<TerminalAgentPlan> PlanTurnAsync(
        TerminalSessionInfo session,
        string objective,
        IList<TerminalAgentDialogueItem> dialogue,
        int modelLevel,
        ToolExecutor executor,
        IProgress<AgentEvent>? progress,
        CancellationToken cancellationToken)
    {
        var config = _configAccessor();
        var messages = new List<PersistedChatMessage>
        {
            new()
            {
                Role = "system",
                Content = BuildSystemPrompt(config, session),
            },
            new()
            {
                Role = "user",
                Content = BuildUserPrompt(session, objective, _sessionManager.GetRecentOutput(session.SessionId, 10000)),
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

    private static string BuildSystemPrompt(AppConfig config, TerminalSessionInfo session)
    {
        var builder = new StringBuilder();
        builder.AppendLine("你是串口终端执行代理。目标是控制当前终端会话，持续规划直到任务完成。")
            .AppendLine("你的职责是：先分析当前终端窗口内容，再决定下一条终端命令，会生成的指令是放到串口终端或者 ssh 终端去执行的。必要时使用远程资料工具补充上下文。")
            .AppendLine()
            .AppendLine("强约束:")
            .AppendLine("- 当前终端是主要执行面，不要调用本机文件、git、本机 shell 等无关工具。")
            .AppendLine("- 只允许使用以下工具: ssh_execute_command、ssh_list_directory、ssh_read_file、ssh_write_file、fetch_web_page、search_web、list_knowledge_documents、read_knowledge_document、write_knowledge_document。")
            .AppendLine("- 如果任务已经完成，必须返回 complete=true，并给出 final_message。")
            .AppendLine("- 如果需要继续在当前终端执行命令，返回 command。")
            .AppendLine("- 不要把解释性文本放在 JSON 外面。")
            .AppendLine("- 禁止使用交互式的指令，比如 vim、nano、less、more 等。dnf install 必须加上 -y 参数。docker指令使用docker exec my_container sh -c '指令' 而不是 docker exec -it进入容器内。并且输出不能分页。")
            .AppendLine("- 输出尽量使用grep、awk、sed等工具过滤和处理，避免输出过多无关信息。")
            .AppendLine("- 如果资料库启用且可用，先调用 list_knowledge_documents 列出资料，再挑选最相关的文件调用 read_knowledge_document 读取。")
            .AppendLine("- 已读取的资料内容会在当前会话的后续轮次继续发送给模型，因此不要重复读取相同资料，除非确有必要。")
            .AppendLine("输出必须是 JSON: {\"analysis\":string,\"command\":string,\"complete\":bool,\"need_input\":bool,\"final_message\":string}")
            .AppendLine();

        if (config.SecondaryServer.Enabled)
        {
            builder.AppendLine("辅助服务器:")
                .AppendLine($"- 主机: {config.SecondaryServer.Host}:{config.SecondaryServer.Port}")
                .AppendLine($"- 用户: {config.SecondaryServer.User}")
                .AppendLine();
        }

        if (config.KnowledgeBase.Enabled)
        {
            builder.AppendLine("资料库:")
                .AppendLine($"- 远端主机: {config.KnowledgeBase.Host}:{config.KnowledgeBase.Port}")
                .AppendLine($"- 远端根目录: {config.KnowledgeBase.RootDir}")
                .AppendLine($"- 本地缓存目录: {ConfigService.ExpandPath(config.KnowledgeBase.LocalCacheDir)}")
                .AppendLine();
        }

        return builder.ToString();
    }

    private static string BuildUserPrompt(TerminalSessionInfo session, string objective, string transcript)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Session kind: {session.Kind}");
        builder.AppendLine($"Session title: {session.Title}");
        builder.AppendLine($"Session descriptor: {session.Descriptor}");
        builder.AppendLine();
        builder.AppendLine("Objective:");
        builder.AppendLine(string.IsNullOrWhiteSpace(objective) ? "No objective provided." : objective);
        builder.AppendLine();
        builder.AppendLine("Current terminal window content (latest 10000 chars):");
        builder.AppendLine(string.IsNullOrWhiteSpace(transcript) ? "<empty>" : transcript);

        builder.AppendLine();
        builder.AppendLine("Return only JSON.");
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
                Completed = root.TryGetProperty("complete", out var completeElement) && completeElement.ValueKind == JsonValueKind.True,
                NeedInput = root.TryGetProperty("need_input", out var needInputElement) && needInputElement.ValueKind == JsonValueKind.True,
                Analysis = root.TryGetProperty("analysis", out var analysis) ? analysis.GetString() ?? string.Empty : string.Empty,
                SuggestedCommand = root.TryGetProperty("command", out var command) ? command.GetString() ?? string.Empty : string.Empty,
                FinalMessage = root.TryGetProperty("final_message", out var finalMessage) ? finalMessage.GetString() ?? string.Empty : string.Empty,
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

    private static string BuildExecutionNote(TerminalCommandResult result)
    {
        var output = string.IsNullOrWhiteSpace(result.Output) ? "<empty>" : result.Output;
        return $"执行命令: {result.Command}\n成功: {result.Success}\n超时: {result.TimedOut}\n输出:\n{output}";
    }
}