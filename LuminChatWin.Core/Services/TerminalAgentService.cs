using System.Text;
using System.Text.Json;
using LuminChatWin.Core.Models;

namespace LuminChatWin.Core.Services;

public sealed class TerminalAgentService
{
    private static readonly HashSet<string> AllowedToolNames =
    [
        "run_shell_command",
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

            var turn = await PlanTurnAsync(session, objective, dialogue, modelLevel, mode, executor, progress, cancellationToken).ConfigureAwait(false);
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
        TerminalAgentMode mode,
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
            var reasoningStreamStarted = false;
            var contentStreamStarted = false;
            var response = await _chatClient.CompleteStreamingAsync(
                config,
                modelLevel,
                messages,
                BuildTerminalDefinitions(executor.Definitions()),
                onReasoningChunk: chunk =>
                {
                    progress?.Report(new AgentEvent
                    {
                        Type = AgentEventType.Reasoning,
                        Message = reasoningStreamStarted ? chunk : $"thinking: {chunk}",
                        AppendToPrevious = reasoningStreamStarted,
                    });
                    reasoningStreamStarted = true;
                },
                onContentChunk: chunk =>
                {
                    progress?.Report(new AgentEvent
                    {
                        Type = AgentEventType.Content,
                        Message = contentStreamStarted ? chunk : $"content: {chunk}",
                        AppendToPrevious = contentStreamStarted,
                    });
                    contentStreamStarted = true;
                },
                cancellationToken).ConfigureAwait(false);
            if (!response.Success)
            {
                return new TerminalAgentPlan
                {
                    Success = false,
                    Error = response.Error,
                    RawResponse = response.Content,
                };
            }

            var contentPlan = ParsePlan(response.Content, allowEmptyContent: response.ToolCalls.Count > 0);
            if (!contentPlan.Success)
            {
                return contentPlan;
            }

            if (response.ToolCalls.Count == 0)
            {
                return contentPlan;
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

                if (!AllowedToolNames.Contains(toolCall.Name))
                {
                    var blocked = new ToolExecutionResult(toolCall.Name, false, "该工具未对终端 Agent 开放。");
                    progress?.Report(new AgentEvent
                    {
                        Type = AgentEventType.ToolResult,
                        ToolName = toolCall.Name,
                        ToolResult = blocked,
                        Message = blocked.Output,
                    });

                    messages.Add(new PersistedChatMessage
                    {
                        Role = "tool",
                        ToolCallId = toolCall.Id,
                        Name = toolCall.Name,
                        Content = blocked.Output,
                    });
                    continue;
                }

                // In prompt mode, run_shell_command becomes a suggestion instead of executing immediately.
                if (string.Equals(toolCall.Name, "run_shell_command", StringComparison.Ordinal))
                {
                    var command = GetRequiredString(toolCall.Arguments, "command");
                    if (mode == TerminalAgentMode.Prompt)
                    {
                        return new TerminalAgentPlan
                        {
                            Success = contentPlan.Success,
                            Completed = contentPlan.Completed,
                            Analysis = contentPlan.Analysis,
                            SuggestedCommand = command,
                            NeedInput = false,
                            FinalMessage = contentPlan.FinalMessage,
                            RawResponse = response.Content,
                        };
                    }

                    var commandResult = await ExecuteTerminalCommandToolAsync(session.SessionId, toolCall.Arguments, config, cancellationToken).ConfigureAwait(false);
                    progress?.Report(new AgentEvent
                    {
                        Type = AgentEventType.ToolResult,
                        ToolName = toolCall.Name,
                        ToolResult = commandResult,
                        Message = commandResult.Output,
                    });

                    messages.Add(new PersistedChatMessage
                    {
                        Role = "tool",
                        ToolCallId = toolCall.Id,
                        Name = toolCall.Name,
                        Content = commandResult.Output,
                    });
                    continue;
                }

                var result = await executor.ExecuteAsync(toolCall, cancellationToken).ConfigureAwait(false);

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
            .AppendLine("你的职责是：根据用户提出的执行需求。先分析当前终端窗口内容，再决定下一条终端命令，生成的指令会放到当前会话窗口中执行，当前会话窗口可能是串口终端或者ssh终端。执行前会调用资料库或者网页搜索相关资料。")
            .AppendLine()
            .AppendLine("强约束:")
            .AppendLine("- 当前终端是主要执行面，不要调用本机文件、git、本机 shell 等无关工具。")
            .AppendLine("- 只允许使用以下工具: run_shell_command、ssh_execute_command、ssh_list_directory、ssh_read_file、ssh_write_file、fetch_web_page、search_web、list_knowledge_documents、read_knowledge_document、write_knowledge_document。")
            .AppendLine("- run_shell_command 不是在本机 PowerShell 执行，而是把 command 放到当前终端会话窗口中执行。")
            .AppendLine("- run_shell_command 默认超时是 120 秒；遇到下载、联网、主板硬件探测等可能阻塞较久的任务时，可把 timeout_seconds 提高到 1800 秒。")
            .AppendLine("- thinking 通过流式 reasoning 输出，content 通过流式正文输出。不要把 thinking 写进 content JSON。")
            .AppendLine("- 不要在正文里输出 thinking、content、tool_calls 这三个字面标签；thinking 走 reasoning 通道，content 只输出 JSON，tool_calls 只走工具调用字段。")
            .AppendLine("- content 必须始终是 JSON，并且只能包含这些字段: {\"complete\":bool,\"analysis\":string,\"final_message\":string}。")
            .AppendLine("- 如果任务已经完成，返回 complete=true，并给出 final_message，不要再返回 tool_calls。")
            .AppendLine("- 如果任务还没完成，不要在 JSON 里返回 command；需要执行命令时调用 run_shell_command。")
            .AppendLine("- 如果需要继续查资料或远程取证，也通过 tool_calls 返回。")
            .AppendLine("- 不要把解释性文本放在 JSON 外面。")
            .AppendLine("- 禁止使用交互式的指令，比如 vim、nano、less、more 等。dnf install 必须加上 -y 参数。docker指令使用docker exec my_container sh -c '指令' 而不是 docker exec -it进入容器内。并且输出不能分页。")
            .AppendLine("- 输出尽量使用grep、awk、sed等工具过滤和处理，避免输出过多无关信息。")
            .AppendLine("- 如果资料库启用且可用，先调用 list_knowledge_documents 列出资料，再挑选最相关的文件调用 read_knowledge_document 读取。")
            .AppendLine("- 已读取的资料内容会在当前会话的后续轮次继续发送给模型，因此不要重复读取相同资料，除非确有必要。")
            .AppendLine("- 如果当前信息不足且无法继续，请返回 complete=false，并在 final_message 中明确说明需要用户补充什么。")
            .AppendLine("输出协议固定为 three-part: thinking, content, tool_calls。其中 thinking 可为空，content 必须是上述 JSON，tool_calls 只在未完成时返回。")
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

    private static IReadOnlyList<Dictionary<string, object?>> BuildTerminalDefinitions(IReadOnlyList<Dictionary<string, object?>> definitions)
    {
        var filtered = definitions
            .Where(definition =>
            {
                if (!definition.TryGetValue("function", out var functionObject) || functionObject is not Dictionary<string, object?> function)
                {
                    return false;
                }

                return function.TryGetValue("name", out var nameObject) && nameObject is string name && AllowedToolNames.Contains(name);
            })
            .Select(CloneDefinition)
            .ToList();

        var runShellDefinition = filtered.FirstOrDefault(definition =>
            definition.TryGetValue("function", out var functionObject) &&
            functionObject is Dictionary<string, object?> function &&
            function.TryGetValue("name", out var nameObject) &&
            string.Equals(nameObject?.ToString(), "run_shell_command", StringComparison.Ordinal));

        if (runShellDefinition is not null &&
            runShellDefinition["function"] is Dictionary<string, object?> runShellFunction &&
            runShellFunction["parameters"] is Dictionary<string, object?> parameters &&
            parameters["properties"] is Dictionary<string, object?> properties)
        {
            // The terminal agent only needs command text and timeout because execution always targets the selected terminal session.
            runShellFunction["description"] = "Send a command to the current terminal session window for execution inside the app. Default timeout is 120 seconds and can be extended to 1800 seconds for long-running operations.";
            properties.Remove("cwd");
        }

        return filtered;
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

    private static TerminalAgentPlan ParsePlan(string raw, bool allowEmptyContent = false)
    {
        try
        {
            if (allowEmptyContent && string.IsNullOrWhiteSpace(raw))
            {
                return new TerminalAgentPlan
                {
                    Success = true,
                    Completed = false,
                    NeedInput = false,
                    RawResponse = raw,
                };
            }

            var json = ExtractJson(raw);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var complete = root.TryGetProperty("complete", out var completeElement) && completeElement.ValueKind == JsonValueKind.True;
            var needInput = root.TryGetProperty("need_input", out var needInputElement) && needInputElement.ValueKind == JsonValueKind.True;
            var command = root.TryGetProperty("command", out var commandElement) ? commandElement.GetString() ?? string.Empty : string.Empty;
            var finalMessage = root.TryGetProperty("final_message", out var finalMessageElement) ? finalMessageElement.GetString() ?? string.Empty : string.Empty;
            return new TerminalAgentPlan
            {
                Success = true,
                Completed = complete,
                NeedInput = needInput || (!complete && string.IsNullOrWhiteSpace(command) && string.IsNullOrWhiteSpace(finalMessage)),
                Analysis = root.TryGetProperty("analysis", out var analysis) ? analysis.GetString() ?? string.Empty : string.Empty,
                SuggestedCommand = command,
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

    private static string BuildExecutionNote(TerminalCommandResult result)
    {
        var output = string.IsNullOrWhiteSpace(result.Output) ? "<empty>" : result.Output;
        return $"执行命令: {result.Command}\n成功: {result.Success}\n超时: {result.TimedOut}\n输出:\n{output}";
    }

    private async Task<ToolExecutionResult> ExecuteTerminalCommandToolAsync(string sessionId, IReadOnlyDictionary<string, object?> arguments, AppConfig config, CancellationToken cancellationToken)
    {
        var command = GetRequiredString(arguments, "command");
        var timeoutSeconds = GetInt(arguments, "timeout_seconds", Math.Max(3, (int)Math.Ceiling(config.Terminal.ExecApi.DefaultTimeoutSeconds)));

        // Route run_shell_command into the active terminal session instead of the local PowerShell workspace.
        var result = await _sessionManager.ExecuteCommandAsync(
            sessionId,
            command,
            TimeSpan.FromSeconds(Math.Max(3, timeoutSeconds)),
            cancellationToken).ConfigureAwait(false);

        return new ToolExecutionResult("run_shell_command", result.Success, JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["command"] = result.Command,
            ["success"] = result.Success,
            ["timed_out"] = result.TimedOut,
            ["output"] = result.Output,
            ["error"] = result.Error,
        }));
    }

    private static string GetRequiredString(IReadOnlyDictionary<string, object?> arguments, string key)
    {
        if (!arguments.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value?.ToString()))
        {
            throw new InvalidOperationException($"缺少必填参数: {key}");
        }

        return value!.ToString()!;
    }

    private static int GetInt(IReadOnlyDictionary<string, object?> arguments, string key, int defaultValue)
    {
        if (!arguments.TryGetValue(key, out var value) || value is null)
        {
            return defaultValue;
        }

        return value switch
        {
            int intValue => intValue,
            long longValue => (int)longValue,
            JsonElement { ValueKind: JsonValueKind.Number } jsonNumber when jsonNumber.TryGetInt32(out var parsed) => parsed,
            _ when int.TryParse(value.ToString(), out var parsed) => parsed,
            _ => defaultValue,
        };
    }

    private static Dictionary<string, object?> CloneDefinition(Dictionary<string, object?> definition)
    {
        var clone = new Dictionary<string, object?>(definition.Count, StringComparer.Ordinal);
        foreach (var pair in definition)
        {
            clone[pair.Key] = pair.Value switch
            {
                Dictionary<string, object?> dictionary => CloneDefinition(dictionary),
                List<object?> list => list.ToList(),
                _ => pair.Value,
            };
        }

        return clone;
    }
}