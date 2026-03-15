using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO.Ports;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LuminChatWin.Core.Models;

namespace LuminChatWin.App;

public partial class TerminalControlWindow : Window
{
    private readonly AppRuntime _runtime;
    private readonly ObservableCollection<TerminalProfileViewModel> _profiles = [];
    private readonly ObservableCollection<OpenTerminalSessionViewModel> _openSessions = [];
    private readonly ObservableCollection<AgentTimelineItemViewModel> _agentTimeline = [];
    private readonly List<TerminalAgentDialogueItem> _agentDialogue = [];
    private readonly List<AgentModelOption> _agentModelOptions = [];
    private string? _loadedProfileId;
    private TerminalSessionKind? _loadedProfileKind;
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
        AgentAutoExecuteCheckBox.IsChecked = runtime.Config.Terminal.Agent.AutoExecute;

        _runtime.TerminalSessions.OutputReceived += TerminalSessions_OutputReceived;
        _runtime.ConfigChanged += Runtime_ConfigChanged;
        Closed += TerminalControlWindow_Closed;

        RefreshSerialPorts();
        RefreshProfiles();
        RefreshOpenSessions();
        RefreshModelChoices();
        RefreshAgentTargets();
        RefreshApiSummary();
        ProfileHintTextBlock.Text = "串口会话开启 SSH 共享后会在打开时自动启动桥接，API 共享则决定是否暴露到本地 HTTP API。";
        AgentStatusTextBlock.Text = "Ready";
    }

    private string? SelectedOpenSessionId => (OpenSessionsTabControl.SelectedItem as OpenTerminalSessionViewModel)?.SessionId;

    private void Runtime_ConfigChanged(object? sender, EventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            RefreshApiSummary();
            RefreshModelChoices();
            RefreshProfiles();
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
            AgentStatusTextBlock.Text = "Agent 正在规划...";

            var plan = await _runtime.TerminalAgent.RunStepAsync(
                targetSessionId,
                request,
                _agentDialogue,
                GetSelectedModelKey(),
                autoExecute: AgentAutoExecuteCheckBox.IsChecked == true);

            if (!plan.Success)
            {
                AddAgentTimeline("agent", $"规划失败: {plan.Error}");
                AgentStatusTextBlock.Text = "规划失败";
                return;
            }

            if (!string.IsNullOrWhiteSpace(plan.Analysis))
            {
                AddAgentTimeline("agent", plan.Analysis);
                _agentDialogue.Add(new TerminalAgentDialogueItem { Role = "agent", Content = plan.Analysis });
            }

            SuggestedCommandTextBox.Text = plan.SuggestedCommand;
            if (!string.IsNullOrWhiteSpace(plan.SuggestedCommand))
            {
                AddAgentTimeline("system", $"建议命令: {plan.SuggestedCommand}");
            }

            if (plan.Executed && plan.ExecutionResult is not null)
            {
                AddAgentTimeline("system", $"已执行: {plan.ExecutionResult.Command}");
                AgentStatusTextBlock.Text = "已自动执行建议命令";
                RefreshOpenSessions(targetSessionId);
            }
            else
            {
                AgentStatusTextBlock.Text = AgentAutoExecuteCheckBox.IsChecked == true ? "本轮无需执行命令" : "已生成建议命令";
            }
        }
        catch (Exception ex)
        {
            AgentStatusTextBlock.Text = "Agent 执行失败";
            ShowError(ex);
        }
        finally
        {
            _agentBusy = false;
        }
    }

    private async void ExecuteSuggestedCommand_Click(object sender, RoutedEventArgs e)
    {
        var targetSessionId = GetAgentTargetSessionId();
        if (string.IsNullOrWhiteSpace(targetSessionId) || string.IsNullOrWhiteSpace(SuggestedCommandTextBox.Text))
        {
            return;
        }

        try
        {
            var result = await _runtime.TerminalSessions.ExecuteCommandAsync(
                targetSessionId,
                SuggestedCommandTextBox.Text,
                TimeSpan.FromSeconds(Math.Max(3, _runtime.Config.Terminal.ExecApi.DefaultTimeoutSeconds)));
            AddAgentTimeline("system", $"手动执行: {result.Command}");
            RefreshOpenSessions(targetSessionId);
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private async void SaveSharingSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var config = _runtime.ConfigService.LoadOrCreate();
            config.Terminal.DefaultPowershellProgram = PowerShellProgramTextBox.Text;
            config.Terminal.DefaultPowershellArgs = PowerShellArgsTextBox.Text;
            config.Terminal.Agent.AutoExecute = AgentAutoExecuteCheckBox.IsChecked == true;
            config.Terminal.Agent.SelectedModel = GetSelectedModelKey();
            config.Terminal.ExecApi.Enabled = ApiEnabledCheckBox.IsChecked == true;
            config.Terminal.ExecApi.BindHost = ApiHostTextBox.Text;
            config.Terminal.ExecApi.Port = ParseInt(ApiPortTextBox.Text, 8765);
            config.Terminal.ExecApi.DefaultTimeoutSeconds = ParseDouble(ApiTimeoutTextBox.Text, 15);
            _runtime.SaveConfig(config);
            await _runtime.EnsureTerminalApiStateAsync();
            RefreshApiSummary();
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private async void OpenBridge_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(SelectedOpenSessionId))
        {
            return;
        }

        try
        {
            var bridge = await _runtime.TerminalSessions.StartSerialBridgeAsync(
                SelectedOpenSessionId,
                string.IsNullOrWhiteSpace(BridgePortTextBox.Text) ? null : ParseInt(BridgePortTextBox.Text, 22000));
            BridgeStatusTextBlock.Text = $"{bridge.Protocol}://{bridge.Host}:{bridge.Port}  {bridge.Message}";
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
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

    private async void ExecuteSessionCommand_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetOpenSessionParameter(sender, out var session) || string.IsNullOrWhiteSpace(session.InputText))
        {
            return;
        }

        await ExecuteSessionCommandAsync(session);
    }

    private async void SendRawSessionInput_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetOpenSessionParameter(sender, out var session) || string.IsNullOrWhiteSpace(session.InputText))
        {
            return;
        }

        try
        {
            await _runtime.TerminalSessions.SendInputAsync(session.SessionId, session.InputText);
            session.InputText = string.Empty;
            RefreshOpenSessions(session.SessionId);
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private async void InterruptSession_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetOpenSessionParameter(sender, out var session))
        {
            return;
        }

        try
        {
            await _runtime.TerminalSessions.SendInputAsync(session.SessionId, "\u0003");
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private async void CloseSessionTab_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetOpenSessionParameter(sender, out var session))
        {
            return;
        }

        await CloseSessionAsync(session.SessionId);
    }

    private async void CloseCurrentSession_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(SelectedOpenSessionId))
        {
            return;
        }

        await CloseSessionAsync(SelectedOpenSessionId);
    }

    private async void SessionInputTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            return;
        }

        if ((sender as FrameworkElement)?.DataContext is not OpenTerminalSessionViewModel session || string.IsNullOrWhiteSpace(session.InputText))
        {
            return;
        }

        e.Handled = true;
        await ExecuteSessionCommandAsync(session);
    }

    private void TerminalOutputTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox textBox)
        {
            textBox.ScrollToEnd();
        }
    }

    private async Task ExecuteSessionCommandAsync(OpenTerminalSessionViewModel session)
    {
        try
        {
            await _runtime.TerminalSessions.ExecuteCommandAsync(
                session.SessionId,
                session.InputText,
                TimeSpan.FromSeconds(Math.Max(3, _runtime.Config.Terminal.ExecApi.DefaultTimeoutSeconds)));
            session.InputText = string.Empty;
            RefreshOpenSessions(session.SessionId);
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
            await _runtime.TerminalSessions.StopSessionAsync(sessionId);
            RefreshOpenSessions();
            RefreshAgentTargets();
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
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
        var cachedInputs = _openSessions.ToDictionary(item => item.SessionId, item => item.InputText, StringComparer.OrdinalIgnoreCase);
        _openSessions.Clear();

        foreach (var session in _runtime.TerminalSessions.ListSessions())
        {
            _openSessions.Add(new OpenTerminalSessionViewModel(session)
            {
                OutputText = _runtime.TerminalSessions.GetRecentOutput(session.SessionId),
                CurrentCommandText = BuildCurrentCommandText(session.SessionId),
                MetaLine = BuildMetaLine(session),
                InputText = cachedInputs.TryGetValue(session.SessionId, out var input) ? input : string.Empty,
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
            ? $"已运行: {_runtime.TerminalApiServer.BaseUrl}"
            : $"已关闭: http://{ApiHostTextBox.Text}:{ApiPortTextBox.Text}/";
        ApiEndpointsTextBlock.Text =
            "GET /api/sessions\nGET /api/sessions/{id}/history\nGET /api/sessions/{id}/current-output\nPOST /api/exec_cmd\nPOST /api/send_input\nPOST /api/bridge/open";
        RefreshSessionSummary();
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
    }

    private void RefreshSessionSummary()
    {
        var selected = OpenSessionsTabControl.SelectedItem as OpenTerminalSessionViewModel;
        OpenSessionSummaryTextBlock.Text = $"打开中的会话: {_openSessions.Count} 个，API 共享 {_openSessions.Count(item => item.IsApiShared)} 个。";
        SessionDeckMetaTextBlock.Text = selected is null
            ? "没有选中的活动终端标签。"
            : $"当前标签: {selected.Title} · {selected.Descriptor}";
    }

    private void PersistAgentPreferences()
    {
        var config = _runtime.ConfigService.LoadOrCreate();
        config.Terminal.Agent.AutoExecute = AgentAutoExecuteCheckBox.IsChecked == true;
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

    private void AddAgentTimeline(string role, string content)
    {
        _agentTimeline.Add(new AgentTimelineItemViewModel(role, content));
        if (_agentTimeline.Count > 200)
        {
            _agentTimeline.RemoveAt(0);
        }
        AgentTimelineListBox.ScrollIntoView(_agentTimeline.LastOrDefault());
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
            ? $"当前执行: {current.Command}\n输出片段: {TrimForSingleLine(current.Output)}"
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

    private void ShowError(Exception ex)
    {
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
        private string _inputText = string.Empty;
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

        public string InputText
        {
            get => _inputText;
            set => SetField(ref _inputText, value);
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
}