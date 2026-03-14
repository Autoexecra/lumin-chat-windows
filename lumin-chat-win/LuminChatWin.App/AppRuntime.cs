using System.IO;
using LuminChatWin.Core.Models;
using LuminChatWin.Core.Services;

namespace LuminChatWin.App;

public sealed class AppRuntime
{
    private readonly OpenAiCompatibleChatClient _chatClient = new();

    public AppRuntime(string configPath, string workspaceRoot)
    {
        ConfigService = new ConfigService(configPath);
        Config = ConfigService.LoadOrCreate();
        WorkspaceRoot = ConfigService.ExpandPath(workspaceRoot);
        Directory.CreateDirectory(WorkspaceRoot);
    }

    public event EventHandler? ConfigChanged;

    public ConfigService ConfigService { get; }

    public AppConfig Config { get; private set; }

    public string WorkspaceRoot { get; private set; }

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
        ConfigChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetWorkspaceRoot(string workspaceRoot)
    {
        WorkspaceRoot = ConfigService.ExpandPath(workspaceRoot);
        Directory.CreateDirectory(WorkspaceRoot);
    }
}