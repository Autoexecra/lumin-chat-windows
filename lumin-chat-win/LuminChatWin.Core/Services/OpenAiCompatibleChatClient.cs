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

            using var request = new HttpRequestMessage(HttpMethod.Post, BuildEndpoint(modelConfig.BaseUrl));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", modelConfig.ApiKey);

            var payload = new JsonObject
            {
                ["model"] = modelConfig.Model,
                ["temperature"] = modelConfig.Temperature,
                ["max_tokens"] = modelConfig.MaxTokens,
                ["stream"] = false,
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

            request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
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
            return new LlmResponse { Success = false, Error = ex.Message };
        }
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
        var choices = root.TryGetProperty("choices", out var choiceElement) ? choiceElement : default;
        if (choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
        {
            return new LlmResponse { Success = false, Error = "LLM 返回结构缺少 choices。" };
        }

        var firstChoice = choices[0];
        var message = firstChoice.TryGetProperty("message", out var messageElement) ? messageElement : default;
        var rawContent = ReadMessageContent(message);
        var reasoning = message.TryGetProperty("reasoning_content", out var reasoningElement) ? ReadMessageContent(reasoningElement) : string.Empty;
        var toolCalls = ParseToolCalls(message);
        var extraReasoning = SplitThinking(rawContent, out var cleanContent);

        return new LlmResponse
        {
            Success = true,
            Content = cleanContent,
            ReasoningContent = string.Concat(reasoning, extraReasoning),
            ToolCalls = toolCalls,
            FinishReason = firstChoice.TryGetProperty("finish_reason", out var finishElement) ? finishElement.GetString() ?? string.Empty : string.Empty,
            Usage = ParseUsage(root),
        };
    }

    private static string ReadMessageContent(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.Array => string.Concat(element.EnumerateArray().Select(ReadContentPart)),
            JsonValueKind.Object when element.TryGetProperty("content", out var nested) => ReadMessageContent(nested),
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
}