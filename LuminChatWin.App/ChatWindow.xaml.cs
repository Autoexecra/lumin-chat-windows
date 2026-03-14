using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using LuminChatWin.Core.Models;
using LuminChatWin.Core.Services;
using Microsoft.Win32;

namespace LuminChatWin.App;

public partial class ChatWindow : Window, INotifyPropertyChanged
{
    private readonly AppRuntime _runtime;
    private ChatAgent _agent;
    private bool _isBusy;
    private string _thinkingText = string.Empty;

    public ChatWindow(AppRuntime runtime)
    {
        _runtime = runtime;
        _agent = _runtime.CreateAgent(confirmCallback: ConfirmAction);
        InitializeComponent();
        DataContext = this;
        _runtime.ConfigChanged += Runtime_ConfigChanged;
        InitializeSelectors();
        RefreshSessionHeader();
        LoadSessions();
        AddSystemMessage("欢迎使用 Windows 图形版 lumin-chat。支持直接提问，也支持使用 /new-session、/switch-session、/model、/approval、/cd、/memory、/workspace、/git-status、/git-diff 等命令。", true);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<ConversationItem> Conversation { get; } = [];

    public ObservableCollection<ToolLogItem> ToolLogs { get; } = [];

    public ObservableCollection<SessionListItem> Sessions { get; } = [];

    public string ThinkingText
    {
        get => _thinkingText;
        set
        {
            _thinkingText = value;
            OnPropertyChanged();
        }
    }

    private void Runtime_ConfigChanged(object? sender, EventArgs e)
    {
        var currentSession = _agent.CurrentSession.SessionId;
        _agent = _runtime.CreateAgent(currentSession, _agent.Cwd, ConfirmAction);
        InitializeSelectors();
        RefreshSessionHeader();
        AddToolLog("配置已更新", "已重新加载当前会话的配置。", Brushes.Transparent);
    }

    private void InitializeSelectors()
    {
        ModelLevelComboBox.ItemsSource = Enumerable.Range(1, _runtime.Config.GetMaxModelLevel()).Select(level => $"level {level}").ToList();
        ModelLevelComboBox.SelectedIndex = Math.Max(0, _agent.ModelLevel - 1);
        ApprovalPolicyComboBox.ItemsSource = new[] { "auto", "prompt", "read-only" };
        ApprovalPolicyComboBox.SelectedItem = _agent.CurrentSession.ApprovalPolicy;
    }

    private async void SendButton_Click(object sender, RoutedEventArgs e)
    {
        await SubmitPromptAsync().ConfigureAwait(false);
    }

    private async Task SubmitPromptAsync()
    {
        if (_isBusy)
        {
            return;
        }

        var text = PromptTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        PromptTextBox.Clear();
        AddUserMessage(text);
        ThinkingText = string.Empty;
        SetBusy(true, "执行中...");

        try
        {
            if (text.StartsWith("/", StringComparison.Ordinal))
            {
                var handled = await HandleSlashCommandAsync(text).ConfigureAwait(true);
                if (handled)
                {
                    return;
                }
            }

            var progress = new Progress<AgentEvent>(HandleAgentEvent);
            var result = await _agent.RunWithTraceAsync(text, progress).ConfigureAwait(true);
            if (!string.IsNullOrWhiteSpace(result.Content))
            {
                AddAssistantMessage(result.Content);
            }

            if (!result.Success && !string.IsNullOrWhiteSpace(result.Error))
            {
                AddSystemMessage(result.Error, false);
            }

            LoadSessions();
            RefreshSessionHeader();
        }
        catch (Exception ex)
        {
            AddSystemMessage($"执行失败: {ex.Message}", false);
        }
        finally
        {
            SetBusy(false, "就绪");
            ScrollConversationToEnd();
        }
    }

    private async Task<bool> HandleSlashCommandAsync(string command)
    {
        var parts = command.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var name = parts[0].ToLowerInvariant();
        var arg = parts.Length > 1 ? parts[1] : string.Empty;

        switch (name)
        {
            case "/new-session":
                _agent.CreateNewSession();
                LoadSessions();
                RefreshSessionHeader();
                AddSystemMessage("已创建新会话。", true);
                return true;
            case "/switch-session":
                if (string.IsNullOrWhiteSpace(arg))
                {
                    AddSystemMessage("用法: /switch-session <session_id>", false);
                    return true;
                }
                _agent.SwitchSession(arg);
                RefreshSessionHeader();
                LoadSessions();
                AddSystemMessage($"已切换到会话 {arg}。", true);
                return true;
            case "/model":
                if (int.TryParse(arg, out var modelLevel))
                {
                    _agent.SetModelLevel(modelLevel);
                    ModelLevelComboBox.SelectedIndex = Math.Max(0, _agent.ModelLevel - 1);
                    RefreshSessionHeader();
                    AddSystemMessage($"已切换模型到 level {_agent.ModelLevel}。", true);
                }
                else
                {
                    AddSystemMessage("用法: /model <level>", false);
                }
                return true;
            case "/approval":
                if (new[] { "auto", "prompt", "read-only" }.Contains(arg, StringComparer.OrdinalIgnoreCase))
                {
                    _agent.SetApprovalPolicy(arg);
                    ApprovalPolicyComboBox.SelectedItem = arg;
                    AddSystemMessage($"审批模式已切换为 {arg}。", true);
                }
                else
                {
                    AddSystemMessage("用法: /approval <auto|prompt|read-only>", false);
                }
                return true;
            case "/cd":
                AddSystemMessage(_agent.ChangeDirectory(arg), true);
                RefreshSessionHeader();
                return true;
            case "/memory":
                AddSystemMessage(_agent.MemorySummary(arg), true);
                return true;
            case "/workspace":
                AddSystemMessage(_agent.WorkspaceOverview(), true);
                return true;
            case "/git-status":
                var gitStatus = await new ToolExecutor(_runtime.Config, _agent.Cwd, _agent.CurrentSession.ApprovalPolicy, ConfirmAction)
                    .GitStatusAsync(_agent.Cwd)
                    .ConfigureAwait(true);
                AddSystemMessage(gitStatus.Output, gitStatus.Ok);
                return true;
            default:
                if (name == "/git-diff")
                {
                    var tool = await new ToolExecutor(_runtime.Config, _agent.Cwd, _agent.CurrentSession.ApprovalPolicy, ConfirmAction)
                        .GitDiffAsync(_agent.Cwd, arg, false, 12000)
                        .ConfigureAwait(true);
                    AddSystemMessage(tool.Output, tool.Ok);
                    return true;
                }
                return false;
        }
    }

    private void HandleAgentEvent(AgentEvent agentEvent)
    {
        switch (agentEvent.Type)
        {
            case AgentEventType.Reasoning:
                ThinkingText = string.IsNullOrWhiteSpace(ThinkingText)
                    ? agentEvent.Message
                    : ThinkingText + Environment.NewLine + Environment.NewLine + agentEvent.Message;
                break;
            case AgentEventType.ToolCall:
                AddToolLog($"工具调用: {agentEvent.ToolName}", agentEvent.Message, Brushes.Transparent);
                break;
            case AgentEventType.ToolResult:
                AddToolLog($"工具结果: {agentEvent.ToolName}", agentEvent.Message, agentEvent.ToolResult?.Ok == true ? Brushes.Transparent : Brushes.Transparent);
                break;
            case AgentEventType.Warning:
                AddSystemMessage(agentEvent.Message, false);
                break;
            case AgentEventType.Content:
                break;
        }
    }

    private void NewSession_Click(object sender, RoutedEventArgs e)
    {
        _agent.CreateNewSession();
        LoadSessions();
        RefreshSessionHeader();
        AddSystemMessage("已创建新会话。", true);
    }

    private void RefreshSessions_Click(object sender, RoutedEventArgs e)
    {
        LoadSessions();
    }

    private void ModelLevelComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!IsLoaded || ModelLevelComboBox.SelectedIndex < 0)
        {
            return;
        }

        _agent.SetModelLevel(ModelLevelComboBox.SelectedIndex + 1);
        RefreshSessionHeader();
    }

    private void ApprovalPolicyComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!IsLoaded || ApprovalPolicyComboBox.SelectedItem is not string selected)
        {
            return;
        }

        _agent.SetApprovalPolicy(selected);
    }

    private void SessionListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SessionListBox.SelectedItem is not SessionListItem item)
        {
            return;
        }

        _agent.SwitchSession(item.SessionId);
        RefreshSessionHeader();
        AddSystemMessage($"已切换到会话 {item.SessionId}。", true);
    }

    private void OpenLlmConfig_Click(object sender, RoutedEventArgs e)
    {
        new LlmConfigWindow(_runtime) { Owner = this }.Show();
    }

    private void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        new SettingsWindow(_runtime) { Owner = this }.Show();
    }

    private async void RunBatch_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择批量任务 JSON 文件",
            Filter = "JSON Files|*.json|All Files|*.*",
            InitialDirectory = _agent.Cwd,
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        SetBusy(true, "正在执行批任务...");
        try
        {
            var runner = _runtime.CreateBatchRunner(ConfirmAction);
            var results = await runner.RunFileAsync(dialog.FileName).ConfigureAwait(true);
            var successCount = results.Count(static item => item.Success);
            AddSystemMessage($"批任务执行完成，共 {results.Count} 项，成功 {successCount} 项。报告已写入 {_runtime.Config.App.ReportDir}。", true);
        }
        catch (Exception ex)
        {
            AddSystemMessage($"批任务执行失败: {ex.Message}", false);
        }
        finally
        {
            SetBusy(false, "就绪");
        }
    }

    private void ClearInput_Click(object sender, RoutedEventArgs e)
    {
        PromptTextBox.Clear();
        PromptTextBox.Focus();
    }

    private async void PromptTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            await SubmitPromptAsync().ConfigureAwait(true);
        }
    }

    private void RefreshSessionHeader()
    {
        SessionHeaderTextBlock.Text = $"会话 {_agent.CurrentSession.SessionId} | {_agent.DescribeModel()} | {_agent.Cwd}";
    }

    private void LoadSessions()
    {
        Sessions.Clear();
        foreach (var session in _agent.ListSessions(24))
        {
            Sessions.Add(new SessionListItem(session["session_id"], session["preview"], session["cwd"]));
        }
    }

    private void SetBusy(bool isBusy, string status)
    {
        _isBusy = isBusy;
        SendButton.IsEnabled = !isBusy;
        PromptTextBox.IsEnabled = !isBusy;
        StatusTextBlock.Text = status;
    }

    private void AddUserMessage(string text)
    {
        Conversation.Add(new ConversationItem("你", text, HorizontalAlignment.Right, (Brush)Application.Current.Resources["AccentSoftBrush"], DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture)));
    }

    private void AddAssistantMessage(string text)
    {
        Conversation.Add(new ConversationItem("Lumin", text, HorizontalAlignment.Left, (Brush)Application.Current.Resources["PanelBrush"], DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture)));
        ScrollConversationToEnd();
    }

    private void AddSystemMessage(string text, bool success)
    {
        var brush = success ? (Brush)Application.Current.Resources["AccentSoftBrush"] : (Brush)Application.Current.Resources["PanelBrush"];
        Conversation.Add(new ConversationItem(success ? "系统" : "系统提示", text, HorizontalAlignment.Left, brush, DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture)));
        ScrollConversationToEnd();
    }

    private void AddToolLog(string title, string message, Brush brush)
    {
        ToolLogs.Insert(0, new ToolLogItem(title, message, DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture)));
    }

    private void ScrollConversationToEnd()
    {
        ConversationScrollViewer.ScrollToEnd();
    }

    private bool ConfirmAction(string toolName, string summary)
    {
        return MessageBox.Show(this, $"允许执行 {toolName}?\n\n{summary}", "审批确认", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public sealed record ConversationItem(string Header, string Content, HorizontalAlignment Alignment, Brush Background, string Timestamp);

    public sealed record ToolLogItem(string Title, string Message, string Timestamp);

    public sealed record SessionListItem(string SessionId, string Preview, string Cwd);
}