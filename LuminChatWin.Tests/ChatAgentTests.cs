using LuminChatWin.Core.Models;
using LuminChatWin.Core.Services;

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
    }
}