using System.Collections.ObjectModel;
using System.IO.Ports;
using System.Windows;
using System.Windows.Controls;
using LuminChatWin.Core.Models;

namespace LuminChatWin.App;

public partial class TerminalControlWindow : Window
{
    private readonly AppRuntime _runtime;
    private readonly ObservableCollection<TerminalSessionInfo> _sessions = [];
    private readonly ObservableCollection<string> _historyItems = [];
    private readonly ObservableCollection<string> _agentMessages = [];
    private readonly List<TerminalAgentDialogueItem> _agentDialogue = [];

    public TerminalControlWindow(AppRuntime runtime)
    {
        _runtime = runtime;
        InitializeComponent();

        SessionsListBox.ItemsSource = _sessions;
        HistoryListBox.ItemsSource = _historyItems;
        AgentDialogueListBox.ItemsSource = _agentMessages;

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
        RefreshSessions();
        RefreshApiSummary();
    }

    private string? SelectedSessionId => (SessionsListBox.SelectedItem as TerminalSessionInfo)?.SessionId;

    private void Runtime_ConfigChanged(object? sender, EventArgs e)
    {
        Dispatcher.Invoke(() => RefreshApiSummary());
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
            RefreshSessionsPreservingSelection();
            if (!string.Equals(SelectedSessionId, e.SessionId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            OutputTextBox.AppendText(e.Text);
            OutputTextBox.ScrollToEnd();
            if (_historyItems.Count > 800)
            {
                _historyItems.RemoveAt(0);
            }
            _historyItems.Add($"[{DateTime.Now:HH:mm:ss}] {e.Kind}: {e.Text.Trim()}" );
            UpdateCurrentCommandSnapshot(e.SessionId);
        });
    }

    private void RefreshSessions_Click(object sender, RoutedEventArgs e)
    {
        RefreshSessionsPreservingSelection();
    }

    private async void ConnectPowerShell_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var session = await _runtime.TerminalSessions.CreatePowerShellSessionAsync(new TerminalPowerShellOptions
            {
                Title = PowerShellTitleTextBox.Text,
                Program = PowerShellProgramTextBox.Text,
                Arguments = PowerShellArgsTextBox.Text,
                WorkingDirectory = PowerShellWorkdirTextBox.Text,
            });
            RefreshSessions(session.SessionId);
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private async void ConnectSsh_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var session = await _runtime.TerminalSessions.CreateSshSessionAsync(new TerminalSshOptions
            {
                Title = SshTitleTextBox.Text,
                Host = SshHostTextBox.Text,
                Port = ParseInt(SshPortTextBox.Text, 22),
                Username = SshUsernameTextBox.Text,
                Password = SshPasswordBox.Password,
            });
            RefreshSessions(session.SessionId);
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private async void ConnectTelnet_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var session = await _runtime.TerminalSessions.CreateTelnetSessionAsync(new TerminalTelnetOptions
            {
                Title = TelnetTitleTextBox.Text,
                Host = TelnetHostTextBox.Text,
                Port = ParseInt(TelnetPortTextBox.Text, 23),
            });
            RefreshSessions(session.SessionId);
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private async void ConnectSerial_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var portName = SerialPortComboBox.Text;
            if (string.IsNullOrWhiteSpace(portName))
            {
                MessageBox.Show(this, "请选择串口。", "串口连接", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var session = await _runtime.TerminalSessions.CreateSerialSessionAsync(new TerminalSerialOptions
            {
                Title = SerialTitleTextBox.Text,
                PortName = portName,
                BaudRate = ParseInt(SerialBaudRateTextBox.Text, 115200),
                Parity = SerialParityTextBox.Text,
                DataBits = ParseInt(SerialDataBitsTextBox.Text, 8),
                StopBits = SerialStopBitsTextBox.Text,
            });
            RefreshSessions(session.SessionId);
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private void SessionsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        LoadSelectedSession();
    }

    private async void ExecuteCommand_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(SelectedSessionId) || string.IsNullOrWhiteSpace(CommandTextBox.Text))
        {
            return;
        }

        try
        {
            await _runtime.TerminalSessions.ExecuteCommandAsync(
                SelectedSessionId,
                CommandTextBox.Text,
                TimeSpan.FromSeconds(Math.Max(3, _runtime.Config.Terminal.ExecApi.DefaultTimeoutSeconds)));
            CommandTextBox.Clear();
            LoadSelectedSession();
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private async void SendRawInput_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(SelectedSessionId) || string.IsNullOrWhiteSpace(CommandTextBox.Text))
        {
            return;
        }

        try
        {
            await _runtime.TerminalSessions.SendInputAsync(SelectedSessionId, CommandTextBox.Text);
            CommandTextBox.Clear();
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private async void InterruptCommand_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(SelectedSessionId))
        {
            return;
        }

        try
        {
            await _runtime.TerminalSessions.SendInputAsync(SelectedSessionId, "\u0003");
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private async void CloseSession_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(SelectedSessionId))
        {
            return;
        }

        try
        {
            await _runtime.TerminalSessions.StopSessionAsync(SelectedSessionId);
            RefreshSessions();
            OutputTextBox.Clear();
            _historyItems.Clear();
            SessionTitleTextBlock.Text = "未选择会话";
            SessionMetaTextBlock.Text = string.Empty;
            CurrentCommandTextBlock.Text = string.Empty;
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private void RefreshSerialPorts_Click(object sender, RoutedEventArgs e)
    {
        RefreshSerialPorts();
    }

    private void SendAgentMessage_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(AgentUserInputTextBox.Text))
        {
            return;
        }

        AddAgentDialogue("user", AgentUserInputTextBox.Text.Trim());
        AgentUserInputTextBox.Clear();
    }

    private async void RunAgentStep_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(SelectedSessionId))
        {
            MessageBox.Show(this, "请先选择一个终端会话。", "Agent", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            var plan = await _runtime.TerminalAgent.RunStepAsync(
                SelectedSessionId,
                AgentObjectiveTextBox.Text,
                _agentDialogue,
                autoExecute: AgentAutoExecuteCheckBox.IsChecked == true);

            if (!plan.Success)
            {
                AddAgentDialogue("agent", $"规划失败: {plan.Error}");
                return;
            }

            if (!string.IsNullOrWhiteSpace(plan.Analysis))
            {
                AddAgentDialogue("agent", plan.Analysis);
            }
            if (!string.IsNullOrWhiteSpace(plan.SuggestedCommand))
            {
                SuggestedCommandTextBox.Text = plan.SuggestedCommand;
                AddAgentDialogue("agent", $"建议命令: {plan.SuggestedCommand}");
            }
            if (plan.Executed && plan.ExecutionResult is not null)
            {
                AddAgentDialogue("system", $"已执行: {plan.ExecutionResult.Command}");
                LoadSelectedSession();
            }
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private async void ExecuteSuggested_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(SelectedSessionId) || string.IsNullOrWhiteSpace(SuggestedCommandTextBox.Text))
        {
            return;
        }

        try
        {
            await _runtime.TerminalSessions.ExecuteCommandAsync(
                SelectedSessionId,
                SuggestedCommandTextBox.Text,
                TimeSpan.FromSeconds(Math.Max(3, _runtime.Config.Terminal.ExecApi.DefaultTimeoutSeconds)));
            AddAgentDialogue("system", $"手动执行: {SuggestedCommandTextBox.Text}");
            LoadSelectedSession();
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private async void ToggleApi_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var config = _runtime.ConfigService.LoadOrCreate();
            config.Terminal.DefaultPowershellProgram = PowerShellProgramTextBox.Text;
            config.Terminal.DefaultPowershellArgs = PowerShellArgsTextBox.Text;
            config.Terminal.Agent.AutoExecute = AgentAutoExecuteCheckBox.IsChecked == true;
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
        if (string.IsNullOrWhiteSpace(SelectedSessionId))
        {
            return;
        }

        try
        {
            var bridge = await _runtime.TerminalSessions.StartSerialBridgeAsync(
                SelectedSessionId,
                string.IsNullOrWhiteSpace(BridgePortTextBox.Text) ? null : ParseInt(BridgePortTextBox.Text, 22000));
            BridgeStatusTextBlock.Text = $"{bridge.Protocol}://{bridge.Host}:{bridge.Port}  {bridge.Message}";
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private void RefreshSessions(string? selectedSessionId = null)
    {
        var sessions = _runtime.TerminalSessions.ListSessions();
        _sessions.Clear();
        foreach (var session in sessions)
        {
            _sessions.Add(session);
        }

        if (!string.IsNullOrWhiteSpace(selectedSessionId))
        {
            SessionsListBox.SelectedItem = _sessions.FirstOrDefault(item => item.SessionId == selectedSessionId);
        }
    }

    private void RefreshSessionsPreservingSelection()
    {
        var selected = SelectedSessionId;
        RefreshSessions(selected);
    }

    private void LoadSelectedSession()
    {
        if (string.IsNullOrWhiteSpace(SelectedSessionId))
        {
            return;
        }

        var session = _runtime.TerminalSessions.GetSession(SelectedSessionId);
        if (session is null)
        {
            return;
        }

        SessionTitleTextBlock.Text = session.Title;
        SessionMetaTextBlock.Text = $"{session.Kind}  ·  {session.Descriptor}  ·  创建于 {session.CreatedAt}";
        OutputTextBox.Text = _runtime.TerminalSessions.GetRecentOutput(session.SessionId);
        OutputTextBox.ScrollToEnd();

        _historyItems.Clear();
        foreach (var entry in _runtime.TerminalSessions.GetHistory(session.SessionId, 200))
        {
            _historyItems.Add($"[{entry.Timestamp[11..19]}] {entry.Kind}: {entry.Text.Trim()}" );
        }

        UpdateCurrentCommandSnapshot(session.SessionId);
    }

    private void UpdateCurrentCommandSnapshot(string sessionId)
    {
        var current = _runtime.TerminalSessions.GetCurrentCommandOutput(sessionId);
        CurrentCommandTextBlock.Text = current.IsRunning
            ? $"当前执行: {current.Command}\n输出片段: {TrimForSingleLine(current.Output)}"
            : "当前没有正在执行的命令。";
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
            $"GET /api/sessions\nGET /api/sessions/{{id}}/history\nGET /api/sessions/{{id}}/current-output\nPOST /api/exec_cmd\nPOST /api/send_input\nPOST /api/bridge/open";
    }

    private void AddAgentDialogue(string role, string content)
    {
        _agentDialogue.Add(new TerminalAgentDialogueItem { Role = role, Content = content });
        _agentMessages.Add($"[{DateTime.Now:HH:mm:ss}] {role}: {content}");
        if (_agentMessages.Count > 200)
        {
            _agentMessages.RemoveAt(0);
        }
        AgentDialogueListBox.ScrollIntoView(_agentMessages.LastOrDefault());
    }

    private static int ParseInt(string text, int fallback)
    {
        return int.TryParse(text, out var value) ? value : fallback;
    }

    private static double ParseDouble(string text, double fallback)
    {
        return double.TryParse(text, out var value) ? value : fallback;
    }

    private static string TrimForSingleLine(string text)
    {
        var compact = string.Join(" ", text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
        return compact.Length <= 100 ? compact : compact[..100] + "...";
    }

    private void ShowError(Exception ex)
    {
        MessageBox.Show(this, ex.Message, "终端控制", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}