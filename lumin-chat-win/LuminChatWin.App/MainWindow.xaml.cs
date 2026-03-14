using System.Windows;
using Microsoft.Win32;

namespace LuminChatWin.App;

public partial class MainWindow : Window
{
    private readonly AppRuntime _runtime;
    private ChatWindow? _chatWindow;

    public MainWindow(AppRuntime runtime)
    {
        _runtime = runtime;
        InitializeComponent();
        _runtime.ConfigChanged += Runtime_ConfigChanged;
        RefreshSummary();
    }

    private void Runtime_ConfigChanged(object? sender, EventArgs e)
    {
        RefreshSummary();
    }

    private void RefreshSummary()
    {
        WorkspaceTextBlock.Text = _runtime.WorkspaceRoot;
        ConfigSummaryTextBlock.Text = $"默认模型级别: level {_runtime.Config.App.DefaultModelLevel}\n" +
                                      $"审批策略: {_runtime.Config.App.DefaultApprovalPolicy}\n" +
                                      $"会话目录: {_runtime.Config.App.SessionDir}\n" +
                                      $"长期记忆目录: {_runtime.Config.App.MemoryDir}\n" +
                                      $"批任务报告目录: {_runtime.Config.App.ReportDir}\n" +
                                      $"知识库: {(_runtime.Config.KnowledgeBase.Enabled ? "已启用" : "未启用")}\n" +
                                      $"辅助服务器: {(_runtime.Config.SecondaryServer.Enabled ? "已启用" : "未启用")}\n" +
                                      $"许可证: {(_runtime.Config.License.Enabled ? "启用校验" : "关闭")}\n" +
                                      $"命令策略模式: {_runtime.Config.CommandPolicy.Mode}";
    }

    private void OpenChatWindow_Click(object sender, RoutedEventArgs e)
    {
        if (_chatWindow is null || !_chatWindow.IsLoaded)
        {
            _chatWindow = new ChatWindow(_runtime);
            _chatWindow.Owner = this;
            _chatWindow.Show();
        }
        else
        {
            _chatWindow.Activate();
        }
    }

    private void OpenLlmConfig_Click(object sender, RoutedEventArgs e)
    {
        var window = new LlmConfigWindow(_runtime) { Owner = this };
        window.Show();
    }

    private void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        var window = new SettingsWindow(_runtime) { Owner = this };
        window.Show();
    }

    private void ChangeWorkspace_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Multiselect = false,
            Title = "选择工作区目录",
            InitialDirectory = _runtime.WorkspaceRoot,
        };

        if (dialog.ShowDialog() == true)
        {
            _runtime.SetWorkspaceRoot(dialog.FolderName);
            RefreshSummary();
        }
    }
}