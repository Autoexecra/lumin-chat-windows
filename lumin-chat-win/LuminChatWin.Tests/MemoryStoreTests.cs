using LuminChatWin.Core.Services;

namespace LuminChatWin.Tests;

public sealed class MemoryStoreTests
{
    [Fact]
    public void MemoryStore_RecordsTurnsAndBuildsContext()
    {
        var root = CreateTempDirectory();
        var store = new MemoryStore(root);

        store.EnsureSession("session-1");
        store.RecordTurn("session-1", "记住：默认使用中文总结并优先检查 git diff", "好的，我会优先使用中文总结，并在增量开发时先检查 git diff。");
        store.RecordTurn("session-1", "现在帮我回顾默认偏好", "你偏好中文总结。");

        var context = store.BuildContext("session-1", "git diff 和中文总结", 5, 1200);

        Assert.Contains("中文", context);
        Assert.Contains("git diff", context, StringComparison.OrdinalIgnoreCase);
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "LuminChatWinTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}