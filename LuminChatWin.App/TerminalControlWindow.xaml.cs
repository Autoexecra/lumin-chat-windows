using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO.Ports;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LuminChatWin.Core.Models;
using LuminChatWin.Core.Services;
using Microsoft.Win32;

namespace LuminChatWin.App;

public partial class TerminalControlWindow : Window
{
    private const double ExpandedNavigationPaneWidth = 260;
    private const double CollapsedNavigationPaneWidth = 76;
    private readonly AppRuntime _runtime;
    private readonly ObservableCollection<TerminalProfileViewModel> _profiles = [];
    private readonly ObservableCollection<OpenTerminalSessionViewModel> _openSessions = [];
    private readonly ObservableCollection<AgentTimelineItemViewModel> _agentTimeline = [];
    private readonly List<TerminalAgentDialogueItem> _agentDialogue = [];
    private readonly List<TerminalAgentDialogueItem> _promptDialogue = [];
    private readonly List<AgentModelOption> _agentModelOptions = [];
    private string _promptObjective = string.Empty;
    private string? _loadedProfileId;
    private TerminalSessionKind? _loadedProfileKind;
    private string? _loadedProfileDescriptor;
    private bool _agentBusy;
    private double _lastExpandedNavigationPaneWidth = ExpandedNavigationPaneWidth;

    public TerminalControlWindow(AppRuntime runtime)
    {
        _runtime = runtime;
        InitializeComponent();

        ProfilesListBox.ItemsSource = _profiles;
        OpenSessionsTabControl.ItemsSource = _openSessions;
        AgentTimelineListBox.ItemsSource = _agentTimeline;

        PowerShellProgramTextBox.Text = runtime.Config.Terminal.DefaultPowershellProgram;
        PowerShellArgsTextBox.Text = runtime.Config.Terminal.DefaultPowershellArgs;
        PowerShellWorkdirTextBox.Text = runtime.WorkspaceRoot;
        ApiEnabledCheckBox.IsChecked = runtime.Config.Terminal.ExecApi.Enabled;
        ApiHostTextBox.Text = runtime.Config.Terminal.ExecApi.BindHost;
        ApiPortTextBox.Text = runtime.Config.Terminal.ExecApi.Port.ToString();
        ApiTimeoutTextBox.Text = runtime.Config.Terminal.ExecApi.DefaultTimeoutSeconds.ToString("0.##");

        _runtime.TerminalSessions.OutputReceived += TerminalSessions_OutputReceived;
        _runtime.ConfigChanged += Runtime_ConfigChanged;
        Closed += TerminalControlWindow_Closed;
        Loaded += (_, _) => ClampWindowToDesktop();

        RefreshSerialPorts();
        RefreshProfiles();
        RefreshOpenSessions();
        RefreshModelChoices();
        RefreshAgentTargets();
        RefreshPromptTargets();
        RefreshApiSummary();
        RefreshBridgeTargets();
        ProfileHintTextBlock.Text = "开启 SSH 共享的会话会在打开时自动启动 SSH bridge，API 共享则决定是否暴露到本地 HTTP API。";
        ResetRequestStatus(AgentStatusTextBlock);
        AgentSummaryTextBlock.Text = "未开始执行。选择目标会话、模型和需求后发送给 Agent。";
        ResetRequestStatus(PromptStatusTextBlock);
        PromptSummaryTextBlock.Text = "未开始执行。生成建议命令后手动执行。";
        NavigationPaneColumn.Width = new GridLength(ExpandedNavigationPaneWidth);
        SetWindowStatus("终端工作台已就绪。焦点进入终端正文后可直接输入。", isMuted: true);
    }

    private string? SelectedOpenSessionId => (OpenSessionsTabControl.SelectedItem as OpenTerminalSessionViewModel)?.SessionId;

    private void Runtime_ConfigChanged(object? sender, EventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            RefreshApiSummary();
            RefreshModelChoices();
            RefreshProfiles();
            RefreshSessionSummary();
            RefreshPromptTargets();
            RefreshBridgeTargets();
        });
    }

    private void TerminalControlWindow_Closed(object? sender, EventArgs e)
    {
        _runtime.TerminalSessions.OutputReceived -= TerminalSessions_OutputReceived;
        _runtime.ConfigChanged -= Runtime_ConfigChanged;
        Closed -= TerminalControlWindow_Closed;
    }

    private void TerminalSessions_OutputReceived(object? sender, TerminalOutputEventArgs e)
    {
        Dispatcher.InvokeAsync(() =>
        {
            var viewModel = _openSessions.FirstOrDefault(item => item.SessionId == e.SessionId);
            if (viewModel is null)
            {
                RefreshOpenSessions(e.SessionId);
                return;
            }

            viewModel.RawOutputText = _runtime.TerminalSessions.GetRecentRawOutput(e.SessionId);
            viewModel.OutputText = _runtime.TerminalSessions.GetRecentOutput(e.SessionId);
            viewModel.CurrentCommandText = BuildCurrentCommandText(e.SessionId);
            viewModel.MetaLine = BuildMetaLine(_runtime.TerminalSessions.GetSession(e.SessionId));
            RefreshSessionSummary();
        });
    }

    private void RefreshAll_Click(object sender, RoutedEventArgs e)
    {
        RefreshProfiles();
        RefreshOpenSessions();
        RefreshAgentTargets();
        RefreshPromptTargets();
        RefreshSerialPorts();
        RefreshApiSummary();
        RefreshBridgeTargets();
        SetWindowStatus("已刷新会话、串口、Agent 目标与共享状态。", isMuted: true);
    }

    private void RefreshOpenSessions_Click(object sender, RoutedEventArgs e)
    {
        RefreshOpenSessions();
    }

    private void OpenLlmConfig_Click(object sender, RoutedEventArgs e)
    {
        var window = new LlmConfigWindow(_runtime)
        {
            Owner = this,
        };
        window.Show();
    }

    private void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        var window = new SettingsWindow(_runtime)
        {
            Owner = this,
        };
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

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        _runtime.SetWorkspaceRoot(dialog.FolderName);
        SetWindowStatus($"工作区已切换到：{_runtime.WorkspaceRoot}", isMuted: false);
    }

    private void RefreshAgentTargets_Click(object sender, RoutedEventArgs e)
    {
        RefreshAgentTargets();
        RefreshPromptTargets();
    }

    private void RefreshPromptTargets_Click(object sender, RoutedEventArgs e)
    {
        RefreshPromptTargets();
    }

    private void ProfilesListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ProfilesListBox.SelectedItem is TerminalProfileViewModel profile)
        {
            _ = OpenProfileAsync(profile.Profile);
        }
    }

    private void OpenSelectedProfile_Click(object sender, RoutedEventArgs e)
    {
        if (ProfilesListBox.SelectedItem is TerminalProfileViewModel profile)
        {
            _ = OpenProfileAsync(profile.Profile);
        }
    }

    private void LoadSelectedProfileToForm_Click(object sender, RoutedEventArgs e)
    {
        if (ProfilesListBox.SelectedItem is TerminalProfileViewModel profile)
        {
            LoadProfileIntoForms(profile.Profile);
        }
    }

    private void OpenProfile_Click(object sender, RoutedEventArgs e)
    {
        if (TryGetProfileParameter(sender, out var profile))
        {
            _ = OpenProfileAsync(profile.Profile);
        }
    }

    private void LoadProfileToForm_Click(object sender, RoutedEventArgs e)
    {
        if (TryGetProfileParameter(sender, out var profile))
        {
            LoadProfileIntoForms(profile.Profile);
        }
    }

    private void RenameProfile_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetProfileParameter(sender, out var profile))
        {
            return;
        }

        var dialog = new TextPromptWindow("重命名会话", "输入新的会话名称", profile.Title)
        {
            Owner = this,
        };

        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.ResponseText))
        {
            return;
        }

        try
        {
            _runtime.TerminalProfiles.Rename(profile.ProfileId, dialog.ResponseText.Trim());
            RefreshProfiles(profile.ProfileId);
            SetWindowStatus($"已重命名配置会话：{dialog.ResponseText.Trim()}", isMuted: true);
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetProfileParameter(sender, out var profile))
        {
            return;
        }

        if (MessageBox.Show(this, $"删除配置会话“{profile.Title}”？", "删除会话配置", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            _runtime.TerminalProfiles.Delete(profile.ProfileId);
            RefreshProfiles();
            SetWindowStatus($"已删除配置会话：{profile.Title}", isMuted: true);
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private void ToggleProfileSshShare_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetProfileParameter(sender, out var profile))
        {
            return;
        }

        try
        {
            var updated = _runtime.TerminalProfiles.SetSshShared(profile.ProfileId, !profile.Profile.SshShared);
            EnsureBridgePortAssignment(updated);
            RefreshProfiles(profile.ProfileId);
            SetWindowStatus($"已切换 SSH 共享：{profile.Title}", isMuted: true);
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private void ToggleProfileApiShare_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetProfileParameter(sender, out var profile))
        {
            return;
        }

        try
        {
            _runtime.TerminalProfiles.SetApiShared(profile.ProfileId, !profile.Profile.ApiShared);
            RefreshProfiles(profile.ProfileId);
            SetWindowStatus($"已切换 API 共享：{profile.Title}", isMuted: true);
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private void SavePowerShellProfile_Click(object sender, RoutedEventArgs e)
    {
        SaveProfile(BuildPowerShellProfile(), openAfterSave: false);
    }

    private void SaveAndOpenPowerShellProfile_Click(object sender, RoutedEventArgs e)
    {
        SaveProfile(BuildPowerShellProfile(), openAfterSave: true);
    }

    private void SaveSshProfile_Click(object sender, RoutedEventArgs e)
    {
        SaveProfile(BuildSshProfile(), openAfterSave: false);
    }

    private void SaveAndOpenSshProfile_Click(object sender, RoutedEventArgs e)
    {
        SaveProfile(BuildSshProfile(), openAfterSave: true);
    }

    private void SaveTelnetProfile_Click(object sender, RoutedEventArgs e)
    {
        SaveProfile(BuildTelnetProfile(), openAfterSave: false);
    }

    private void SaveAndOpenTelnetProfile_Click(object sender, RoutedEventArgs e)
    {
        SaveProfile(BuildTelnetProfile(), openAfterSave: true);
    }

    private void RefreshSerialPorts_Click(object sender, RoutedEventArgs e)
    {
        RefreshSerialPorts();
    }

    private void SaveSerialProfile_Click(object sender, RoutedEventArgs e)
    {
        SaveProfile(BuildSerialProfile(), openAfterSave: false);
    }

    private void SaveAndOpenSerialProfile_Click(object sender, RoutedEventArgs e)
    {
        SaveProfile(BuildSerialProfile(), openAfterSave: true);
    }

    private async void SendAgentRequest_Click(object sender, RoutedEventArgs e)
    {
        if (_agentBusy)
        {
            return;
        }

        var request = AgentRequestTextBox.Text.Trim();
        var targetSessionId = GetAgentTargetSessionId();
        if (string.IsNullOrWhiteSpace(request))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(targetSessionId))
        {
            MessageBox.Show(this, "请先选择一个打开中的终端会话。", "Agent", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            _agentBusy = true;
            PersistAgentPreferences();
            AddAgentTimeline("user", request);
            _agentDialogue.Add(new TerminalAgentDialogueItem { Role = "user", Content = request });
            AgentRequestTextBox.Clear();
            SetRequestStatus(AgentStatusTextBlock, request);
            AgentSummaryTextBlock.Text = $"目标会话：{targetSessionId} | 模型：{GetSelectedModelKey()}";
            SetWindowStatus("Agent 正在自动规划并执行。", isMuted: false);

            var plan = await _runtime.TerminalAgent.RunLoopAsync(
                targetSessionId,
                request,
                _agentDialogue,
                GetSelectedModelKey(),
                TerminalAgentMode.Auto,
                CreateAgentProgress());

            if (!plan.Success)
            {
                AddAgentTimeline("agent", $"规划失败: {plan.Error}");
                ResetRequestStatus(AgentStatusTextBlock);
                AgentSummaryTextBlock.Text = plan.Error;
                SetWindowStatus("Agent 规划失败。", isMuted: false);
                return;
            }

            if (!string.IsNullOrWhiteSpace(plan.Analysis))
            {
                AddAgentTimeline("agent", plan.Analysis);
                _agentDialogue.Add(new TerminalAgentDialogueItem { Role = "agent", Content = plan.Analysis });
            }

            if (!string.IsNullOrWhiteSpace(plan.FinalMessage))
            {
                AddAgentTimeline("agent", plan.FinalMessage);
            }

            if (plan.Completed)
            {
                ResetRequestStatus(AgentStatusTextBlock);
                AgentSummaryTextBlock.Text = string.IsNullOrWhiteSpace(plan.FinalMessage) ? "Agent 判断任务已完成。" : plan.FinalMessage;
                SetWindowStatus("Agent 已完成当前任务。", isMuted: false);
                RefreshOpenSessions(targetSessionId);
            }
            else if (plan.NeedInput)
            {
                ResetRequestStatus(AgentStatusTextBlock);
                AgentSummaryTextBlock.Text = string.IsNullOrWhiteSpace(plan.FinalMessage) ? "Agent 需要更多上下文。" : plan.FinalMessage;
                SetWindowStatus("Agent 需要人工补充信息。", isMuted: false);
            }
            else
            {
                ResetRequestStatus(AgentStatusTextBlock);
                AgentSummaryTextBlock.Text = string.IsNullOrWhiteSpace(plan.SuggestedCommand) ? "本轮未生成可执行命令。" : $"最后建议命令：{plan.SuggestedCommand}";
                SetWindowStatus("Agent 已停止自动流程。", isMuted: true);
            }
        }
        catch (Exception ex)
        {
            ResetRequestStatus(AgentStatusTextBlock);
            AgentSummaryTextBlock.Text = ex.Message;
            ShowError(ex);
        }
        finally
        {
            _agentBusy = false;
        }
    }

    private async void SendPromptRequest_Click(object sender, RoutedEventArgs e)
    {
        if (_agentBusy)
        {
            return;
        }

        var request = PromptRequestTextBox.Text.Trim();
        var targetSessionId = GetPromptTargetSessionId();
        if (string.IsNullOrWhiteSpace(request))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(targetSessionId))
        {
            MessageBox.Show(this, "请先选择一个打开中的终端会话。", "提示模式", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            _agentBusy = true;
            _promptObjective = request;
            _promptDialogue.Clear();
            _promptDialogue.Add(new TerminalAgentDialogueItem { Role = "user", Content = request });
            AddAgentTimeline("user", $"[提示模式] {request}");
            PromptRequestTextBox.Clear();
            SetRequestStatus(PromptStatusTextBlock, request);
            PromptSummaryTextBlock.Text = $"目标会话：{targetSessionId} | 模型：{GetPromptSelectedModelKey()}";

            var plan = await _runtime.TerminalAgent.RunLoopAsync(
                targetSessionId,
                _promptObjective,
                _promptDialogue,
                GetPromptSelectedModelKey(),
                TerminalAgentMode.Prompt,
                CreateAgentProgress());

            ApplyPromptPlan(plan);
        }
        catch (Exception ex)
        {
            ResetRequestStatus(PromptStatusTextBlock);
            ShowError(ex);
        }
        finally
        {
            _agentBusy = false;
        }
    }

    private async void ExecutePromptCommand_Click(object sender, RoutedEventArgs e)
    {
        if (_agentBusy)
        {
            return;
        }

        var targetSessionId = GetPromptTargetSessionId();
        var command = PromptSuggestedCommandTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(targetSessionId) || string.IsNullOrWhiteSpace(command))
        {
            return;
        }

        try
        {
            _agentBusy = true;
            SetRequestStatus(PromptStatusTextBlock, _promptObjective);
            var result = await _runtime.TerminalSessions.ExecuteCommandAsync(
                targetSessionId,
                command,
                TimeSpan.FromSeconds(Math.Max(3, _runtime.Config.Terminal.ExecApi.DefaultTimeoutSeconds)));
            var executionNote = BuildExecutionNote(result);
            _promptDialogue.Add(new TerminalAgentDialogueItem { Role = "system", Content = executionNote });
            AddAgentTimeline("system", $"[提示模式] 已执行: {result.Command}");
            RefreshOpenSessions(targetSessionId);

            var nextPlan = await _runtime.TerminalAgent.RunLoopAsync(
                targetSessionId,
                _promptObjective,
                _promptDialogue,
                GetPromptSelectedModelKey(),
                TerminalAgentMode.Prompt,
                CreateAgentProgress());

            ApplyPromptPlan(nextPlan);
            SetWindowStatus("已执行提示模式建议命令，并重新规划下一步。", isMuted: false);
        }
        catch (Exception ex)
        {
            ResetRequestStatus(PromptStatusTextBlock);
            ShowError(ex);
        }
        finally
        {
            _agentBusy = false;
        }
    }

    private async void SaveSharingSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var config = _runtime.ConfigService.LoadOrCreate();
            config.Terminal.DefaultPowershellProgram = PowerShellProgramTextBox.Text;
            config.Terminal.DefaultPowershellArgs = PowerShellArgsTextBox.Text;
            config.Terminal.Agent.AutoExecute = true;
            config.Terminal.Agent.SelectedModel = GetSelectedModelKey();
            config.Terminal.ExecApi.Enabled = ApiEnabledCheckBox.IsChecked == true;
            config.Terminal.ExecApi.BindHost = ApiHostTextBox.Text;
            config.Terminal.ExecApi.Port = ParseInt(ApiPortTextBox.Text, 8765);
            config.Terminal.ExecApi.DefaultTimeoutSeconds = ParseDouble(ApiTimeoutTextBox.Text, 15);
            _runtime.SaveConfig(config);
            await _runtime.EnsureTerminalApiStateAsync();
            RefreshApiSummary();
            SetWindowStatus("已保存共享与 Agent 偏好设置。", isMuted: true);
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private void SaveBridgeSettings_Click(object sender, RoutedEventArgs e)
    {
        if (BridgeSessionComboBox.SelectedItem is not BridgeTargetItem target)
        {
            MessageBox.Show(this, "请先选择一个会话。", "SSH Bridge", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            var port = ParseInt(BridgePortTextBox.Text, 0);
            if (port <= 0)
            {
                throw new InvalidOperationException("桥接端口必须是有效的正整数。");
            }

            var config = _runtime.ConfigService.LoadOrCreate();
            config.Terminal.SerialSshBridge.PortOverrides[target.PortKey] = port;
            if (!string.IsNullOrWhiteSpace(target.LegacyPortKey))
            {
                config.Terminal.SerialSshBridge.PortOverrides.Remove(target.LegacyPortKey);
            }
            _runtime.SaveConfig(config);
            var usernameHint = string.IsNullOrWhiteSpace(config.Terminal.SerialSshBridge.Username) ? "任意用户名" : config.Terminal.SerialSshBridge.Username;
            var passwordHint = string.IsNullOrEmpty(config.Terminal.SerialSshBridge.Password) ? "空密码" : "已配置密码";
            BridgeStatusTextBlock.Text = $"已保存 {target.Label} 的共享端口: {config.Terminal.SerialSshBridge.BindHost}:{port}，账号: {usernameHint}，认证: {passwordHint}";
            SetWindowStatus($"已保存 SSH bridge 端口：{target.Label} -> {port}", isMuted: true);
            RefreshBridgeTargets(target.PortKey);
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private async void OpenBridge_Click(object sender, RoutedEventArgs e)
    {
        if (BridgeSessionComboBox.SelectedItem is not BridgeTargetItem target)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(target.SessionId))
        {
            MessageBox.Show(this, "这个会话当前没有打开实例。请先打开对应会话，再手工打开共享端口。", "SSH Bridge", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            var bridge = await _runtime.TerminalSessions.StartSerialBridgeAsync(
                target.SessionId,
                string.IsNullOrWhiteSpace(BridgePortTextBox.Text) ? null : ParseInt(BridgePortTextBox.Text, target.SuggestedPort));
            BridgeStatusTextBlock.Text = $"{target.Label}: {bridge.Protocol}://{bridge.Host}:{bridge.Port}  {bridge.Message}";
            SetWindowStatus($"已打开共享端口：{target.Label} -> {bridge.Host}:{bridge.Port}", isMuted: true);
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private void BridgeSessionComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (BridgeSessionComboBox.SelectedItem is not BridgeTargetItem target)
        {
            return;
        }

        BridgePortTextBox.Text = ResolveConfiguredBridgePort(target).ToString();
    }

    private void OpenSessionsTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        RefreshSessionSummary();
        RefreshAgentTargets();
        FocusSelectedTerminalViewport();
    }

    private void TerminalViewport_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is AnsiTerminalBox terminalBox)
        {
            terminalBox.FocusTerminalInput();
        }
    }

    private void TerminalViewport_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox textBox)
        {
            textBox.ScrollToEnd();
            textBox.CaretIndex = textBox.Text.Length;
        }
    }

    private async void TerminalViewport_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (!TryGetOpenSessionParameter(sender, out var session) || string.IsNullOrEmpty(e.Text))
        {
            return;
        }

        e.Handled = true;
        await SendTerminalInputAsync(session.SessionId, e.Text);
    }

    private async void TerminalViewport_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!TryGetOpenSessionParameter(sender, out var session))
        {
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.C)
        {
            var selectedText = sender switch
            {
                TextBox textBox => textBox.SelectedText,
                RichTextBox richTextBox => richTextBox.Selection.Text,
                _ => string.Empty,
            };

            if (!string.IsNullOrEmpty(selectedText))
            {
                return;
            }

            e.Handled = true;
            await SendTerminalInputAsync(session.SessionId, "\u0003");
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.V)
        {
            e.Handled = true;
            var pastedText = Clipboard.ContainsText() ? Clipboard.GetText() : string.Empty;
            if (!string.IsNullOrEmpty(pastedText))
            {
                await SendTerminalInputAsync(session.SessionId, pastedText);
            }
            return;
        }

        var payload = TryMapTerminalKey(e.Key, Keyboard.Modifiers);

        if (payload is null)
        {
            return;
        }

        e.Handled = true;
        await SendTerminalInputAsync(session.SessionId, payload);
    }

    private void NavigationTabControl_PreviewMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<TabItem>(e.OriginalSource as DependencyObject) is null)
        {
            return;
        }

        ToggleNavigationPane();
        e.Handled = true;
    }

    private async Task SendTerminalInputAsync(string sessionId, string text)
    {
        try
        {
            await _runtime.TerminalSessions.SendInputAsync(sessionId, text, recordInHistory: false);
            SetWindowStatus($"已向终端发送输入：{DescribeInput(text)}", isMuted: true);
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private async Task CloseSessionAsync(string sessionId)
    {
        try
        {
            var session = _runtime.TerminalSessions.GetSession(sessionId);
            await _runtime.TerminalSessions.StopSessionAsync(sessionId);
            RefreshOpenSessions();
            RefreshAgentTargets();
            SetWindowStatus($"已关闭会话：{session?.Title ?? sessionId}", isMuted: true);
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private async void CloseCurrentSession_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(SelectedOpenSessionId))
        {
            return;
        }

        await CloseSessionAsync(SelectedOpenSessionId);
    }

    private void SaveProfile(TerminalSessionProfile profile, bool openAfterSave)
    {
        try
        {
            var saved = _runtime.TerminalProfiles.Save(profile);
            EnsureBridgePortAssignment(saved);
            RefreshProfiles(saved.ProfileId);
            _loadedProfileId = saved.ProfileId;
            _loadedProfileKind = saved.Kind;
            _loadedProfileDescriptor = saved.Descriptor;
            ProfileHintTextBlock.Text = $"已保存 {saved.KindLabel} 配置：{saved.Title}";
            SetWindowStatus($"已保存配置会话：{saved.Title}", isMuted: true);
            if (openAfterSave)
            {
                _ = OpenProfileAsync(saved);
            }
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private async Task OpenProfileAsync(TerminalSessionProfile profile)
    {
        try
        {
            if (profile.ApiShared && !_runtime.Config.Terminal.ExecApi.Enabled)
            {
                var config = _runtime.ConfigService.LoadOrCreate();
                config.Terminal.ExecApi.Enabled = true;
                _runtime.SaveConfig(config);
                await _runtime.EnsureTerminalApiStateAsync();
            }

            TerminalSessionInfo session = profile.Kind switch
            {
                TerminalSessionKind.PowerShell => await _runtime.TerminalSessions.CreatePowerShellSessionAsync(new TerminalPowerShellOptions
                {
                    Title = profile.Title,
                    Program = profile.Program,
                    Arguments = profile.Arguments,
                    WorkingDirectory = profile.WorkingDirectory,
                    ApiShared = profile.ApiShared,
                    SshShared = profile.SshShared,
                }),
                TerminalSessionKind.Ssh => await _runtime.TerminalSessions.CreateSshSessionAsync(new TerminalSshOptions
                {
                    Title = profile.Title,
                    Host = profile.Host,
                    Port = profile.Port,
                    Username = profile.Username,
                    Password = profile.Password,
                    ApiShared = profile.ApiShared,
                    SshShared = profile.SshShared,
                }),
                TerminalSessionKind.Telnet => await _runtime.TerminalSessions.CreateTelnetSessionAsync(new TerminalTelnetOptions
                {
                    Title = profile.Title,
                    Host = profile.Host,
                    Port = profile.Port,
                    ApiShared = profile.ApiShared,
                    SshShared = profile.SshShared,
                }),
                TerminalSessionKind.Serial => await _runtime.TerminalSessions.CreateSerialSessionAsync(new TerminalSerialOptions
                {
                    Title = profile.Title,
                    PortName = profile.PortName,
                    BaudRate = profile.BaudRate,
                    Parity = profile.Parity,
                    DataBits = profile.DataBits,
                    StopBits = profile.StopBits,
                    NewLine = profile.NewLine,
                    ApiShared = profile.ApiShared,
                    SshShared = profile.SshShared,
                }),
                _ => throw new InvalidOperationException($"Unsupported profile kind: {profile.Kind}"),
            };

            _runtime.TerminalProfiles.Touch(profile.ProfileId);
            if (profile.SshShared && session.SupportsBridge)
            {
                var bridge = await _runtime.TerminalSessions.StartSerialBridgeAsync(session.SessionId);
                BridgeStatusTextBlock.Text = $"自动 SSH bridge 已开启: {bridge.Protocol}://{bridge.Host}:{bridge.Port}";
            }

            RefreshProfiles(profile.ProfileId);
            RefreshOpenSessions(session.SessionId);
            RefreshAgentTargets(session.SessionId);
            RefreshApiSummary();
            RefreshBridgeTargets();
            FocusSelectedTerminalViewport();
            SetWindowStatus($"已打开会话：{profile.Title}", isMuted: false);
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private void RefreshProfiles(string? selectedProfileId = null)
    {
        var selected = selectedProfileId ?? (ProfilesListBox.SelectedItem as TerminalProfileViewModel)?.ProfileId;
        _profiles.Clear();
        foreach (var profile in _runtime.TerminalProfiles.List().Select(static item => new TerminalProfileViewModel(item)))
        {
            _profiles.Add(profile);
        }

        if (!string.IsNullOrWhiteSpace(selected))
        {
            ProfilesListBox.SelectedItem = _profiles.FirstOrDefault(item => item.ProfileId == selected);
        }
    }

    private void RefreshOpenSessions(string? selectedSessionId = null)
    {
        var selected = selectedSessionId ?? SelectedOpenSessionId;
        _openSessions.Clear();

        foreach (var session in _runtime.TerminalSessions.ListSessions())
        {
            _openSessions.Add(new OpenTerminalSessionViewModel(session)
            {
                RawOutputText = _runtime.TerminalSessions.GetRecentRawOutput(session.SessionId),
                OutputText = _runtime.TerminalSessions.GetRecentOutput(session.SessionId),
                CurrentCommandText = BuildCurrentCommandText(session.SessionId),
                MetaLine = BuildMetaLine(session),
            });
        }

        if (!string.IsNullOrWhiteSpace(selected))
        {
            OpenSessionsTabControl.SelectedItem = _openSessions.FirstOrDefault(item => item.SessionId == selected);
        }
        else if (_openSessions.Count > 0)
        {
            OpenSessionsTabControl.SelectedIndex = 0;
        }

        EmptySessionsBorder.Visibility = _openSessions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RefreshSessionSummary();
        RefreshBridgeTargets();
        FocusSelectedTerminalViewport();
    }

    private void RefreshAgentTargets(string? preferredSessionId = null)
    {
        var selected = preferredSessionId ?? GetAgentTargetSessionId() ?? SelectedOpenSessionId;
        AgentTargetSessionComboBox.ItemsSource = _openSessions
            .Select(item => new AgentTargetSessionItem(item.SessionId, $"{item.KindLabel} | {item.Title}"))
            .ToList();
        if (!string.IsNullOrWhiteSpace(selected))
        {
            AgentTargetSessionComboBox.SelectedValue = selected;
        }
        else if (AgentTargetSessionComboBox.Items.Count > 0)
        {
            AgentTargetSessionComboBox.SelectedIndex = 0;
        }
    }

    private void RefreshPromptTargets(string? preferredSessionId = null)
    {
        var selected = preferredSessionId ?? GetPromptTargetSessionId() ?? SelectedOpenSessionId;
        PromptTargetSessionComboBox.ItemsSource = _openSessions
            .Select(item => new AgentTargetSessionItem(item.SessionId, $"{item.KindLabel} | {item.Title}"))
            .ToList();
        if (!string.IsNullOrWhiteSpace(selected))
        {
            PromptTargetSessionComboBox.SelectedValue = selected;
        }
        else if (PromptTargetSessionComboBox.Items.Count > 0)
        {
            PromptTargetSessionComboBox.SelectedIndex = 0;
        }
    }

    private void RefreshSerialPorts()
    {
        var current = SerialPortComboBox.Text;
        SerialPortComboBox.ItemsSource = SerialPort.GetPortNames().OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList();
        if (!string.IsNullOrWhiteSpace(current))
        {
            SerialPortComboBox.Text = current;
        }
    }

    private void RefreshApiSummary()
    {
        ApiSummaryTextBlock.Text = _runtime.TerminalApiServer.IsRunning
            ? $"API: {_runtime.TerminalApiServer.BaseUrl}"
            : $"API: 已关闭 http://{ApiHostTextBox.Text}:{ApiPortTextBox.Text}/";
        ApiEndpointsTextBlock.Text =
            "GET /api/sessions\nGET /api/sessions/{id}/history\nGET /api/sessions/{id}/current-output\nPOST /api/exec_cmd\nPOST /api/send_input\nPOST /api/bridge/open";
        RefreshSessionSummary();
    }

    private void RefreshBridgeTargets(string? preferredPortKey = null)
    {
        var openTargets = _openSessions
            .Where(item => item.Session.SupportsBridge)
            .Select(item => new BridgeTargetItem(
                item.Session.Kind,
                ResolveBridgePortKey(item.Session.Kind, item.Descriptor),
                $"{item.KindLabel} | {item.Title} | {item.Descriptor}",
                item.SessionId,
                0,
                ResolveLegacyBridgePortKey(item.Session.Kind, item.Descriptor)))
            .Where(item => !string.IsNullOrWhiteSpace(item.PortKey))
            .ToList();

        var configuredTargets = _runtime.TerminalProfiles.List()
            .Where(item => SupportsBridge(item.Kind))
            .Select(item =>
            {
                var portKey = ResolveBridgePortKey(item.Kind, item.Descriptor);
                var openMatch = openTargets.FirstOrDefault(open => string.Equals(open.PortKey, portKey, StringComparison.OrdinalIgnoreCase));
                return new BridgeTargetItem(item.Kind, portKey, $"{item.KindLabel} | {item.Title} | {item.Descriptor}", openMatch?.SessionId, 0, ResolveLegacyBridgePortKey(item.Kind, item.Descriptor));
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.PortKey))
            .ToList();

        var config = _runtime.Config.Terminal.SerialSshBridge;
        var occupiedPorts = new HashSet<int>(config.PortOverrides.Values);
        var items = openTargets
            .Concat(configuredTargets)
            .GroupBy(item => item.PortKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(item => item.Kind)
            .ThenBy(item => item.PortKey, StringComparer.OrdinalIgnoreCase)
            .Select(item =>
            {
                var suggestedPort = ResolveConfiguredBridgePort(item, occupiedPorts);
                occupiedPorts.Add(suggestedPort);
                return item with { SuggestedPort = suggestedPort };
            })
            .ToList();

        var selected = preferredPortKey ?? (BridgeSessionComboBox.SelectedItem as BridgeTargetItem)?.PortKey;
        BridgeSessionComboBox.ItemsSource = items;
        if (!string.IsNullOrWhiteSpace(selected))
        {
            BridgeSessionComboBox.SelectedItem = items.FirstOrDefault(item => string.Equals(item.PortKey, selected, StringComparison.OrdinalIgnoreCase));
        }
        else if (items.Count > 0)
        {
            BridgeSessionComboBox.SelectedIndex = 0;
        }
    }

    private void RefreshModelChoices()
    {
        var selectedKey = GetSelectedModelKey();
        _agentModelOptions.Clear();
        _agentModelOptions.Add(new AgentModelOption("auto", "auto | 使用终端默认模型等级"));
        foreach (var entry in _runtime.Config.Ai
                     .Where(item => item.Key.StartsWith("level", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(item => ParseLevel(item.Key)))
        {
            _agentModelOptions.Add(new AgentModelOption(entry.Key, $"{entry.Key} | {entry.Value.Name}"));
        }

        AgentModelComboBox.ItemsSource = _agentModelOptions;
        AgentModelComboBox.DisplayMemberPath = nameof(AgentModelOption.Label);
        AgentModelComboBox.SelectedValuePath = nameof(AgentModelOption.Key);
        AgentModelComboBox.SelectedValue = _runtime.Config.Terminal.Agent.SelectedModel;
        if (AgentModelComboBox.SelectedValue is null)
        {
            AgentModelComboBox.SelectedValue = string.IsNullOrWhiteSpace(selectedKey) ? "auto" : selectedKey;
        }
        if (AgentModelComboBox.SelectedValue is null)
        {
            AgentModelComboBox.SelectedIndex = 0;
        }

        PromptModelComboBox.ItemsSource = _agentModelOptions;
        PromptModelComboBox.DisplayMemberPath = nameof(AgentModelOption.Label);
        PromptModelComboBox.SelectedValuePath = nameof(AgentModelOption.Key);
        PromptModelComboBox.SelectedValue = PromptModelComboBox.SelectedValue as string ?? _runtime.Config.Terminal.Agent.SelectedModel;
        if (PromptModelComboBox.SelectedValue is null)
        {
            PromptModelComboBox.SelectedIndex = 0;
        }
    }

    private void RefreshSessionSummary()
    {
        var selected = OpenSessionsTabControl.SelectedItem as OpenTerminalSessionViewModel;
        OpenSessionSummaryTextBlock.Text = $"打开中的会话: {_openSessions.Count} 个，API 共享 {_openSessions.Count(item => item.IsApiShared)} 个。";
        SessionDeckMetaTextBlock.Text = selected is null
            ? "选择一个已打开标签；输入焦点在终端正文时可直接输入。"
            : $"{selected.Title} · 直接在终端正文输入；Ctrl+V 粘贴，Ctrl+C 无选区时发送中断。";
        SelectedSessionStatusTextBlock.Text = selected is null
            ? "未选中终端会话。"
            : $"当前会话: {selected.Title} | {selected.Descriptor} | {selected.ApiShareLabel} | {selected.SshShareLabel}";
        CurrentCommandStatusTextBlock.Text = selected is null ? "当前没有正在执行的命令。" : TrimForSingleLine(selected.CurrentCommandText);
    }

    private void PersistAgentPreferences()
    {
        var config = _runtime.ConfigService.LoadOrCreate();
        config.Terminal.Agent.AutoExecute = true;
        config.Terminal.Agent.SelectedModel = GetSelectedModelKey();
        _runtime.SaveConfig(config);
    }

    private string GetSelectedModelKey()
    {
        return AgentModelComboBox.SelectedValue as string ?? _runtime.Config.Terminal.Agent.SelectedModel ?? "auto";
    }

    private string? GetAgentTargetSessionId()
    {
        return AgentTargetSessionComboBox.SelectedValue as string ?? SelectedOpenSessionId;
    }

    private string GetPromptSelectedModelKey()
    {
        return PromptModelComboBox.SelectedValue as string ?? GetSelectedModelKey();
    }

    private string? GetPromptTargetSessionId()
    {
        return PromptTargetSessionComboBox.SelectedValue as string ?? SelectedOpenSessionId;
    }

    private void AddAgentTimeline(string role, string content)
    {
        AddAgentTimeline(role, content, appendToPrevious: false);
    }

    private void AddAgentTimeline(string role, string content, bool appendToPrevious)
    {
        // Streamed thinking/content should grow in place instead of creating a new timeline row for every token.
        if (appendToPrevious && _agentTimeline.Count > 0)
        {
            var last = _agentTimeline[^1];
            if (string.Equals(last.Role, role, StringComparison.Ordinal))
            {
                _agentTimeline[^1] = new AgentTimelineItemViewModel(role, last.Content + content, last.TimestampLabel);
                AgentTimelineListBox.ScrollIntoView(_agentTimeline.LastOrDefault());
                AgentSummaryTextBlock.Text = $"最近事件: {TrimForSingleLine(_agentTimeline[^1].Content)}";
                return;
            }
        }

        _agentTimeline.Add(new AgentTimelineItemViewModel(role, content));
        if (_agentTimeline.Count > 200)
        {
            _agentTimeline.RemoveAt(0);
        }
        AgentTimelineListBox.ScrollIntoView(_agentTimeline.LastOrDefault());
        AgentSummaryTextBlock.Text = $"最近事件: {content}";
    }

    private Progress<AgentEvent> CreateAgentProgress()
    {
        return new Progress<AgentEvent>(evt =>
        {
            if (string.IsNullOrWhiteSpace(evt.Message) && evt.ToolResult is null)
            {
                return;
            }

            var content = evt.Type switch
            {
                AgentEventType.ToolCall => $"工具调用 {evt.ToolName}: {evt.Message}",
                AgentEventType.ToolResult => $"工具结果 {evt.ToolName}: {evt.Message}",
                AgentEventType.Reasoning => evt.Message,
                AgentEventType.Content => evt.Message,
                _ => evt.Message,
            };

            AddAgentTimeline(evt.Type is AgentEventType.Reasoning or AgentEventType.Content ? "agent" : "system", content, evt.AppendToPrevious);
        });
    }

    private void ApplyPromptPlan(TerminalAgentPlan plan)
    {
        if (!plan.Success)
        {
            ResetRequestStatus(PromptStatusTextBlock);
            PromptSummaryTextBlock.Text = plan.Error;
            AddAgentTimeline("agent", $"[提示模式] 规划失败: {plan.Error}");
            return;
        }

        if (!string.IsNullOrWhiteSpace(plan.Analysis))
        {
            _promptDialogue.Add(new TerminalAgentDialogueItem { Role = "assistant", Content = plan.Analysis });
            AddAgentTimeline("agent", $"[提示模式] {plan.Analysis}");
        }

        if (!string.IsNullOrWhiteSpace(plan.FinalMessage))
        {
            AddAgentTimeline("agent", $"[提示模式] {plan.FinalMessage}");
        }

        if (plan.Completed)
        {
            PromptSuggestedCommandTextBox.Clear();
            ResetRequestStatus(PromptStatusTextBlock);
            PromptSummaryTextBlock.Text = string.IsNullOrWhiteSpace(plan.FinalMessage) ? "Agent 判断任务已完成。" : plan.FinalMessage;
            return;
        }

        if (plan.NeedInput)
        {
            PromptSuggestedCommandTextBox.Clear();
            ResetRequestStatus(PromptStatusTextBlock);
            PromptSummaryTextBlock.Text = string.IsNullOrWhiteSpace(plan.FinalMessage) ? "Agent 需要更多上下文。" : plan.FinalMessage;
            return;
        }

        PromptSuggestedCommandTextBox.Text = plan.SuggestedCommand;
        ResetRequestStatus(PromptStatusTextBlock);
        PromptSummaryTextBlock.Text = string.IsNullOrWhiteSpace(plan.SuggestedCommand) ? "本轮未生成命令。" : $"待手动执行：{plan.SuggestedCommand}";
    }

    private static void SetRequestStatus(TextBlock target, string request)
    {
        target.Text = string.IsNullOrWhiteSpace(request) ? "Ready" : request.Trim();
    }

    private static void ResetRequestStatus(TextBlock target)
    {
        target.Text = "Ready";
    }

    private static string BuildExecutionNote(TerminalCommandResult result)
    {
        var output = string.IsNullOrWhiteSpace(result.Output) ? "<empty>" : result.Output;
        return $"执行命令: {result.Command}\n成功: {result.Success}\n超时: {result.TimedOut}\n输出:\n{output}";
    }

    private void LoadProfileIntoForms(TerminalSessionProfile profile)
    {
        _loadedProfileId = profile.ProfileId;
        _loadedProfileKind = profile.Kind;
        _loadedProfileDescriptor = profile.Descriptor;
        NavigationTabControl.SelectedIndex = 1;

        switch (profile.Kind)
        {
            case TerminalSessionKind.PowerShell:
                PowerShellTitleTextBox.Text = profile.Title;
                PowerShellProgramTextBox.Text = profile.Program;
                PowerShellArgsTextBox.Text = profile.Arguments;
                PowerShellWorkdirTextBox.Text = profile.WorkingDirectory;
                PowerShellSshSharedCheckBox.IsChecked = profile.SshShared;
                PowerShellApiSharedCheckBox.IsChecked = profile.ApiShared;
                break;
            case TerminalSessionKind.Ssh:
                SshTitleTextBox.Text = profile.Title;
                SshHostTextBox.Text = profile.Host;
                SshPortTextBox.Text = profile.Port.ToString();
                SshUsernameTextBox.Text = profile.Username;
                SshPasswordBox.Password = profile.Password;
                SshSshSharedCheckBox.IsChecked = profile.SshShared;
                SshApiSharedCheckBox.IsChecked = profile.ApiShared;
                break;
            case TerminalSessionKind.Telnet:
                TelnetTitleTextBox.Text = profile.Title;
                TelnetHostTextBox.Text = profile.Host;
                TelnetPortTextBox.Text = profile.Port.ToString();
                TelnetSshSharedCheckBox.IsChecked = profile.SshShared;
                TelnetApiSharedCheckBox.IsChecked = profile.ApiShared;
                break;
            case TerminalSessionKind.Serial:
                SerialTitleTextBox.Text = profile.Title;
                SerialPortComboBox.Text = profile.PortName;
                SerialBaudRateTextBox.Text = profile.BaudRate.ToString();
                SerialParityTextBox.Text = profile.Parity;
                SerialDataBitsTextBox.Text = profile.DataBits.ToString();
                SerialStopBitsTextBox.Text = profile.StopBits;
                SerialSshSharedCheckBox.IsChecked = profile.SshShared;
                SerialApiSharedCheckBox.IsChecked = profile.ApiShared;
                break;
        }

        ProfileHintTextBlock.Text = $"已将“{profile.Title}”加载到连接表单；再次保存会覆盖原配置。";
        SetWindowStatus($"已加载配置会话到连接表单：{profile.Title}", isMuted: true);
    }

    private TerminalSessionProfile BuildPowerShellProfile()
    {
        var profile = new TerminalSessionProfile
        {
            Title = PowerShellTitleTextBox.Text.Trim(),
            Kind = TerminalSessionKind.PowerShell,
            Program = PowerShellProgramTextBox.Text.Trim(),
            Arguments = PowerShellArgsTextBox.Text.Trim(),
            WorkingDirectory = PowerShellWorkdirTextBox.Text.Trim(),
            ApiShared = PowerShellApiSharedCheckBox.IsChecked == true,
            SshShared = PowerShellSshSharedCheckBox.IsChecked == true,
        };

        profile.ProfileId = ResolveLoadedProfileId(profile.Kind, profile.Descriptor);
        return profile;
    }

    private TerminalSessionProfile BuildSshProfile()
    {
        var profile = new TerminalSessionProfile
        {
            Title = SshTitleTextBox.Text.Trim(),
            Kind = TerminalSessionKind.Ssh,
            Host = SshHostTextBox.Text.Trim(),
            Port = ParseInt(SshPortTextBox.Text, 22),
            Username = SshUsernameTextBox.Text.Trim(),
            Password = SshPasswordBox.Password,
            ApiShared = SshApiSharedCheckBox.IsChecked == true,
            SshShared = SshSshSharedCheckBox.IsChecked == true,
        };

        profile.ProfileId = ResolveLoadedProfileId(profile.Kind, profile.Descriptor);
        return profile;
    }

    private TerminalSessionProfile BuildTelnetProfile()
    {
        var profile = new TerminalSessionProfile
        {
            Title = TelnetTitleTextBox.Text.Trim(),
            Kind = TerminalSessionKind.Telnet,
            Host = TelnetHostTextBox.Text.Trim(),
            Port = ParseInt(TelnetPortTextBox.Text, 23),
            ApiShared = TelnetApiSharedCheckBox.IsChecked == true,
            SshShared = TelnetSshSharedCheckBox.IsChecked == true,
        };

        profile.ProfileId = ResolveLoadedProfileId(profile.Kind, profile.Descriptor);
        return profile;
    }

    private TerminalSessionProfile BuildSerialProfile()
    {
        var profile = new TerminalSessionProfile
        {
            Title = SerialTitleTextBox.Text.Trim(),
            Kind = TerminalSessionKind.Serial,
            PortName = SerialPortComboBox.Text.Trim(),
            BaudRate = ParseInt(SerialBaudRateTextBox.Text, 115200),
            Parity = SerialParityTextBox.Text.Trim(),
            DataBits = ParseInt(SerialDataBitsTextBox.Text, 8),
            StopBits = SerialStopBitsTextBox.Text.Trim(),
            ApiShared = SerialApiSharedCheckBox.IsChecked == true,
            SshShared = SerialSshSharedCheckBox.IsChecked == true,
        };

        profile.ProfileId = ResolveLoadedProfileId(profile.Kind, profile.Descriptor);
        return profile;
    }

    private string ResolveLoadedProfileId(TerminalSessionKind kind, string descriptor)
    {
        return _loadedProfileId is not null &&
               _loadedProfileKind == kind &&
               string.Equals(_loadedProfileDescriptor, descriptor, StringComparison.OrdinalIgnoreCase)
            ? _loadedProfileId
            : string.Empty;
    }

    private bool TryGetProfileParameter(object sender, out TerminalProfileViewModel profile)
    {
        profile = (sender as MenuItem)?.CommandParameter as TerminalProfileViewModel
            ?? ProfilesListBox.SelectedItem as TerminalProfileViewModel
            ?? new TerminalProfileViewModel(new TerminalSessionProfile());
        return !string.IsNullOrWhiteSpace(profile.ProfileId);
    }

    private static bool TryGetOpenSessionParameter(object sender, out OpenTerminalSessionViewModel session)
    {
        session = (sender as FrameworkElement)?.DataContext as OpenTerminalSessionViewModel
            ?? new OpenTerminalSessionViewModel(new TerminalSessionInfo());
        return !string.IsNullOrWhiteSpace(session.SessionId);
    }

    private string BuildCurrentCommandText(string sessionId)
    {
        var current = _runtime.TerminalSessions.GetCurrentCommandOutput(sessionId);
        return current.IsRunning
            ? $"当前执行: {current.Command} | 输出片段: {TrimForSingleLine(current.Output)}"
            : "当前没有正在执行的命令。";
    }

    private static string BuildMetaLine(TerminalSessionInfo? session)
    {
        if (session is null)
        {
            return string.Empty;
        }

        return $"{session.Kind} · 创建于 {session.CreatedAt} · 最近活动 {session.LastActivityAt}";
    }

    private static int ParseInt(string text, int fallback)
    {
        return int.TryParse(text, out var value) ? value : fallback;
    }

    private static double ParseDouble(string text, double fallback)
    {
        return double.TryParse(text, out var value) ? value : fallback;
    }

    private static int ParseLevel(string key)
    {
        return key.StartsWith("level", StringComparison.OrdinalIgnoreCase) && int.TryParse(key[5..], out var level) ? level : int.MaxValue;
    }

    private static string TrimForSingleLine(string text)
    {
        var compact = string.Join(" ", text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
        return compact.Length <= 120 ? compact : compact[..120] + "...";
    }

    private static string DescribeInput(string text)
    {
        return text switch
        {
            "\n" => "Enter",
            " " => "Space",
            "\t" => "Tab",
            "\x7f" => "Backspace",
            "\u0003" => "Ctrl+C",
            _ => TrimForSingleLine(text),
        };
    }

    private string? TryMapTerminalKey(Key key, ModifierKeys modifiers)
    {
        if (modifiers != ModifierKeys.None)
        {
            return null;
        }

        return key switch
        {
            Key.Space => " ",
            Key.Enter => "\n",
            Key.Back => "\x7f",
            Key.Tab => "\t",
            Key.Up => "\x1b[A",
            Key.Down => "\x1b[B",
            Key.Right => "\x1b[C",
            Key.Left => "\x1b[D",
            Key.Home => "\x1b[H",
            Key.End => "\x1b[F",
            Key.Insert => "\x1b[2~",
            Key.Delete => "\x1b[3~",
            Key.PageUp => "\x1b[5~",
            Key.PageDown => "\x1b[6~",
            _ => null,
        };
    }

    private void ToggleNavigationPane()
    {
        if (NavigationPaneColumn.Width.Value <= CollapsedNavigationPaneWidth + 0.5d)
        {
            NavigationPaneColumn.Width = new GridLength(Math.Max(ExpandedNavigationPaneWidth, _lastExpandedNavigationPaneWidth));
            NavigationSplitter.Width = 12;
            NavigationSplitter.Visibility = Visibility.Visible;
            return;
        }

        _lastExpandedNavigationPaneWidth = Math.Max(ExpandedNavigationPaneWidth, NavigationPaneBorder.ActualWidth);
        NavigationPaneColumn.Width = new GridLength(CollapsedNavigationPaneWidth);
        NavigationSplitter.Width = 0;
        NavigationSplitter.Visibility = Visibility.Collapsed;
    }

    private void FocusSelectedTerminalViewport()
    {
        Dispatcher.BeginInvoke(() =>
        {
            OpenSessionsTabControl.UpdateLayout();
            if (OpenSessionsTabControl.SelectedItem is null)
            {
                return;
            }

            var container = OpenSessionsTabControl.ItemContainerGenerator.ContainerFromItem(OpenSessionsTabControl.SelectedItem) as TabItem;
            var terminalBox = FindDescendant<AnsiTerminalBox>(container);
            terminalBox?.FocusTerminalInput();
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T match)
            {
                return match;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return null;
    }

    private static T? FindDescendant<T>(DependencyObject? source) where T : DependencyObject
    {
        if (source is null)
        {
            return null;
        }

        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(source); index++)
        {
            var child = VisualTreeHelper.GetChild(source, index);
            if (child is T match)
            {
                return match;
            }

            var nested = FindDescendant<T>(child);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private static bool SupportsBridge(TerminalSessionKind kind)
    {
        return kind is TerminalSessionKind.PowerShell or TerminalSessionKind.Ssh or TerminalSessionKind.Telnet or TerminalSessionKind.Serial;
    }

    private void EnsureBridgePortAssignment(TerminalSessionProfile profile)
    {
        if (!profile.SshShared || !SupportsBridge(profile.Kind))
        {
            return;
        }

        var portKey = ResolveBridgePortKey(profile.Kind, profile.Descriptor);
        if (string.IsNullOrWhiteSpace(portKey))
        {
            return;
        }

        var config = _runtime.ConfigService.LoadOrCreate();
        if (config.Terminal.SerialSshBridge.PortOverrides.ContainsKey(portKey))
        {
            return;
        }

        var legacyPortKey = ResolveLegacyBridgePortKey(profile.Kind, profile.Descriptor);
        if (!string.IsNullOrWhiteSpace(legacyPortKey) && config.Terminal.SerialSshBridge.PortOverrides.ContainsKey(legacyPortKey))
        {
            var existingPort = config.Terminal.SerialSshBridge.PortOverrides[legacyPortKey];
            config.Terminal.SerialSshBridge.PortOverrides[portKey] = existingPort;
            config.Terminal.SerialSshBridge.PortOverrides.Remove(legacyPortKey);
            _runtime.SaveConfig(config);
            return;
        }

        var port = BuildDefaultBridgePort(profile.Kind, config.Terminal.SerialSshBridge.PortPrefix, config.Terminal.SerialSshBridge.PortOverrides.Values);
        config.Terminal.SerialSshBridge.PortOverrides[portKey] = port;
        _runtime.SaveConfig(config);
    }

    private int ResolveConfiguredBridgePort(BridgeTargetItem target)
    {
        return ResolveConfiguredBridgePort(target, null);
    }

    private int ResolveConfiguredBridgePort(BridgeTargetItem target, ISet<int>? occupiedPorts)
    {
        var config = _runtime.Config.Terminal.SerialSshBridge;
        if (config.PortOverrides.TryGetValue(target.PortKey, out var savedPort))
        {
            return savedPort;
        }

        if (!string.IsNullOrWhiteSpace(target.LegacyPortKey) && config.PortOverrides.TryGetValue(target.LegacyPortKey, out savedPort))
        {
            return savedPort;
        }

        IEnumerable<int> reservedPorts = occupiedPorts is null ? config.PortOverrides.Values : occupiedPorts;
        return BuildDefaultBridgePort(target.Kind, config.PortPrefix, reservedPorts);
    }

    private static string ResolveBridgePortKey(TerminalSessionKind kind, string descriptor)
    {
        if (string.IsNullOrWhiteSpace(descriptor))
        {
            return string.Empty;
        }

        return $"{kind}:{descriptor.Trim()}";
    }

    private static string? ResolveLegacyBridgePortKey(TerminalSessionKind kind, string descriptor)
    {
        if (kind != TerminalSessionKind.Serial || string.IsNullOrWhiteSpace(descriptor))
        {
            return null;
        }

        return descriptor.Split('@', 2, StringSplitOptions.TrimEntries)[0].Trim();
    }

    private static int BuildDefaultBridgePort(TerminalSessionKind kind, string serialPortPrefix, IEnumerable<int> occupiedPorts)
    {
        var prefix = kind switch
        {
            TerminalSessionKind.Telnet => 23,
            TerminalSessionKind.Ssh => 24,
            TerminalSessionKind.PowerShell => 25,
            TerminalSessionKind.Serial when int.TryParse(serialPortPrefix, out var parsedPrefix) => parsedPrefix,
            TerminalSessionKind.Serial => 22,
            _ => 22,
        };

        var occupied = new HashSet<int>(occupiedPorts.Where(port => port / 100 == prefix));
        for (var suffix = 1; suffix <= 99; suffix++)
        {
            var candidate = prefix * 100 + suffix;
            if (!occupied.Contains(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException($"No free SSH bridge port left in range {prefix}01-{prefix}99.");
    }

    private void ClampWindowToDesktop()
    {
        var workArea = SystemParameters.WorkArea;
        MaxWidth = workArea.Width;
        MaxHeight = workArea.Height;
        Width = Math.Min(Width, workArea.Width);
        Height = Math.Min(Height, workArea.Height);
        Left = Math.Max(workArea.Left, workArea.Left + (workArea.Width - Width) / 2d);
        Top = Math.Max(workArea.Top, workArea.Top + (workArea.Height - Height) / 2d);
    }

    private void SetWindowStatus(string message, bool isMuted)
    {
        WindowStatusTextBlock.Text = message;
        WindowStatusTextBlock.Foreground = isMuted
            ? (Brush)FindResource("MutedBrush")
            : (Brush)FindResource("AccentBrush");
    }

    private void ShowError(Exception ex)
    {
        SetWindowStatus(ex.Message, isMuted: false);
        MessageBox.Show(this, ex.Message, "终端控制", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private sealed class TerminalProfileViewModel
    {
        public TerminalProfileViewModel(TerminalSessionProfile profile)
        {
            Profile = profile;
        }

        public TerminalSessionProfile Profile { get; }
        public string ProfileId => Profile.ProfileId;
        public string Title => Profile.Title;
        public string KindLabel => Profile.KindLabel;
        public string Descriptor => Profile.Descriptor;
        public string ApiShareLabel => Profile.ApiShared ? "API 共享: 开" : "API 共享: 关";
        public string SshShareLabel => Profile.SshShared ? "SSH 共享: 开" : "SSH 共享: 关";
        public string LastUsedLabel => string.IsNullOrWhiteSpace(Profile.LastUsedAt) ? "尚未打开" : $"最近打开: {Profile.LastUsedAt}";
    }

    private sealed class OpenTerminalSessionViewModel : INotifyPropertyChanged
    {
        private string _rawOutputText = string.Empty;
        private string _outputText = string.Empty;
        private string _currentCommandText = string.Empty;
        private string _metaLine = string.Empty;

        public OpenTerminalSessionViewModel(TerminalSessionInfo session)
        {
            Session = session;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public TerminalSessionInfo Session { get; }
        public string SessionId => Session.SessionId;
        public string Title => Session.Title;
        public string KindLabel => Session.Kind.ToString();
        public string Descriptor => Session.Descriptor;
        public bool IsApiShared => Session.IsApiShared;
        public string ApiShareLabel => Session.IsApiShared ? "API 共享中" : "未共享 API";
        public string SshShareLabel => Session.IsSshShared ? "SSH 共享开" : "SSH 共享关";

        public string OutputText
        {
            get => _outputText;
            set => SetField(ref _outputText, value);
        }

        public string RawOutputText
        {
            get => _rawOutputText;
            set => SetField(ref _rawOutputText, value);
        }

        public string CurrentCommandText
        {
            get => _currentCommandText;
            set => SetField(ref _currentCommandText, value);
        }

        public string MetaLine
        {
            get => _metaLine;
            set => SetField(ref _metaLine, value);
        }

        private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return;
            }

            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    private sealed class AgentTimelineItemViewModel
    {
        public AgentTimelineItemViewModel(string role, string content)
            : this(role, content, DateTime.Now.ToString("HH:mm:ss"))
        {
        }

        public AgentTimelineItemViewModel(string role, string content, string timestampLabel)
        {
            Role = role;
            Content = content;
            TimestampLabel = timestampLabel;
        }

        public string Role { get; }
        public string Content { get; }
        public string TimestampLabel { get; }
        public string RoleLabel => Role switch
        {
            "user" => "用户",
            "agent" => "Agent",
            "system" => "系统",
            _ => Role,
        };
    }

    private sealed record AgentModelOption(string Key, string Label);
    private sealed record AgentTargetSessionItem(string SessionId, string Label);
    private sealed record BridgeTargetItem(TerminalSessionKind Kind, string PortKey, string Label, string? SessionId, int SuggestedPort, string? LegacyPortKey);
}