using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LuminChatWin.Core.Models;

namespace LuminChatWin.Core.Services;

public sealed class OpenAiCompatibleChatClient : IChatCompletionClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = null,
    };

    private readonly HttpClient _httpClient;

    public OpenAiCompatibleChatClient(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
        if (_httpClient.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("lumin-chat-win", "1.0"));
        }
    }

    public async Task<LlmResponse> CompleteAsync(
        AppConfig config,
        int modelLevel,
        IReadOnlyList<PersistedChatMessage> messages,
        IReadOnlyList<Dictionary<string, object?>>? tools,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var modelConfig = config.GetModel(modelLevel);
            if (string.IsNullOrWhiteSpace(modelConfig.BaseUrl) || string.IsNullOrWhiteSpace(modelConfig.Model))
            {
                return new LlmResponse { Success = false, Error = $"level{modelLevel} 模型配置不完整。" };
            }

            if (string.IsNullOrWhiteSpace(modelConfig.ApiKey))
            {
                return new LlmResponse { Success = false, Error = $"level{modelLevel} 未配置 API Key。" };
            }

            using var request = BuildRequest(modelConfig, messages, tools, stream: false, out var requestBody);
            LlmDebugLogger.LogRequest(config, modelLevel, request.RequestUri?.ToString() ?? string.Empty, requestBody);
            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            LlmDebugLogger.LogResponse(config, modelLevel, body, response.IsSuccessStatusCode);
            if (!response.IsSuccessStatusCode)
            {
                return new LlmResponse
                {
                    Success = false,
                    Error = $"LLM HTTP {(int)response.StatusCode}: {body}",
                };
            }

            return ParseResponse(body);
        }
        catch (Exception ex)
        {
            LlmDebugLogger.LogResponse(config, modelLevel, ex.ToString(), success: false);
            return new LlmResponse { Success = false, Error = ex.Message };
        }
    }

    public async Task<LlmResponse> CompleteStreamingAsync(
        AppConfig config,
        int modelLevel,
        IReadOnlyList<PersistedChatMessage> messages,
        IReadOnlyList<Dictionary<string, object?>>? tools,
        Action<string>? onReasoningChunk = null,
        Action<string>? onContentChunk = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var modelConfig = config.GetModel(modelLevel);
            if (string.IsNullOrWhiteSpace(modelConfig.BaseUrl) || string.IsNullOrWhiteSpace(modelConfig.Model))
            {
                return new LlmResponse { Success = false, Error = $"level{modelLevel} 模型配置不完整。" };
            }

            if (string.IsNullOrWhiteSpace(modelConfig.ApiKey))
            {
                return new LlmResponse { Success = false, Error = $"level{modelLevel} 未配置 API Key。" };
            }

            using var request = BuildRequest(modelConfig, messages, tools, stream: true, out var requestBody);
            LlmDebugLogger.LogRequest(config, modelLevel, request.RequestUri?.ToString() ?? string.Empty, requestBody);

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                LlmDebugLogger.LogResponse(config, modelLevel, errorBody, success: false);
                return new LlmResponse
                {
                    Success = false,
                    Error = $"LLM HTTP {(int)response.StatusCode}: {errorBody}",
                };
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            var contentBuilder = new StringBuilder();
            var reasoningBuilder = new StringBuilder();
            var toolCallsMap = new Dictionary<int, StreamingToolCallState>();
            var usage = new Dictionary<string, object?>();
            var rawEvents = new StringBuilder();
            var finishReason = string.Empty;

            // OpenAI-compatible providers usually stream as SSE lines prefixed with data:.
            while (!reader.EndOfStream)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var payload = line[5..].Trim();
                if (payload.Length == 0)
                {
                    continue;
                }

                if (string.Equals(payload, "[DONE]", StringComparison.Ordinal))
                {
                    break;
                }

                rawEvents.AppendLine(payload);
                using var document = JsonDocument.Parse(payload);
                if (!TryReadChoices(document.RootElement, out var choices) || choices.GetArrayLength() == 0)
                {
                    MergeUsage(document.RootElement, usage);
                    continue;
                }

                var choice = choices[0];
                if (choice.TryGetProperty("finish_reason", out var finishReasonElement) && finishReasonElement.ValueKind == JsonValueKind.String)
                {
                    finishReason = finishReasonElement.GetString() ?? finishReason;
                }

                var delta = choice.TryGetProperty("delta", out var deltaElement)
                    ? deltaElement
                    : choice.TryGetProperty("message", out var messageElement)
                        ? messageElement
                        : default;

                if (delta.ValueKind == JsonValueKind.Object)
                {
                    // Reasoning and visible content can arrive in separate incremental fields.
                    var reasoningChunk = delta.TryGetProperty("reasoning_content", out var reasoningElement)
                        ? ReadMessageContent(reasoningElement)
                        : string.Empty;
                    if (!string.IsNullOrEmpty(reasoningChunk))
                    {
                        reasoningBuilder.Append(reasoningChunk);
                        onReasoningChunk?.Invoke(reasoningChunk);
                    }

                    var contentChunk = delta.TryGetProperty("content", out var contentElement)
                        ? ReadMessageContent(contentElement)
                        : string.Empty;
                    if (!string.IsNullOrEmpty(contentChunk))
                    {
                        contentBuilder.Append(contentChunk);
                        onContentChunk?.Invoke(contentChunk);
                    }

                    if (delta.TryGetProperty("tool_calls", out var toolCallsElement) && toolCallsElement.ValueKind == JsonValueKind.Array)
                    {
                        // Tool call arguments may be split across many chunks, so we merge them by index.
                        MergeStreamingToolCalls(toolCallsMap, toolCallsElement);
                    }
                }

                MergeUsage(document.RootElement, usage);
            }

            var rawContent = contentBuilder.ToString();
            var normalizedContent = NormalizeThreePartPayload(rawContent, out var inlineReasoning);
            var extraReasoning = SplitThinking(normalizedContent, out var cleanContent);
            var finalReasoning = string.Concat(reasoningBuilder.ToString(), inlineReasoning, extraReasoning);
            var finalResponse = new LlmResponse
            {
                Success = true,
                Content = cleanContent,
                ReasoningContent = finalReasoning,
                ToolCalls = BuildStreamingToolCalls(toolCallsMap),
                FinishReason = finishReason,
                Usage = usage,
            };

            LlmDebugLogger.LogResponse(config, modelLevel, BuildStreamingLogPayload(finalResponse, rawContent, rawEvents.ToString()), success: true);
            return finalResponse;
        }
        catch (Exception ex)
        {
            LlmDebugLogger.LogResponse(config, modelLevel, ex.ToString(), success: false);
            return new LlmResponse { Success = false, Error = ex.Message };
        }
    }

    private HttpRequestMessage BuildRequest(AiModelConfig modelConfig, IReadOnlyList<PersistedChatMessage> messages, IReadOnlyList<Dictionary<string, object?>>? tools, bool stream, out string requestBody)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, BuildEndpoint(modelConfig.BaseUrl));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", modelConfig.ApiKey);

        var payload = new JsonObject
        {
            ["model"] = modelConfig.Model,
            ["temperature"] = modelConfig.Temperature,
            ["max_tokens"] = modelConfig.MaxTokens,
            ["stream"] = stream,
            ["messages"] = BuildMessages(messages),
        };

        if (tools is { Count: > 0 })
        {
            payload["tools"] = JsonSerializer.SerializeToNode(tools, JsonOptions);
            payload["tool_choice"] = "auto";
        }

        if (modelConfig.EnableThinking)
        {
            payload["extra_body"] = new JsonObject { ["enable_thinking"] = true };
        }

        requestBody = payload.ToJsonString();
        request.Content = new StringContent(requestBody, Encoding.UTF8, "application/json");
        return request;
    }

    private static string BuildEndpoint(string baseUrl)
    {
        var trimmed = baseUrl.TrimEnd('/');
        return trimmed.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : trimmed + "/chat/completions";
    }

    private static JsonArray BuildMessages(IReadOnlyList<PersistedChatMessage> messages)
    {
        var array = new JsonArray();
        foreach (var message in messages)
        {
            var node = new JsonObject
            {
                ["role"] = message.Role,
                ["content"] = message.Content,
            };

            if (!string.IsNullOrWhiteSpace(message.Name))
            {
                node["name"] = message.Name;
            }

            if (!string.IsNullOrWhiteSpace(message.ToolCallId))
            {
                node["tool_call_id"] = message.ToolCallId;
            }

            if (message.ToolCalls is { Count: > 0 })
            {
                node["tool_calls"] = JsonSerializer.SerializeToNode(message.ToolCalls, JsonOptions);
            }

            array.Add(node);
        }

        return array;
    }

    private static LlmResponse ParseResponse(string body)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        if (TryParseChoicesResponse(root, out var response))
        {
            return response;
        }

        if (TryParseDirectMessageResponse(root, out response))
        {
            return response;
        }

        if (root.TryGetProperty("error", out var errorElement))
        {
            return new LlmResponse
            {
                Success = false,
                Error = $"LLM 返回错误: {ReadMessageContent(errorElement)}",
            };
        }

        return new LlmResponse
        {
            Success = false,
            Error = $"LLM 返回结构无法识别，缺少 choices/message。响应片段: {BuildPreview(body)}",
        };
    }

    internal static bool TryParseChoicesResponse(JsonElement root, out LlmResponse response)
    {
        if (!TryReadChoices(root, out var choices) || choices.GetArrayLength() == 0)
        {
            response = new LlmResponse();
            return false;
        }

        var firstChoice = choices[0];
        var message = firstChoice.TryGetProperty("message", out var messageElement) ? messageElement : default;
        var rawContent = ReadMessageContent(message);
        rawContent = NormalizeThreePartPayload(rawContent, out var inlineReasoning);
        var reasoning = message.TryGetProperty("reasoning_content", out var reasoningElement) ? ReadMessageContent(reasoningElement) : string.Empty;
        var toolCalls = ParseToolCalls(message);
        var extraReasoning = SplitThinking(rawContent, out var cleanContent);

        response = new LlmResponse
        {
            Success = true,
            Content = cleanContent,
            ReasoningContent = string.Concat(reasoning, inlineReasoning, extraReasoning),
            ToolCalls = toolCalls,
            FinishReason = firstChoice.TryGetProperty("finish_reason", out var finishElement) ? finishElement.GetString() ?? string.Empty : string.Empty,
            Usage = ParseUsage(root),
        };
        return true;
    }

    internal static bool TryParseDirectMessageResponse(JsonElement root, out LlmResponse response)
    {
        JsonElement message;
        if (root.TryGetProperty("message", out var directMessage))
        {
            message = directMessage;
        }
        else if (root.TryGetProperty("data", out var dataElement) && dataElement.ValueKind == JsonValueKind.Object && dataElement.TryGetProperty("message", out var nestedMessage))
        {
            message = nestedMessage;
        }
        else
        {
            response = new LlmResponse();
            return false;
        }

        var rawContent = ReadMessageContent(message);
        rawContent = NormalizeThreePartPayload(rawContent, out var inlineReasoning);
        var reasoning = message.TryGetProperty("reasoning_content", out var reasoningElement) ? ReadMessageContent(reasoningElement) : string.Empty;
        var extraReasoning = SplitThinking(rawContent, out var cleanContent);

        response = new LlmResponse
        {
            Success = !string.IsNullOrWhiteSpace(cleanContent) || !string.IsNullOrWhiteSpace(reasoning),
            Content = cleanContent,
            ReasoningContent = string.Concat(reasoning, inlineReasoning, extraReasoning),
            ToolCalls = ParseToolCalls(message),
            Usage = ParseUsage(root),
            Error = string.IsNullOrWhiteSpace(cleanContent) && string.IsNullOrWhiteSpace(reasoning)
                ? "LLM 返回了 message，但未包含可读内容。"
                : string.Empty,
        };
        return true;
    }

    private static bool TryReadChoices(JsonElement root, out JsonElement choices)
    {
        if (root.TryGetProperty("choices", out choices) && choices.ValueKind == JsonValueKind.Array)
        {
            return true;
        }

        if (root.TryGetProperty("data", out var dataElement) && dataElement.ValueKind == JsonValueKind.Object && dataElement.TryGetProperty("choices", out choices) && choices.ValueKind == JsonValueKind.Array)
        {
            return true;
        }

        choices = default;
        return false;
    }

    private static string BuildPreview(string body)
    {
        var compact = body.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Trim();
        return compact.Length <= 180 ? compact : compact[..180] + "...";
    }

    private static string ReadMessageContent(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.Array => string.Concat(element.EnumerateArray().Select(ReadContentPart)),
            JsonValueKind.Object when element.TryGetProperty("content", out var nested) => ReadMessageContent(nested),
            JsonValueKind.Object when element.TryGetProperty("message", out var message) => ReadMessageContent(message),
            JsonValueKind.Object when element.TryGetProperty("text", out var text) => ReadMessageContent(text),
            _ => string.Empty,
        };
    }

    private static string ReadContentPart(JsonElement part)
    {
        if (part.ValueKind == JsonValueKind.String)
        {
            return part.GetString() ?? string.Empty;
        }

        if (part.ValueKind == JsonValueKind.Object)
        {
            if (part.TryGetProperty("text", out var text))
            {
                return text.GetString() ?? string.Empty;
            }

            if (part.TryGetProperty("content", out var nested))
            {
                return ReadMessageContent(nested);
            }
        }

        return string.Empty;
    }

    private static List<ToolCall> ParseToolCalls(JsonElement message)
    {
        var calls = new List<ToolCall>();
        if (!message.TryGetProperty("tool_calls", out var toolCallsElement) || toolCallsElement.ValueKind != JsonValueKind.Array)
        {
            return calls;
        }

        foreach (var call in toolCallsElement.EnumerateArray())
        {
            var id = call.TryGetProperty("id", out var idElement) ? idElement.GetString() ?? Guid.NewGuid().ToString("N") : Guid.NewGuid().ToString("N");
            var function = call.TryGetProperty("function", out var functionElement) ? functionElement : default;
            var name = function.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? string.Empty : string.Empty;
            var argsText = function.TryGetProperty("arguments", out var argsElement) ? argsElement.GetString() ?? "{}" : "{}";
            calls.Add(new ToolCall(id, name, SafeParseArguments(argsText)));
        }

        return calls;
    }

    private static Dictionary<string, object?> ParseUsage(JsonElement root)
    {
        var usage = new Dictionary<string, object?>();
        if (!root.TryGetProperty("usage", out var usageElement) || usageElement.ValueKind != JsonValueKind.Object)
        {
            return usage;
        }

        foreach (var property in usageElement.EnumerateObject())
        {
            usage[property.Name] = property.Value.ValueKind switch
            {
                JsonValueKind.Number when property.Value.TryGetInt64(out var longValue) => longValue,
                JsonValueKind.Number when property.Value.TryGetDouble(out var doubleValue) => doubleValue,
                JsonValueKind.String => property.Value.GetString(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => property.Value.ToString(),
            };
        }

        return usage;
    }

    private static Dictionary<string, object?> SafeParseArguments(string argsText)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, object?>>(argsText, JsonOptions);
            return parsed ?? new Dictionary<string, object?>();
        }
        catch
        {
            return new Dictionary<string, object?> { ["raw"] = argsText };
        }
    }

    private static string SplitThinking(string rawContent, out string cleanContent)
    {
        cleanContent = rawContent;
        if (string.IsNullOrWhiteSpace(rawContent) || !rawContent.Contains("<think>", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        var remaining = rawContent;
        while (true)
        {
            var start = remaining.IndexOf("<think>", StringComparison.OrdinalIgnoreCase);
            if (start < 0)
            {
                break;
            }

            var end = remaining.IndexOf("</think>", start, StringComparison.OrdinalIgnoreCase);
            if (end < 0)
            {
                break;
            }

            builder.Append(remaining.Substring(start + 7, end - start - 7));
            remaining = remaining.Remove(start, end + 8 - start);
        }

        cleanContent = remaining.Trim();
        return builder.ToString();
    }

    private static string NormalizeThreePartPayload(string rawContent, out string inlineReasoning)
    {
        inlineReasoning = string.Empty;
        if (string.IsNullOrWhiteSpace(rawContent))
        {
            return string.Empty;
        }

        var normalized = rawContent.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        var contentMarkerIndex = IndexOfSectionMarker(normalized, "content");
        if (contentMarkerIndex < 0)
        {
            return normalized;
        }

        var headerSection = normalized[..contentMarkerIndex].Trim();
        inlineReasoning = StripSectionLabel(headerSection, "thinking");

        var contentSection = normalized[(contentMarkerIndex + "content".Length)..].TrimStart('\n', ' ', '\t');
        var toolCallsMarkerIndex = IndexOfSectionMarker(contentSection, "tool_calls");
        if (toolCallsMarkerIndex >= 0)
        {
            contentSection = contentSection[..toolCallsMarkerIndex].TrimEnd();
        }

        return contentSection.Trim();
    }

    private static int IndexOfSectionMarker(string text, string sectionName)
    {
        var prefix = sectionName + "\n";
        if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (string.Equals(text, sectionName, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        var marker = "\n" + sectionName + "\n";
        var index = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index >= 0)
        {
            return index + 1;
        }

        var suffixMarker = "\n" + sectionName;
        index = text.LastIndexOf(suffixMarker, StringComparison.OrdinalIgnoreCase);
        return index >= 0 && index + suffixMarker.Length == text.Length ? index + 1 : -1;
    }

    private static string StripSectionLabel(string text, string sectionName)
    {
        var normalized = text.Trim();
        if (normalized.StartsWith(sectionName + "\n", StringComparison.OrdinalIgnoreCase))
        {
            return normalized[(sectionName.Length + 1)..].Trim();
        }

        return normalized;
    }

    private static void MergeUsage(JsonElement root, Dictionary<string, object?> usage)
    {
        if (!root.TryGetProperty("usage", out var usageElement) || usageElement.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var property in usageElement.EnumerateObject())
        {
            usage[property.Name] = property.Value.ValueKind switch
            {
                JsonValueKind.Number when property.Value.TryGetInt64(out var longValue) => longValue,
                JsonValueKind.Number when property.Value.TryGetDouble(out var doubleValue) => doubleValue,
                JsonValueKind.String => property.Value.GetString(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => property.Value.ToString(),
            };
        }
    }

    private static void MergeStreamingToolCalls(Dictionary<int, StreamingToolCallState> toolCallsMap, JsonElement toolCallsElement)
    {
        foreach (var toolCall in toolCallsElement.EnumerateArray())
        {
            var index = toolCall.TryGetProperty("index", out var indexElement) && indexElement.TryGetInt32(out var parsedIndex)
                ? parsedIndex
                : toolCallsMap.Count;
            if (!toolCallsMap.TryGetValue(index, out var state))
            {
                state = new StreamingToolCallState();
                toolCallsMap[index] = state;
            }

            if (toolCall.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String)
            {
                state.Id = idElement.GetString() ?? state.Id;
            }

            if (!toolCall.TryGetProperty("function", out var functionElement) || functionElement.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (functionElement.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String)
            {
                state.Name = nameElement.GetString() ?? state.Name;
            }

            if (functionElement.TryGetProperty("arguments", out var argumentsElement) && argumentsElement.ValueKind == JsonValueKind.String)
            {
                state.Arguments.Append(argumentsElement.GetString());
            }
        }
    }

    private static List<ToolCall> BuildStreamingToolCalls(Dictionary<int, StreamingToolCallState> toolCallsMap)
    {
        return toolCallsMap
            .OrderBy(pair => pair.Key)
            .Select(pair => new ToolCall(
                string.IsNullOrWhiteSpace(pair.Value.Id) ? Guid.NewGuid().ToString("N") : pair.Value.Id,
                pair.Value.Name,
                SafeParseArguments(pair.Value.Arguments.ToString())))
            .Where(call => !string.IsNullOrWhiteSpace(call.Name))
            .ToList();
    }

    private static string BuildStreamingLogPayload(LlmResponse response, string rawContent, string rawEvents)
    {
        return new StringBuilder()
            .AppendLine("Parsed reasoning:")
            .AppendLine(string.IsNullOrWhiteSpace(response.ReasoningContent) ? "<empty>" : response.ReasoningContent)
            .AppendLine()
            .AppendLine("Parsed content:")
            .AppendLine(string.IsNullOrWhiteSpace(response.Content) ? "<empty>" : response.Content)
            .AppendLine()
            .AppendLine("Finish reason:")
            .AppendLine(string.IsNullOrWhiteSpace(response.FinishReason) ? "<empty>" : response.FinishReason)
            .AppendLine()
            .AppendLine("Tool calls:")
            .AppendLine(JsonSerializer.Serialize(response.ToolCalls, new JsonSerializerOptions(JsonOptions) { WriteIndented = true }))
            .AppendLine()
            .AppendLine("Usage:")
            .AppendLine(JsonSerializer.Serialize(response.Usage, new JsonSerializerOptions(JsonOptions) { WriteIndented = true }))
            .AppendLine()
            .AppendLine("Raw streamed content:")
            .AppendLine(string.IsNullOrWhiteSpace(rawContent) ? "<empty>" : rawContent)
            .AppendLine()
            .AppendLine("Raw SSE events:")
            .AppendLine(string.IsNullOrWhiteSpace(rawEvents) ? "<empty>" : rawEvents)
            .ToString();
    }

    private sealed class StreamingToolCallState
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public StringBuilder Arguments { get; } = new();
    }
}