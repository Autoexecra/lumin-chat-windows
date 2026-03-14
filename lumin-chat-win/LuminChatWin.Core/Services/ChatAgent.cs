using System.Runtime.InteropServices;
using System.Text.Json;
using LuminChatWin.Core.Models;

namespace LuminChatWin.Core.Services;

public sealed class ChatAgent
{
    private readonly AppConfig _config;
    private readonly SessionStore _sessionStore;
    private readonly MemoryStore _memoryStore;
    private readonly IChatCompletionClient _chatClient;
    private readonly Func<string, string, bool>? _confirmCallback;
    private ToolExecutor _toolExecutor;
    private int _repeatedToolCount;
    private int _consecutiveToolFailures;
    private int _consecutiveEmptyResponses;
    private string _lastToolSignature = string.Empty;
    private string _systemPrompt;

    public ChatAgent(
        AppConfig config,
        string workdir,
        IChatCompletionClient? chatClient = null,
        Func<string, string, bool>? confirmCallback = null,
        string? sessionIdOrPath = null)
    {
        _config = config;
        _confirmCallback = confirmCallback;
        var baseDir = ConfigService.ExpandPath(workdir);
        var sessionDir = ConfigService.ExpandPath(config.App.SessionDir);
        var memoryDir = ConfigService.ExpandPath(config.App.MemoryDir);
        _sessionStore = new SessionStore(sessionDir);
        _memoryStore = new MemoryStore(memoryDir);
        _chatClient = chatClient ?? new OpenAiCompatibleChatClient();
        ModelLevel = config.App.DefaultModelLevel;
        _systemPrompt = SystemPromptBuilder.Build(_config, ModelLevel, _config.GetMaxModelLevel());

        if (!string.IsNullOrWhiteSpace(sessionIdOrPath))
        {
            CurrentSession = _sessionStore.Load(sessionIdOrPath);
            ModelLevel = CurrentSession.ModelLevel;
        }
        else
        {
            CurrentSession = _sessionStore.Create(ModelLevel, config.App.DefaultApprovalPolicy, baseDir, _systemPrompt);
        }

        _memoryStore.EnsureSession(CurrentSession.SessionId, CurrentSession.CreatedAt);
        _toolExecutor = new ToolExecutor(_config, CurrentSession.Cwd, CurrentSession.ApprovalPolicy, _confirmCallback);
    }

    public SessionState CurrentSession { get; private set; }

    public int ModelLevel { get; private set; }

    public string Cwd => _toolExecutor.Cwd;

    public string DescribeModel()
    {
        return _config.GetModel(ModelLevel).Name;
    }

    public string CreateNewSession()
    {
        CurrentSession = _sessionStore.Create(ModelLevel, CurrentSession.ApprovalPolicy, Cwd, _systemPrompt);
        _memoryStore.EnsureSession(CurrentSession.SessionId, CurrentSession.CreatedAt);
        _toolExecutor = new ToolExecutor(_config, CurrentSession.Cwd, CurrentSession.ApprovalPolicy, _confirmCallback);
        ResetCounters();
        return CurrentSession.SessionId;
    }

    public IReadOnlyList<Dictionary<string, string>> ListSessions(int limit = 20) => _sessionStore.ListSessions(limit);

    public string SwitchSession(string sessionIdOrPath)
    {
        CurrentSession = _sessionStore.Load(sessionIdOrPath);
        ModelLevel = CurrentSession.ModelLevel;
        _systemPrompt = SystemPromptBuilder.Build(_config, ModelLevel, _config.GetMaxModelLevel());
        EnsureSystemPrompt();
        _memoryStore.EnsureSession(CurrentSession.SessionId, CurrentSession.CreatedAt);
        _toolExecutor = new ToolExecutor(_config, CurrentSession.Cwd, CurrentSession.ApprovalPolicy, _confirmCallback);
        ResetCounters();
        SaveSession();
        return CurrentSession.SessionId;
    }

    public void SetModelLevel(int modelLevel)
    {
        ModelLevel = Math.Clamp(modelLevel, 1, _config.GetMaxModelLevel());
        CurrentSession.ModelLevel = ModelLevel;
        _systemPrompt = SystemPromptBuilder.Build(_config, ModelLevel, _config.GetMaxModelLevel());
        EnsureSystemPrompt();
        SaveSession();
    }

    public void SetApprovalPolicy(string approvalPolicy)
    {
        CurrentSession.ApprovalPolicy = approvalPolicy;
        _toolExecutor.SetApprovalPolicy(approvalPolicy);
        SaveSession();
    }

    public void SetCommandPolicyMode(string mode)
    {
        _toolExecutor.SetCommandPolicyMode(mode);
        _systemPrompt = SystemPromptBuilder.Build(_config, ModelLevel, _config.GetMaxModelLevel());
        EnsureSystemPrompt();
        SaveSession();
    }

    public string ChangeDirectory(string path)
    {
        var result = _toolExecutor.ChangeDirectory(path);
        CurrentSession.Cwd = _toolExecutor.Cwd;
        SaveSession();
        return result.Output;
    }

    public string MemorySummary(string query = "")
    {
        var text = _memoryStore.BuildContext(CurrentSession.SessionId, string.IsNullOrWhiteSpace(query) ? "最近的长期记忆" : query, _config.App.MemoryRecallLimit, _config.App.MemoryMaxChars);
        return string.IsNullOrWhiteSpace(text) ? "当前会话还没有沉淀长期记忆。" : text;
    }

    public string WorkspaceOverview() => _toolExecutor.BuildWorkspaceContext(_config.App.WorkspaceContextMaxDepth, _config.App.WorkspaceContextMaxEntries);

    public async Task<AgentRunResult> RunWithTraceAsync(string userInput, IProgress<AgentEvent>? progress = null, CancellationToken cancellationToken = default)
    {
        EnsureSystemPrompt();
        CurrentSession.Messages.Add(new PersistedChatMessage { Role = "user", Content = userInput });
        var finalContent = string.Empty;
        var usedTools = false;
        var toolRecords = new List<ToolRecord>();
        var recalledMemory = _memoryStore.BuildContext(CurrentSession.SessionId, userInput, _config.App.MemoryRecallLimit, _config.App.MemoryMaxChars);

        for (var round = 0; round < Math.Max(1, _config.App.MaxToolRounds); round++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var response = await _chatClient.CompleteAsync(_config, ModelLevel, BuildMessagesForModel(recalledMemory), _toolExecutor.Definitions(), cancellationToken).ConfigureAwait(false);
            if (!response.Success)
            {
                if (_config.ModelEscalation.UpgradeOnLlmError && TryUpgradeModel($"LLM 调用失败: {response.Error}", progress))
                {
                    continue;
                }

                SaveAndRemember(userInput, finalContent);
                return new AgentRunResult
                {
                    Success = false,
                    Content = finalContent,
                    Error = response.Error,
                    ToolRecords = toolRecords,
                    SessionId = CurrentSession.SessionId,
                    Cwd = Cwd,
                };
            }

            if (!string.IsNullOrWhiteSpace(response.ReasoningContent))
            {
                progress?.Report(new AgentEvent { Type = AgentEventType.Reasoning, Message = response.ReasoningContent });
            }

            if (!string.IsNullOrWhiteSpace(response.Content))
            {
                finalContent = response.Content.Trim();
            }

            CurrentSession.Messages.Add(BuildAssistantMessage(response));

            if (!response.ToolCalls.Any())
            {
                if (!string.IsNullOrWhiteSpace(finalContent))
                {
                    progress?.Report(new AgentEvent { Type = AgentEventType.Content, Message = finalContent });
                    SaveAndRemember(userInput, finalContent);
                    return new AgentRunResult
                    {
                        Success = true,
                        Content = finalContent,
                        ToolRecords = toolRecords,
                        SessionId = CurrentSession.SessionId,
                        Cwd = Cwd,
                    };
                }

                if (usedTools)
                {
                    finalContent = await RequestFinalSummaryAsync(progress, cancellationToken).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(finalContent))
                    {
                        SaveAndRemember(userInput, finalContent);
                        return new AgentRunResult
                        {
                            Success = true,
                            Content = finalContent,
                            ToolRecords = toolRecords,
                            SessionId = CurrentSession.SessionId,
                            Cwd = Cwd,
                        };
                    }
                }

                _consecutiveEmptyResponses++;
                if (_consecutiveEmptyResponses >= Math.Max(1, _config.ModelEscalation.EmptyResponseRetryLimit) && !TryUpgradeModel("模型连续返回空响应", progress))
                {
                    SaveAndRemember(userInput, finalContent);
                    return new AgentRunResult
                    {
                        Success = false,
                        Content = finalContent,
                        Error = "模型连续返回空响应，任务已停止。",
                        ToolRecords = toolRecords,
                        SessionId = CurrentSession.SessionId,
                        Cwd = Cwd,
                    };
                }

                CurrentSession.Messages.Add(new PersistedChatMessage
                {
                    Role = "system",
                    Content = "上一轮返回了空响应。下一轮必须给出明确答复或调用必要工具，禁止空白。",
                });
                continue;
            }

            _consecutiveEmptyResponses = 0;
            foreach (var toolCall in response.ToolCalls)
            {
                usedTools = true;
                progress?.Report(new AgentEvent
                {
                    Type = AgentEventType.ToolCall,
                    ToolName = toolCall.Name,
                    Arguments = toolCall.Arguments,
                    Message = JsonSerializer.Serialize(toolCall.Arguments),
                });

                TrackToolSignature(toolCall);
                var result = await _toolExecutor.ExecuteAsync(toolCall, cancellationToken).ConfigureAwait(false);
                toolRecords.Add(new ToolRecord
                {
                    Name = toolCall.Name,
                    Arguments = toolCall.Arguments,
                    Ok = result.Ok,
                    Output = result.Output,
                });
                progress?.Report(new AgentEvent
                {
                    Type = AgentEventType.ToolResult,
                    ToolName = toolCall.Name,
                    ToolResult = result,
                    Message = result.Output,
                });

                CurrentSession.Messages.Add(new PersistedChatMessage
                {
                    Role = "tool",
                    ToolCallId = toolCall.Id,
                    Name = toolCall.Name,
                    Content = result.Output,
                });

                _consecutiveToolFailures = result.Ok ? 0 : _consecutiveToolFailures + 1;
                CurrentSession.Cwd = _toolExecutor.Cwd;
                if (ShouldUpgradeAfterTool(result.Ok) && TryUpgradeModel("工具调用重复失败或重复执行", progress))
                {
                    break;
                }
            }
        }

        SaveAndRemember(userInput, finalContent);
        return new AgentRunResult
        {
            Success = false,
            Content = finalContent,
            Error = "达到最大工具调用轮次，任务被停止。",
            ToolRecords = toolRecords,
            SessionId = CurrentSession.SessionId,
            Cwd = Cwd,
        };
    }

    private async Task<string> RequestFinalSummaryAsync(IProgress<AgentEvent>? progress, CancellationToken cancellationToken)
    {
        var messages = BuildMessagesForModel(string.Empty).ToList();
        messages.Add(new PersistedChatMessage
        {
            Role = "system",
            Content = "你已经拿到全部工具结果。现在直接基于这些结果给用户最终答复，不要再调用工具。",
        });

        var response = await _chatClient.CompleteAsync(_config, ModelLevel, messages, null, cancellationToken).ConfigureAwait(false);
        if (!response.Success || string.IsNullOrWhiteSpace(response.Content))
        {
            return string.Empty;
        }

        var final = response.Content.Trim();
        CurrentSession.Messages.Add(new PersistedChatMessage { Role = "assistant", Content = final });
        progress?.Report(new AgentEvent { Type = AgentEventType.Content, Message = final });
        return final;
    }

    private IReadOnlyList<PersistedChatMessage> BuildMessagesForModel(string recalledMemory)
    {
        var messages = CurrentSession.Messages.Select(CloneMessage).ToList();
        var runtimeContext = BuildRuntimeContext();
        if (!string.IsNullOrWhiteSpace(runtimeContext))
        {
            messages.Add(new PersistedChatMessage { Role = "system", Content = runtimeContext });
        }

        if (!string.IsNullOrWhiteSpace(recalledMemory))
        {
            messages.Add(new PersistedChatMessage { Role = "system", Content = recalledMemory });
        }

        return messages;
    }

    private string BuildRuntimeContext()
    {
        var parts = new List<string>
        {
            "当前主机基础信息:",
            $"- OS: {RuntimeInformation.OSDescription}",
            $"- .NET: {RuntimeInformation.FrameworkDescription}",
            $"- 当前工作目录: {Cwd}",
        };
        if (_config.App.WorkspaceContextEnabled)
        {
            parts.Add(_toolExecutor.BuildWorkspaceContext(_config.App.WorkspaceContextMaxDepth, _config.App.WorkspaceContextMaxEntries));
        }

        return string.Join(Environment.NewLine, parts.Where(static item => !string.IsNullOrWhiteSpace(item)));
    }

    private PersistedChatMessage BuildAssistantMessage(LlmResponse response)
    {
        var message = new PersistedChatMessage
        {
            Role = "assistant",
            Content = response.Content,
        };
        if (response.ToolCalls.Any())
        {
            message.ToolCalls = response.ToolCalls.Select(call => new PersistedToolCall
            {
                Id = call.Id,
                Type = "function",
                Function = new PersistedToolFunction
                {
                    Name = call.Name,
                    Arguments = JsonSerializer.Serialize(call.Arguments),
                },
            }).ToList();
        }

        return message;
    }

    private bool ShouldUpgradeAfterTool(bool resultOk)
    {
        return _config.ModelEscalation.Enabled &&
            (_repeatedToolCount >= _config.ModelEscalation.RepeatCommandThreshold || (!resultOk && _consecutiveToolFailures >= _config.ModelEscalation.ConsecutiveErrorThreshold));
    }

    private bool TryUpgradeModel(string reason, IProgress<AgentEvent>? progress)
    {
        if (!_config.ModelEscalation.Enabled || ModelLevel >= _config.GetMaxModelLevel())
        {
            return false;
        }

        var previous = ModelLevel;
        ModelLevel++;
        CurrentSession.ModelLevel = ModelLevel;
        _systemPrompt = SystemPromptBuilder.Build(_config, ModelLevel, _config.GetMaxModelLevel());
        EnsureSystemPrompt();
        ResetCounters();
        CurrentSession.Messages.Add(new PersistedChatMessage
        {
            Role = "system",
            Content = $"系统已将模型从 level {previous} 升级到 level {ModelLevel}。原因: {reason}。请重新规划，避免重复失败。",
        });
        progress?.Report(new AgentEvent { Type = AgentEventType.Warning, Message = $"模型已自动升级到 {DescribeModel()}。原因: {reason}" });
        SaveSession();
        return true;
    }

    private void EnsureSystemPrompt()
    {
        if (!CurrentSession.Messages.Any())
        {
            CurrentSession.Messages.Add(new PersistedChatMessage { Role = "system", Content = _systemPrompt });
            return;
        }

        if (CurrentSession.Messages[0].Role == "system")
        {
            CurrentSession.Messages[0].Content = _systemPrompt;
        }
        else
        {
            CurrentSession.Messages.Insert(0, new PersistedChatMessage { Role = "system", Content = _systemPrompt });
        }
    }

    private void SaveAndRemember(string userInput, string finalContent)
    {
        if (!string.IsNullOrWhiteSpace(userInput) || !string.IsNullOrWhiteSpace(finalContent))
        {
            _memoryStore.RecordTurn(CurrentSession.SessionId, userInput, finalContent);
        }

        SaveSession();
    }

    private void SaveSession()
    {
        CurrentSession.Cwd = _toolExecutor.Cwd;
        _sessionStore.Save(CurrentSession);
    }

    private void TrackToolSignature(ToolCall toolCall)
    {
        var signature = $"{toolCall.Name}:{JsonSerializer.Serialize(toolCall.Arguments)}";
        if (string.Equals(signature, _lastToolSignature, StringComparison.Ordinal))
        {
            _repeatedToolCount++;
        }
        else
        {
            _lastToolSignature = signature;
            _repeatedToolCount = 1;
        }
    }

    private void ResetCounters()
    {
        _lastToolSignature = string.Empty;
        _repeatedToolCount = 0;
        _consecutiveToolFailures = 0;
        _consecutiveEmptyResponses = 0;
    }

    private static PersistedChatMessage CloneMessage(PersistedChatMessage message)
    {
        return new PersistedChatMessage
        {
            Role = message.Role,
            Content = message.Content,
            Name = message.Name,
            ToolCallId = message.ToolCallId,
            ToolCalls = message.ToolCalls?.Select(tool => new PersistedToolCall
            {
                Id = tool.Id,
                Type = tool.Type,
                Function = new PersistedToolFunction
                {
                    Name = tool.Function.Name,
                    Arguments = tool.Function.Arguments,
                },
            }).ToList(),
        };
    }
}