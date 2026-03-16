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
        var reasoning = message.TryGetProperty("reasoning_content", out var reasoningElement) ? ReadMessageContent(reasoningElement) : string.Empty;
        var toolCalls = ParseToolCalls(message);
        var extraReasoning = SplitThinking(rawContent, out var cleanContent);

        response = new LlmResponse
        {
            Success = true,
            Content = cleanContent,
            ReasoningContent = string.Concat(reasoning, extraReasoning),
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
        var reasoning = message.TryGetProperty("reasoning_content", out var reasoningElement) ? ReadMessageContent(reasoningElement) : string.Empty;
        var extraReasoning = SplitThinking(rawContent, out var cleanContent);

        response = new LlmResponse
        {
            Success = !string.IsNullOrWhiteSpace(cleanContent) || !string.IsNullOrWhiteSpace(reasoning),
            Content = cleanContent,
            ReasoningContent = string.Concat(reasoning, extraReasoning),
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
}