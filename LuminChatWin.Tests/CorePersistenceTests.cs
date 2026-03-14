using LuminChatWin.Core.Models;
using LuminChatWin.Core.Services;

namespace LuminChatWin.Tests;

public sealed class CorePersistenceTests
{
    [Fact]
    public void ConfigService_CreatesDefaultConfig_WhenMissing()
    {
        var root = CreateTempDirectory();
        var configPath = Path.Combine(root, "config.json");
        var service = new ConfigService(configPath);

        var config = service.LoadOrCreate();

        Assert.True(File.Exists(configPath));
        Assert.Equal(1, config.App.DefaultModelLevel);
        Assert.True(config.Ai.ContainsKey("level1"));
    }

    [Fact]
    public void SessionStore_CreatesAndLoadsSession()
    {
        var root = CreateTempDirectory();
        var store = new SessionStore(root);

        var created = store.Create(2, "auto", root, "system prompt");
        created.Messages.Add(new PersistedChatMessage { Role = "user", Content = "hello" });
        store.Save(created);
        var loaded = store.Load(created.SessionId);

        Assert.Equal(created.SessionId, loaded.SessionId);
        Assert.Equal(2, loaded.ModelLevel);
        Assert.Contains(loaded.Messages, message => message.Role == "user" && message.Content == "hello");
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "LuminChatWinTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}