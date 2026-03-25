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

    [Fact]
    public void TerminalProfileStore_PersistsRenameAndSharingFlags()
    {
        var root = CreateTempDirectory();
        var path = Path.Combine(root, "terminal-profiles.json");
        var store = new TerminalProfileStore(() => path);

        var saved = store.Save(new TerminalSessionProfile
        {
            Title = "Board SSH",
            Kind = TerminalSessionKind.Ssh,
            Host = "192.168.1.20",
            Port = 22,
            Username = "root",
        });

        store.Rename(saved.ProfileId, "Board SSH Renamed");
        store.SetApiShared(saved.ProfileId, false);
        store.SetSshShared(saved.ProfileId, false);
        store.Touch(saved.ProfileId);

        var profiles = store.List();
        var profile = Assert.Single(profiles);
        Assert.Equal("Board SSH Renamed", profile.Title);
        Assert.False(profile.ApiShared);
        Assert.False(profile.SshShared);
        Assert.False(string.IsNullOrWhiteSpace(profile.LastUsedAt));
    }

    [Fact]
    public void TerminalProfileStore_UsesConnectionDescriptorAsIdentity()
    {
        var root = CreateTempDirectory();
        var path = Path.Combine(root, "terminal-profiles.json");
        var store = new TerminalProfileStore(() => path);

        store.Save(new TerminalSessionProfile
        {
            Title = "SSH 会话",
            Kind = TerminalSessionKind.Ssh,
            Host = "192.168.1.20",
            Port = 22,
            Username = "root",
        });

        store.Save(new TerminalSessionProfile
        {
            Title = "SSH 会话",
            Kind = TerminalSessionKind.Ssh,
            Host = "192.168.1.21",
            Port = 22,
            Username = "root",
        });

        var profiles = store.List();
        Assert.Equal(2, profiles.Count);
        Assert.Contains(profiles, item => item.Descriptor == "root@192.168.1.20:22");
        Assert.Contains(profiles, item => item.Descriptor == "root@192.168.1.21:22");
    }

    [Fact]
    public void TerminalProfileStore_UpdatesExistingProfileWhenDescriptorMatches()
    {
        var root = CreateTempDirectory();
        var path = Path.Combine(root, "terminal-profiles.json");
        var store = new TerminalProfileStore(() => path);

        store.Save(new TerminalSessionProfile
        {
            Title = "SSH 会话",
            Kind = TerminalSessionKind.Ssh,
            Host = "192.168.1.20",
            Port = 22,
            Username = "root",
        });

        store.Save(new TerminalSessionProfile
        {
            Title = "已改名",
            Kind = TerminalSessionKind.Ssh,
            Host = "192.168.1.20",
            Port = 22,
            Username = "root",
        });

        var profiles = store.List();
        var profile = Assert.Single(profiles);
        Assert.Equal("已改名", profile.Title);
        Assert.Equal("root@192.168.1.20:22", profile.Descriptor);
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "LuminChatWinTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}