using System.IO;
using LuminChatWin.Core.Models;
using LuminChatWin.Core.Services;
using System.Windows;

namespace LuminChatWin.App;

public sealed class AppRuntime : IAsyncDisposable
{
    private readonly OpenAiCompatibleChatClient _chatClient = new();

    public AppRuntime(string configPath, string workspaceRoot)
    {
        ConfigService = new ConfigService(configPath);
        Config = ConfigService.LoadOrCreate();
        WorkspaceRoot = ConfigService.ExpandPath(workspaceRoot);
        Directory.CreateDirectory(WorkspaceRoot);
        TerminalSessions = new TerminalSessionManager(() => Config.Terminal);
        TerminalApiServer = new TerminalApiServer(TerminalSessions, () => Config.Terminal);
        TerminalAgent = new TerminalAgentService(_chatClient, () => Config, TerminalSessions);
        TerminalProfiles = new TerminalProfileStore(() => ConfigService.ExpandPath(Config.Terminal.ProfileStorePath));
        EnsurePromptLibrary();
        _ = EnsureTerminalApiStateAsync();
    }

    public event EventHandler? ConfigChanged;

    public ConfigService ConfigService { get; }

    public AppConfig Config { get; private set; }

    public string WorkspaceRoot { get; private set; }

    public TerminalSessionManager TerminalSessions { get; }

    public TerminalApiServer TerminalApiServer { get; }

    public TerminalAgentService TerminalAgent { get; }

    public TerminalProfileStore TerminalProfiles { get; }

    public ChatAgent CreateAgent(string? sessionIdOrPath = null, string? workdir = null, Func<string, string, bool>? confirmCallback = null)
    {
        return new ChatAgent(Config, workdir ?? WorkspaceRoot, _chatClient, confirmCallback, sessionIdOrPath);
    }

    public BatchTaskRunner CreateBatchRunner(Func<string, string, bool>? confirmCallback = null)
    {
        return new BatchTaskRunner(() => CreateAgent(confirmCallback: confirmCallback), Config.App.ReportDir);
    }

    public void SaveConfig(AppConfig config)
    {
        Config = config;
        ConfigService.Save(config);
        EnsurePromptLibrary();
        if (Application.Current is not null)
        {
            ThemeManager.ApplyTheme(Application.Current.Resources, config.App.ThemeId);
        }
        _ = EnsureTerminalApiStateAsync();
        ConfigChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetWorkspaceRoot(string workspaceRoot)
    {
        WorkspaceRoot = ConfigService.ExpandPath(workspaceRoot);
        Directory.CreateDirectory(WorkspaceRoot);
    }

    public IReadOnlyList<string> GetPromptFiles()
    {
        return PromptTemplateService.ListPromptFiles(Config);
    }

    private void EnsurePromptLibrary()
    {
        var promptDir = ConfigService.ExpandPath(Config.Prompts.PromptLibraryDir);
        Directory.CreateDirectory(promptDir);
        var defaultSystem = Path.Combine(promptDir, "default-system.md");
        var defaultUser = Path.Combine(promptDir, "default-user.prompt");
        if (!File.Exists(defaultSystem))
        {
            File.WriteAllText(defaultSystem, "保持工程化、务实、精确；在没有完成目标前持续推进。\n必要时先检查工作区、git 状态和相关文件上下文。\n不要虚构执行结果。");
        }
        if (!File.Exists(defaultUser))
        {
            File.WriteAllText(defaultUser, "{input}");
        }
    }

    public async Task EnsureTerminalApiStateAsync()
    {
        if (Config.Terminal.ExecApi.Enabled)
        {
            await TerminalApiServer.StartAsync().ConfigureAwait(false);
            return;
        }

        await TerminalApiServer.StopAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await TerminalApiServer.DisposeAsync().ConfigureAwait(false);
        await TerminalSessions.DisposeAsync().ConfigureAwait(false);
    }
}