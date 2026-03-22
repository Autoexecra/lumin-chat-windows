using LuminChatWin.Core.Models;
using LuminChatWin.Core.Services;
using System.Text.Json;

namespace LuminChatWin.Tests;

public sealed class ChatAgentTests
{
    [Fact]
    public async Task ChatAgent_ExecutesToolCallAndReturnsFinalContent()
    {
        var root = CreateTempDirectory();
        var config = AppConfig.CreateDefault();
        config.App.SessionDir = Path.Combine(root, "sessions");
        config.App.MemoryDir = Path.Combine(root, "memory");
        config.App.MaxToolRounds = 4;

        var client = new FakeChatCompletionClient(
            new LlmResponse
            {
                Success = true,
                ToolCalls = [new ToolCall("call-1", "get_environment", new Dictionary<string, object?>())],
            },
            new LlmResponse
            {
                Success = true,
                Content = "环境检查完成。",
            });

        var agent = new ChatAgent(config, root, client);

        var result = await agent.RunWithTraceAsync("检查当前环境");

        Assert.True(result.Success);
        Assert.Equal("环境检查完成。", result.Content);
        Assert.Single(result.ToolRecords);
        Assert.Equal("get_environment", result.ToolRecords[0].Name);
    }

        [Fact]
        public void OpenAiCompatibleChatClient_ParseChoicesResponse_SupportsNestedDataChoices()
        {
                using var document = JsonDocument.Parse("""
                {
                    "data": {
                        "choices": [
                            {
                                "message": {
                                    "content": "TEST_OK"
                                },
                                "finish_reason": "stop"
                            }
                        ]
                    }
                }
                """);

                var ok = OpenAiCompatibleChatClient.TryParseChoicesResponse(document.RootElement, out var response);

                Assert.True(ok);
                Assert.True(response.Success);
                Assert.Equal("TEST_OK", response.Content);
        }

        [Fact]
        public void OpenAiCompatibleChatClient_ParseDirectMessageResponse_SupportsMessageOnlyPayload()
        {
                using var document = JsonDocument.Parse("""
                {
                    "message": {
                        "content": [
                            { "text": "hello" }
                        ]
                    }
                }
                """);

                var ok = OpenAiCompatibleChatClient.TryParseDirectMessageResponse(document.RootElement, out var response);

                Assert.True(ok);
                Assert.True(response.Success);
                Assert.Equal("hello", response.Content);
        }

        [Fact]
        public void OpenAiCompatibleChatClient_ParseChoicesResponse_NormalizesThreePartContentLabels()
        {
                using var document = JsonDocument.Parse("""
                {
                    "choices": [
                        {
                            "message": {
                                "content": "thinking\n先分析当前终端状态。\ncontent\n{\"complete\":false,\"analysis\":\"继续执行\",\"final_message\":\"\"}\ntool_calls\n"
                            },
                            "finish_reason": "tool_calls"
                        }
                    ]
                }
                """);

                var ok = OpenAiCompatibleChatClient.TryParseChoicesResponse(document.RootElement, out var response);

                Assert.True(ok);
                Assert.True(response.Success);
                Assert.Contains("先分析当前终端状态", response.ReasoningContent, StringComparison.Ordinal);
                Assert.Equal("{\"complete\":false,\"analysis\":\"继续执行\",\"final_message\":\"\"}", response.Content);
        }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "LuminChatWinTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class FakeChatCompletionClient(params LlmResponse[] responses) : IChatCompletionClient
    {
        private readonly Queue<LlmResponse> _responses = new(responses);

        public Task<LlmResponse> CompleteAsync(AppConfig config, int modelLevel, IReadOnlyList<PersistedChatMessage> messages, IReadOnlyList<Dictionary<string, object?>>? tools, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_responses.Count > 0 ? _responses.Dequeue() : new LlmResponse { Success = true, Content = "" });
        }

        public Task<LlmResponse> CompleteStreamingAsync(AppConfig config, int modelLevel, IReadOnlyList<PersistedChatMessage> messages, IReadOnlyList<Dictionary<string, object?>>? tools, Action<string>? onReasoningChunk = null, Action<string>? onContentChunk = null, CancellationToken cancellationToken = default)
        {
            var response = _responses.Count > 0 ? _responses.Dequeue() : new LlmResponse { Success = true, Content = string.Empty };
            if (!string.IsNullOrWhiteSpace(response.ReasoningContent))
            {
                onReasoningChunk?.Invoke(response.ReasoningContent);
            }

            if (!string.IsNullOrWhiteSpace(response.Content))
            {
                onContentChunk?.Invoke(response.Content);
            }

            return Task.FromResult(response);
        }
    }
}