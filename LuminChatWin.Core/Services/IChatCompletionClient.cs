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
}