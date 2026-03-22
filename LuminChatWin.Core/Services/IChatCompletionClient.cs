using LuminChatWin.Core.Models;

namespace LuminChatWin.Core.Services;

public interface IChatCompletionClient
{
    Task<LlmResponse> CompleteAsync(
        AppConfig config,
        int modelLevel,
        IReadOnlyList<PersistedChatMessage> messages,
        IReadOnlyList<Dictionary<string, object?>>? tools,
        CancellationToken cancellationToken = default);

    Task<LlmResponse> CompleteStreamingAsync(
        AppConfig config,
        int modelLevel,
        IReadOnlyList<PersistedChatMessage> messages,
        IReadOnlyList<Dictionary<string, object?>>? tools,
        Action<string>? onReasoningChunk = null,
        Action<string>? onContentChunk = null,
        CancellationToken cancellationToken = default);
}