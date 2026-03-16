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

namespace LuminChatWin.App;

public partial class TerminalControlWindow : Window
{
    private readonly AppRuntime _runtime;
    private readonly ObservableCollection<TerminalProfileViewModel> _profiles = [];
    private readonly ObservableCollection<OpenTerminalSessionViewModel> _openSessions = [];
    private readonly ObservableCollection<AgentTimelineItemViewModel> _agentTimeline = [];
    private readonly List<TerminalAgentDialogueItem> _agentDialogue = [];
    private readonly List<TerminalAgentDialogueItem> _promptDialogue = [];
    private readonly List<AgentModelOption> _agentModelOptions = [];
    private string? _loadedProfileId;
    private TerminalSessionKind? _loadedProfileKind;
    private string? _promptObjective;
    private bool _agentBusy;

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

        RefreshSerialPorts();
        RefreshProfiles();
        RefreshOpenSessions();
        RefreshModelChoices();
        RefreshAgentTargets();
        RefreshApiSummary();
        RefreshBridgeTargets();
        ProfileHintTextBlock.Text = "串口会话开启 SSH 共享后会在打开时自动启动桥接，API 共享则决定是否暴露到本地 HTTP API。";
        AgentStatusTextBlock.Text = "自动模式待命";
        AgentSummaryTextBlock.Text = "未开始执行。选择目标会话、模型和需求后，Agent 会持续执行直到完成。";
        PromptStatusTextBlock.Text = "提示模式待命";
        PromptSummaryTextBlock.Text = "未开始执行。提示模式只生成建议命令，手动执行后会继续规划。";
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

            viewModel.OutputText = _runtime.TerminalSessions.GetRecentOutput(e.SessionId);
            viewModel.RawOutput = _runtime.TerminalSessions.GetRecentRawOutput(e.SessionId);
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
        RefreshSerialPorts();
        RefreshApiSummary();
        RefreshBridgeTargets();
        SetWindowStatus("已刷新会话、串口、Agent 目标与共享状态。", isMuted: true);
    }

    private void RefreshOpenSessions_Click(object sender, RoutedEventArgs e)
    {
        RefreshOpenSessions();
    }

    private void RefreshAgentTargets_Click(object sender, RoutedEventArgs e)
    {
        RefreshAgentTargets();
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
            _runtime.TerminalProfiles.SetSshShared(profile.ProfileId, !profile.Profile.SshShared);
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
            AgentStatusTextBlock.Text = "自动模式执行中...";
            AgentSummaryTextBlock.Text = $"目标会话：{targetSessionId} | 模型：{GetSelectedModelKey()}";
            SetWindowStatus("Agent 正在自动执行终端任务。", isMuted: false);

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
                AgentStatusTextBlock.Text = "规划失败";
                AgentSummaryTextBlock.Text = plan.Error;
                SetWindowStatus("Agent 规划失败。", isMuted: false);
                return;
            }

            if (!string.IsNullOrWhiteSpace(plan.Analysis))
            {
                AddAgentTimeline("agent", plan.Analysis);
            }

            if (plan.Completed)
            {
                AgentStatusTextBlock.Text = "任务完成";
                AgentSummaryTextBlock.Text = string.IsNullOrWhiteSpace(plan.FinalMessage) ? "Agent 判断任务已完成。" : plan.FinalMessage;
                SetWindowStatus("Agent 已完成当前任务。", isMuted: false);
            }
            else if (plan.NeedInput)
            {
                AgentStatusTextBlock.Text = "等待人工输入";
                AgentSummaryTextBlock.Text = string.IsNullOrWhiteSpace(plan.FinalMessage) ? "Agent 需要更多信息。" : plan.FinalMessage;
                SetWindowStatus("Agent 需要人工补充信息。", isMuted: false);
            }
            else if (plan.Executed && plan.ExecutionResult is not null)
            {
                AgentStatusTextBlock.Text = "自动流程已暂停";
                AgentSummaryTextBlock.Text = $"最后执行：{plan.ExecutionResult.Command}";
                SetWindowStatus("Agent 自动流程已暂停。", isMuted: false);
                RefreshOpenSessions(targetSessionId);
            }
            else
            {
                AgentStatusTextBlock.Text = "自动流程结束";
                AgentSummaryTextBlock.Text = string.IsNullOrWhiteSpace(plan.SuggestedCommand) ? "本轮未生成可执行命令。" : $"最后建议命令：{plan.SuggestedCommand}";
                SetWindowStatus("Agent 自动流程结束。", isMuted: true);
            }
        }
        catch (Exception ex)
        {
            AgentStatusTextBlock.Text = "Agent 执行失败";
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
            PersistAgentPreferences();
            _promptObjective = request;
            _promptDialogue.Clear();
            _promptDialogue.Add(new TerminalAgentDialogueItem { Role = "user", Content = request });
            AddAgentTimeline("user", $"[提示模式] {request}");
            PromptRequestTextBox.Clear();
            PromptStatusTextBlock.Text = "提示模式规划中...";
            PromptSummaryTextBlock.Text = $"目标会话：{targetSessionId} | 模型：{GetPromptModelKey()}";
            SetWindowStatus("提示模式正在生成建议命令。", isMuted: false);

            var plan = await _runtime.TerminalAgent.RunLoopAsync(
                targetSessionId,
                request,
                _promptDialogue,
                GetPromptModelKey(),
                TerminalAgentMode.Prompt,
                CreateAgentProgress());

            ApplyPromptPlan(plan, targetSessionId);
        }
        catch (Exception ex)
        {
            PromptStatusTextBlock.Text = "提示模式失败";
            PromptSummaryTextBlock.Text = ex.Message;
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
        if (string.IsNullOrWhiteSpace(targetSessionId) || string.IsNullOrWhiteSpace(PromptSuggestedCommandTextBox.Text) || string.IsNullOrWhiteSpace(_promptObjective))
        {
            return;
        }

        try
        {
            _agentBusy = true;
            var result = await _runtime.TerminalSessions.ExecuteCommandAsync(
                targetSessionId,
                PromptSuggestedCommandTextBox.Text,
                TimeSpan.FromSeconds(Math.Max(3, _runtime.Config.Terminal.ExecApi.DefaultTimeoutSeconds)));
            AddAgentTimeline("system", $"[提示模式] 手动执行: {result.Command}");
            _promptDialogue.Add(new TerminalAgentDialogueItem { Role = "system", Content = BuildExecutionNote(result) });
            PromptStatusTextBlock.Text = "已执行，继续规划中...";
            PromptSummaryTextBlock.Text = $"已手动执行：{result.Command}";
            SetWindowStatus("提示模式已执行建议命令，正在继续规划。", isMuted: false);
            RefreshOpenSessions(targetSessionId);

            var plan = await _runtime.TerminalAgent.RunLoopAsync(
                targetSessionId,
                _promptObjective,
                _promptDialogue,
                GetPromptModelKey(),
                TerminalAgentMode.Prompt,
                CreateAgentProgress());

            ApplyPromptPlan(plan, targetSessionId);
        }
        catch (Exception ex)
        {
            PromptStatusTextBlock.Text = "提示模式失败";
            PromptSummaryTextBlock.Text = ex.Message;
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
        if (BridgeSessionComboBox.SelectedItem is not SerialBridgeTargetItem target)
        {
            MessageBox.Show(this, "请先选择一个串口。", "串口 SSH Bridge", MessageBoxButton.OK, MessageBoxImage.Information);
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
            _runtime.SaveConfig(config);
            BridgeStatusTextBlock.Text = $"已保存 {target.Label} 的共享端口: {config.Terminal.SerialSshBridge.BindHost}:{port}";
            SetWindowStatus($"已保存串口共享端口：{target.Label} -> {port}", isMuted: true);
            RefreshBridgeTargets(target.PortKey);
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private async void OpenBridge_Click(object sender, RoutedEventArgs e)
    {
        if (BridgeSessionComboBox.SelectedItem is not SerialBridgeTargetItem target)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(target.SessionId))
        {
            MessageBox.Show(this, "这个串口当前没有打开会话。请先打开对应串口会话，再手工打开共享端口。", "串口 SSH Bridge", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            var bridge = await _runtime.TerminalSessions.StartSerialBridgeAsync(
                target.SessionId,
                string.IsNullOrWhiteSpace(BridgePortTextBox.Text) ? null : ParseInt(BridgePortTextBox.Text, 22000));
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
        if (BridgeSessionComboBox.SelectedItem is not SerialBridgeTargetItem target)
        {
            return;
        }

        if (_runtime.Config.Terminal.SerialSshBridge.PortOverrides.TryGetValue(target.PortKey, out var savedPort))
        {
            BridgePortTextBox.Text = savedPort.ToString();
            return;
        }

        BridgePortTextBox.Text = string.Empty;
    }

    private void OpenSessionsTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        RefreshSessionSummary();
        RefreshAgentTargets();
    }

    private void TerminalViewport_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is AnsiTerminalBox terminalBox)
        {
            terminalBox.CaretPosition = terminalBox.Document.ContentEnd;
            terminalBox.ScrollToEnd();
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
        if (!TryGetOpenSessionParameter(sender, out var session) || sender is not AnsiTerminalBox terminalBox)
        {
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.C)
        {
            if (!string.IsNullOrEmpty(terminalBox.Selection.Text))
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

        if (Keyboard.Modifiers != ModifierKeys.None)
        {
            return;
        }

        var payload = e.Key switch
        {
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

        if (payload is null)
        {
            return;
        }

        e.Handled = true;
        await SendTerminalInputAsync(session.SessionId, payload);
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
            RefreshProfiles(saved.ProfileId);
            _loadedProfileId = saved.ProfileId;
            _loadedProfileKind = saved.Kind;
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
                BridgeStatusTextBlock.Text = $"自动桥接已开启: {bridge.Protocol}://{bridge.Host}:{bridge.Port}";
            }

            RefreshProfiles(profile.ProfileId);
            RefreshOpenSessions(session.SessionId);
            RefreshAgentTargets(session.SessionId);
            RefreshApiSummary();
            RefreshBridgeTargets();
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
                OutputText = _runtime.TerminalSessions.GetRecentOutput(session.SessionId),
                RawOutput = _runtime.TerminalSessions.GetRecentRawOutput(session.SessionId),
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
    }

    private void RefreshAgentTargets(string? preferredSessionId = null)
    {
        var items = _openSessions
            .Select(item => new AgentTargetSessionItem(item.SessionId, $"{item.KindLabel} | {item.Title}"))
            .ToList();
        var selected = preferredSessionId ?? GetAgentTargetSessionId() ?? GetPromptTargetSessionId() ?? SelectedOpenSessionId;
        AgentTargetSessionComboBox.ItemsSource = items;
        PromptTargetSessionComboBox.ItemsSource = items;
        if (!string.IsNullOrWhiteSpace(selected))
        {
            AgentTargetSessionComboBox.SelectedValue = selected;
            PromptTargetSessionComboBox.SelectedValue = selected;
        }
        else if (items.Count > 0)
        {
            AgentTargetSessionComboBox.SelectedIndex = 0;
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
        var openSerials = _openSessions
            .Where(item => item.Session.Kind == TerminalSessionKind.Serial)
            .Select(item => new SerialBridgeTargetItem(
                ResolveBridgePortKey(item.Descriptor),
                $"{item.Title} | {item.Descriptor}",
                item.SessionId))
            .Where(item => !string.IsNullOrWhiteSpace(item.PortKey))
            .ToList();

        var configuredSerials = _runtime.TerminalProfiles.List()
            .Where(item => item.Kind == TerminalSessionKind.Serial)
            .Select(item =>
            {
                var portKey = item.PortName.Trim();
                var openMatch = openSerials.FirstOrDefault(open => string.Equals(open.PortKey, portKey, StringComparison.OrdinalIgnoreCase));
                return new SerialBridgeTargetItem(portKey, $"{item.Title} | {portKey}", openMatch?.SessionId);
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.PortKey))
            .ToList();

        var items = openSerials
            .Concat(configuredSerials)
            .GroupBy(item => item.PortKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(item => item.PortKey, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var selected = preferredPortKey ?? (BridgeSessionComboBox.SelectedItem as SerialBridgeTargetItem)?.PortKey;
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
        PromptModelComboBox.ItemsSource = _agentModelOptions;
        PromptModelComboBox.DisplayMemberPath = nameof(AgentModelOption.Label);
        PromptModelComboBox.SelectedValuePath = nameof(AgentModelOption.Key);
        AgentModelComboBox.SelectedValue = _runtime.Config.Terminal.Agent.SelectedModel;
        PromptModelComboBox.SelectedValue = _runtime.Config.Terminal.Agent.SelectedModel;
        if (AgentModelComboBox.SelectedValue is null)
        {
            AgentModelComboBox.SelectedValue = string.IsNullOrWhiteSpace(selectedKey) ? "auto" : selectedKey;
        }
        if (PromptModelComboBox.SelectedValue is null)
        {
            PromptModelComboBox.SelectedValue = AgentModelComboBox.SelectedValue;
        }
        if (AgentModelComboBox.SelectedValue is null)
        {
            AgentModelComboBox.SelectedIndex = 0;
        }
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

    private string GetPromptModelKey()
    {
        return PromptModelComboBox.SelectedValue as string ?? GetSelectedModelKey();
    }

    private string? GetAgentTargetSessionId()
    {
        return AgentTargetSessionComboBox.SelectedValue as string ?? SelectedOpenSessionId;
    }

    private string? GetPromptTargetSessionId()
    {
        return PromptTargetSessionComboBox.SelectedValue as string ?? SelectedOpenSessionId;
    }

    private void AddAgentTimeline(string role, string content)
    {
        _agentTimeline.Add(new AgentTimelineItemViewModel(role, content));
        if (_agentTimeline.Count > 200)
        {
            _agentTimeline.RemoveAt(0);
        }
        AgentTimelineListBox.ScrollIntoView(_agentTimeline.LastOrDefault());
        AgentSummaryTextBlock.Text = $"最近事件: {content}";
    }

    private void ApplyPromptPlan(TerminalAgentPlan plan, string sessionId)
    {
        if (!plan.Success)
        {
            AddAgentTimeline("agent", $"[提示模式] 规划失败: {plan.Error}");
            PromptStatusTextBlock.Text = "规划失败";
            PromptSummaryTextBlock.Text = plan.Error;
            SetWindowStatus("提示模式规划失败。", isMuted: false);
            return;
        }

        if (!string.IsNullOrWhiteSpace(plan.Analysis))
        {
            AddAgentTimeline("agent", $"[提示模式] {plan.Analysis}");
        }

        PromptSuggestedCommandTextBox.Text = plan.SuggestedCommand;
        if (plan.Completed)
        {
            PromptStatusTextBlock.Text = "任务完成";
            PromptSummaryTextBlock.Text = string.IsNullOrWhiteSpace(plan.FinalMessage) ? "提示模式判断任务已完成。" : plan.FinalMessage;
            SetWindowStatus("提示模式任务已完成。", isMuted: false);
            return;
        }

        if (plan.NeedInput)
        {
            PromptStatusTextBlock.Text = "等待人工输入";
            PromptSummaryTextBlock.Text = string.IsNullOrWhiteSpace(plan.FinalMessage) ? "提示模式需要更多信息。" : plan.FinalMessage;
            SetWindowStatus("提示模式需要人工补充信息。", isMuted: false);
            return;
        }

        PromptStatusTextBlock.Text = "等待手动执行";
        PromptSummaryTextBlock.Text = string.IsNullOrWhiteSpace(plan.SuggestedCommand)
            ? $"会话 {sessionId} 当前没有可执行命令。"
            : $"建议命令：{plan.SuggestedCommand}";
        if (!string.IsNullOrWhiteSpace(plan.SuggestedCommand))
        {
            AddAgentTimeline("system", $"[提示模式] 建议命令: {plan.SuggestedCommand}");
        }
        SetWindowStatus("提示模式已生成建议命令。", isMuted: true);
    }

    private IProgress<AgentEvent> CreateAgentProgress()
    {
        return new Progress<AgentEvent>(agentEvent =>
        {
            if (agentEvent is null)
            {
                return;
            }

            var role = agentEvent.Type switch
            {
                AgentEventType.Reasoning or AgentEventType.Content => "agent",
                AgentEventType.ToolCall or AgentEventType.ToolResult or AgentEventType.Info => "system",
                AgentEventType.Warning or AgentEventType.Error => "system",
                _ => "system",
            };

            var content = agentEvent.Type switch
            {
                AgentEventType.ToolCall => $"工具调用 {agentEvent.ToolName}: {agentEvent.Message}",
                AgentEventType.ToolResult => $"工具结果 {agentEvent.ToolName}: {agentEvent.Message}",
                _ => agentEvent.Message,
            };

            AddAgentTimeline(role, content);
        });
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
        return new TerminalSessionProfile
        {
            ProfileId = ResolveLoadedProfileId(TerminalSessionKind.PowerShell),
            Title = PowerShellTitleTextBox.Text.Trim(),
            Kind = TerminalSessionKind.PowerShell,
            Program = PowerShellProgramTextBox.Text.Trim(),
            Arguments = PowerShellArgsTextBox.Text.Trim(),
            WorkingDirectory = PowerShellWorkdirTextBox.Text.Trim(),
            ApiShared = PowerShellApiSharedCheckBox.IsChecked == true,
            SshShared = PowerShellSshSharedCheckBox.IsChecked == true,
        };
    }

    private TerminalSessionProfile BuildSshProfile()
    {
        return new TerminalSessionProfile
        {
            ProfileId = ResolveLoadedProfileId(TerminalSessionKind.Ssh),
            Title = SshTitleTextBox.Text.Trim(),
            Kind = TerminalSessionKind.Ssh,
            Host = SshHostTextBox.Text.Trim(),
            Port = ParseInt(SshPortTextBox.Text, 22),
            Username = SshUsernameTextBox.Text.Trim(),
            Password = SshPasswordBox.Password,
            ApiShared = SshApiSharedCheckBox.IsChecked == true,
            SshShared = SshSshSharedCheckBox.IsChecked == true,
        };
    }

    private TerminalSessionProfile BuildTelnetProfile()
    {
        return new TerminalSessionProfile
        {
            ProfileId = ResolveLoadedProfileId(TerminalSessionKind.Telnet),
            Title = TelnetTitleTextBox.Text.Trim(),
            Kind = TerminalSessionKind.Telnet,
            Host = TelnetHostTextBox.Text.Trim(),
            Port = ParseInt(TelnetPortTextBox.Text, 23),
            ApiShared = TelnetApiSharedCheckBox.IsChecked == true,
            SshShared = TelnetSshSharedCheckBox.IsChecked == true,
        };
    }

    private TerminalSessionProfile BuildSerialProfile()
    {
        return new TerminalSessionProfile
        {
            ProfileId = ResolveLoadedProfileId(TerminalSessionKind.Serial),
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
    }

    private string ResolveLoadedProfileId(TerminalSessionKind kind)
    {
        return _loadedProfileId is not null && _loadedProfileKind == kind ? _loadedProfileId : string.Empty;
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
            "\t" => "Tab",
            "\x7f" => "Backspace",
            "\u0003" => "Ctrl+C",
            _ => TrimForSingleLine(text),
        };
    }

    private static string ResolveBridgePortKey(string descriptor)
    {
        if (string.IsNullOrWhiteSpace(descriptor))
        {
            return string.Empty;
        }

        return descriptor.Split('@', 2, StringSplitOptions.TrimEntries)[0].Trim();
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
        private string _outputText = string.Empty;
        private string _rawOutput = string.Empty;
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

        public string RawOutput
        {
            get => _rawOutput;
            set => SetField(ref _rawOutput, value);
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
        {
            Role = role;
            Content = content;
            TimestampLabel = DateTime.Now.ToString("HH:mm:ss");
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
    private sealed record SerialBridgeTargetItem(string PortKey, string Label, string? SessionId);
}