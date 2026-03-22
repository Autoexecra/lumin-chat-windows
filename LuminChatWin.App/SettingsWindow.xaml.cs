using System.IO;
using System.Windows;
using System.Windows.Controls;
using LuminChatWin.Core.Models;
using LuminChatWin.Core.Services;

namespace LuminChatWin.App;

public partial class SettingsWindow : Window
{
    private readonly AppRuntime _runtime;
    private readonly AppConfig _draft;
    private readonly Dictionary<string, Control> _controls = new(StringComparer.OrdinalIgnoreCase);
    private TextBlock? _knowledgeTestStatusTextBlock;

    public SettingsWindow(AppRuntime runtime)
    {
        _runtime = runtime;
        _draft = _runtime.ConfigService.LoadOrCreate();
        InitializeComponent();
        BuildGeneral();
        BuildPrompts();
        BuildPolicy();
        BuildTerminal();
        BuildSecondary();
        BuildKnowledge();
        BuildLicense();
    }

    private void BuildGeneral()
    {
        AddTextBox(GeneralPanel, "default_model_level", "默认模型级别", _draft.App.DefaultModelLevel.ToString());
        AddComboBox(GeneralPanel, "theme_id", "主题", ThemeManager.Themes.Select(static theme => theme.Id).ToList(), _draft.App.ThemeId, themeId => ThemeManager.GetTheme(themeId).Name, false, ThemeComboBox_SelectionChanged);
        AddTextBox(GeneralPanel, "default_approval_policy", "默认审批策略", _draft.App.DefaultApprovalPolicy);
        AddTextBox(GeneralPanel, "max_tool_rounds", "最大工具轮次", _draft.App.MaxToolRounds.ToString());
        AddTextBox(GeneralPanel, "session_dir", "会话目录", _draft.App.SessionDir);
        AddTextBox(GeneralPanel, "memory_dir", "长期记忆目录", _draft.App.MemoryDir);
        AddTextBox(GeneralPanel, "report_dir", "报告目录", _draft.App.ReportDir);
        AddTextBox(GeneralPanel, "workspace_context_max_depth", "工作区摘要深度", _draft.App.WorkspaceContextMaxDepth.ToString());
        AddTextBox(GeneralPanel, "workspace_context_max_entries", "工作区摘要条目数", _draft.App.WorkspaceContextMaxEntries.ToString());
        AddCheckBox(GeneralPanel, "show_thinking", "显示模型思考", _draft.App.ShowThinking);
        AddCheckBox(GeneralPanel, "workspace_context_enabled", "启用工作区上下文", _draft.App.WorkspaceContextEnabled);
    }

    private void BuildPrompts()
    {
        AddTextBox(PromptPanel, "prompt_library_dir", "提示词库目录", _draft.Prompts.PromptLibraryDir);
        var promptRoot = ConfigService.ExpandPath(_draft.Prompts.PromptLibraryDir);
        AddInfoText(PromptPanel, $"手动修改系统提示词文件: {Path.Combine(promptRoot, "default-system.md")}");
        AddInfoText(PromptPanel, $"手动修改用户提示词文件: {Path.Combine(promptRoot, "default-user.prompt")}");
        AddMultiLineTextBox(PromptPanel, "system_prompt_template", "系统提示词追加内容", _draft.Prompts.SystemPromptTemplate);
        AddMultiLineTextBox(PromptPanel, "user_prompt_template", "用户提示词追加内容", _draft.Prompts.UserPromptTemplate == "{input}" ? string.Empty : _draft.Prompts.UserPromptTemplate);
        AddInfoText(PromptPanel, "这里填写的内容会追加到默认提示词文件后面；用户提示词可使用 {input} 占位符。留空则只使用默认提示词文件。", new Thickness(0, 6, 0, 0));
    }

    private void BuildPolicy()
    {
        AddComboBox(PolicyPanel, "command_policy_mode", "策略模式", ["blacklist", "whitelist"], _draft.CommandPolicy.Mode, mode => mode == "whitelist" ? "白名单" : "黑名单");
        AddMultiLineTextBox(PolicyPanel, "command_policy_blacklist", "黑名单", string.Join(Environment.NewLine, _draft.CommandPolicy.Blacklist));
        AddMultiLineTextBox(PolicyPanel, "command_policy_whitelist", "白名单", string.Join(Environment.NewLine, _draft.CommandPolicy.Whitelist));
        AddMultiLineTextBox(PolicyPanel, "command_policy_extension_rules", "扩展规则", string.Join(Environment.NewLine, _draft.CommandPolicy.ExtensionRules));
    }

    private void BuildTerminal()
    {
        AddCheckBox(TerminalPanel, "serial_bridge_enabled", "启用串口 SSH Bridge", _draft.Terminal.SerialSshBridge.Enabled);
        AddTextBox(TerminalPanel, "serial_bridge_bind_host", "绑定主机", _draft.Terminal.SerialSshBridge.BindHost);
        AddTextBox(TerminalPanel, "serial_bridge_port_prefix", "默认端口前缀", _draft.Terminal.SerialSshBridge.PortPrefix);
        AddTextBox(TerminalPanel, "serial_bridge_username", "桥接用户名（留空表示接受任意用户名）", _draft.Terminal.SerialSshBridge.Username);
        AddTextBox(TerminalPanel, "serial_bridge_password", "桥接密码（留空表示空密码）", _draft.Terminal.SerialSshBridge.Password);
        AddTextBox(TerminalPanel, "serial_bridge_exec_timeout", "命令超时秒数", _draft.Terminal.SerialSshBridge.ExecTimeoutSeconds.ToString("0.##"));
        AddTextBox(TerminalPanel, "serial_bridge_host_key_path", "Host Key 路径", _draft.Terminal.SerialSshBridge.HostKeyPath);
        AddInfoText(TerminalPanel, "标准 SSH 协议必须带用户名，无法真正做到完全无账号；当前实现支持用户名留空时接受任意用户名，密码留空时允许空密码。", new Thickness(0, 6, 0, 0));
    }

    private void BuildSecondary()
    {
        AddCheckBox(SecondaryPanel, "secondary_enabled", "启用辅助服务器", _draft.SecondaryServer.Enabled);
        AddTextBox(SecondaryPanel, "secondary_host", "主机", _draft.SecondaryServer.Host);
        AddTextBox(SecondaryPanel, "secondary_port", "端口", _draft.SecondaryServer.Port.ToString());
        AddTextBox(SecondaryPanel, "secondary_user", "用户", _draft.SecondaryServer.User);
        AddTextBox(SecondaryPanel, "secondary_password", "密码", _draft.SecondaryServer.Password);
    }

    private void BuildKnowledge()
    {
        AddCheckBox(KnowledgePanel, "knowledge_enabled", "启用资料库", _draft.KnowledgeBase.Enabled);
        AddTextBox(KnowledgePanel, "knowledge_host", "主机", _draft.KnowledgeBase.Host);
        AddTextBox(KnowledgePanel, "knowledge_port", "端口", _draft.KnowledgeBase.Port.ToString());
        AddTextBox(KnowledgePanel, "knowledge_username", "用户", _draft.KnowledgeBase.Username);
        AddTextBox(KnowledgePanel, "knowledge_password", "密码", _draft.KnowledgeBase.Password);
        AddTextBox(KnowledgePanel, "knowledge_root_dir", "远端根目录", _draft.KnowledgeBase.RootDir);
        AddTextBox(KnowledgePanel, "knowledge_local_cache_dir", "本地缓存目录", _draft.KnowledgeBase.LocalCacheDir);
        AddMultiLineTextBox(KnowledgePanel, "knowledge_patterns", "匹配模式", string.Join(Environment.NewLine, _draft.KnowledgeBase.Patterns));
        var testButton = new Button { Content = "测试资料库连接" };
        testButton.Click += TestKnowledgeConnection_Click;
        KnowledgePanel.Children.Add(testButton);
        _knowledgeTestStatusTextBlock = new TextBlock
        {
            Margin = new Thickness(0, 8, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            Foreground = (System.Windows.Media.Brush?)FindResource("MutedBrush"),
            Text = "可以用它快速验证远端资料库是否可访问。",
        };
        KnowledgePanel.Children.Add(_knowledgeTestStatusTextBlock);
    }

    private void BuildLicense()
    {
        AddCheckBox(LicensePanel, "license_enabled", "启用许可证校验", _draft.License.Enabled);
        AddTextBox(LicensePanel, "license_subject", "Subject", _draft.License.Subject);
        AddTextBox(LicensePanel, "license_file", "许可证文件", _draft.License.LicenseFile);
        AddTextBox(LicensePanel, "license_secret_env", "密钥环境变量", _draft.License.SecretEnv);
        AddTextBox(LicensePanel, "license_secret", "密钥", _draft.License.Secret);
        AddCheckBox(LicensePanel, "debug_enabled", "启用调试模式", _draft.Log.DebugMode.Enabled);
        AddCheckBox(LicensePanel, "debug_prompts", "记录 LLM Prompt", _draft.Log.DebugMode.ShowLlmPrompts);
        AddCheckBox(LicensePanel, "debug_responses", "记录 LLM Response", _draft.Log.DebugMode.ShowLlmResponses);
        AddTextBox(LicensePanel, "debug_log_dir", "调试日志目录", _draft.Log.DebugMode.LogDir);
        AddInfoText(LicensePanel, "启用后会把每次请求和返回分别落盘到日志目录，便于定位提示词和模型返回问题。", new Thickness(0, 6, 0, 0));
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _draft.App.DefaultModelLevel = ReadInt("default_model_level", _draft.App.DefaultModelLevel);
        _draft.App.ThemeId = ReadComboBoxText("theme_id", _draft.App.ThemeId);
        _draft.App.DefaultApprovalPolicy = ReadText("default_approval_policy", _draft.App.DefaultApprovalPolicy);
        _draft.App.MaxToolRounds = ReadInt("max_tool_rounds", _draft.App.MaxToolRounds);
        _draft.App.SessionDir = ReadText("session_dir", _draft.App.SessionDir);
        _draft.App.MemoryDir = ReadText("memory_dir", _draft.App.MemoryDir);
        _draft.App.ReportDir = ReadText("report_dir", _draft.App.ReportDir);
        _draft.App.WorkspaceContextMaxDepth = ReadInt("workspace_context_max_depth", _draft.App.WorkspaceContextMaxDepth);
        _draft.App.WorkspaceContextMaxEntries = ReadInt("workspace_context_max_entries", _draft.App.WorkspaceContextMaxEntries);
        _draft.App.ShowThinking = ReadCheckBox("show_thinking", _draft.App.ShowThinking);
        _draft.App.WorkspaceContextEnabled = ReadCheckBox("workspace_context_enabled", _draft.App.WorkspaceContextEnabled);

        _draft.Prompts.PromptLibraryDir = ReadText("prompt_library_dir", _draft.Prompts.PromptLibraryDir);
        _draft.Prompts.SystemPromptTemplate = ReadText("system_prompt_template", _draft.Prompts.SystemPromptTemplate);
        _draft.Prompts.SelectedSystemPromptFile = string.Empty;
        _draft.Prompts.SelectedUserPromptFile = string.Empty;
        _draft.Prompts.UserPromptTemplate = ReadText("user_prompt_template", string.Empty);

        _draft.CommandPolicy.Mode = ReadComboBoxText("command_policy_mode", _draft.CommandPolicy.Mode);
        _draft.CommandPolicy.Blacklist = ReadLines("command_policy_blacklist");
        _draft.CommandPolicy.Whitelist = ReadLines("command_policy_whitelist");
        _draft.CommandPolicy.ExtensionRules = ReadLines("command_policy_extension_rules");

        _draft.Terminal.SerialSshBridge.Enabled = ReadCheckBox("serial_bridge_enabled", _draft.Terminal.SerialSshBridge.Enabled);
        _draft.Terminal.SerialSshBridge.BindHost = ReadText("serial_bridge_bind_host", _draft.Terminal.SerialSshBridge.BindHost);
        _draft.Terminal.SerialSshBridge.PortPrefix = ReadText("serial_bridge_port_prefix", _draft.Terminal.SerialSshBridge.PortPrefix);
        _draft.Terminal.SerialSshBridge.Username = ReadText("serial_bridge_username", _draft.Terminal.SerialSshBridge.Username);
        _draft.Terminal.SerialSshBridge.Password = ReadText("serial_bridge_password", _draft.Terminal.SerialSshBridge.Password);
        _draft.Terminal.SerialSshBridge.ExecTimeoutSeconds = ReadDouble("serial_bridge_exec_timeout", _draft.Terminal.SerialSshBridge.ExecTimeoutSeconds);
        _draft.Terminal.SerialSshBridge.HostKeyPath = ReadText("serial_bridge_host_key_path", _draft.Terminal.SerialSshBridge.HostKeyPath);

        _draft.SecondaryServer.Enabled = ReadCheckBox("secondary_enabled", _draft.SecondaryServer.Enabled);
        _draft.SecondaryServer.Host = ReadText("secondary_host", _draft.SecondaryServer.Host);
        _draft.SecondaryServer.Port = ReadInt("secondary_port", _draft.SecondaryServer.Port);
        _draft.SecondaryServer.User = ReadText("secondary_user", _draft.SecondaryServer.User);
        _draft.SecondaryServer.Password = ReadText("secondary_password", _draft.SecondaryServer.Password);

        _draft.KnowledgeBase.Enabled = ReadCheckBox("knowledge_enabled", _draft.KnowledgeBase.Enabled);
        _draft.KnowledgeBase.Host = ReadText("knowledge_host", _draft.KnowledgeBase.Host);
        _draft.KnowledgeBase.Port = ReadInt("knowledge_port", _draft.KnowledgeBase.Port);
        _draft.KnowledgeBase.Username = ReadText("knowledge_username", _draft.KnowledgeBase.Username);
        _draft.KnowledgeBase.Password = ReadText("knowledge_password", _draft.KnowledgeBase.Password);
        _draft.KnowledgeBase.RootDir = ReadText("knowledge_root_dir", _draft.KnowledgeBase.RootDir);
        _draft.KnowledgeBase.LocalCacheDir = ReadText("knowledge_local_cache_dir", _draft.KnowledgeBase.LocalCacheDir);
        _draft.KnowledgeBase.Patterns = ReadLines("knowledge_patterns");

        _draft.License.Enabled = ReadCheckBox("license_enabled", _draft.License.Enabled);
        _draft.License.Subject = ReadText("license_subject", _draft.License.Subject);
        _draft.License.LicenseFile = ReadText("license_file", _draft.License.LicenseFile);
        _draft.License.SecretEnv = ReadText("license_secret_env", _draft.License.SecretEnv);
        _draft.License.Secret = ReadText("license_secret", _draft.License.Secret);
        _draft.Log.DebugMode.Enabled = ReadCheckBox("debug_enabled", _draft.Log.DebugMode.Enabled);
        _draft.Log.DebugMode.ShowLlmPrompts = ReadCheckBox("debug_prompts", _draft.Log.DebugMode.ShowLlmPrompts);
        _draft.Log.DebugMode.ShowLlmResponses = ReadCheckBox("debug_responses", _draft.Log.DebugMode.ShowLlmResponses);
        _draft.Log.DebugMode.LogDir = ReadText("debug_log_dir", _draft.Log.DebugMode.LogDir);

        _runtime.SaveConfig(_draft);
        MessageBox.Show(this, "综合设置已保存。", "保存成功", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void TestKnowledgeConnection_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var settings = new SshConnectionSettings
            {
                Host = ReadText("knowledge_host", _draft.KnowledgeBase.Host),
                Port = ReadInt("knowledge_port", _draft.KnowledgeBase.Port),
                Username = ReadText("knowledge_username", _draft.KnowledgeBase.Username),
                Password = ReadText("knowledge_password", _draft.KnowledgeBase.Password),
                Timeout = TimeSpan.FromSeconds(10),
            };
            var rootDir = ReadText("knowledge_root_dir", _draft.KnowledgeBase.RootDir);
            var entries = new SshService().ListDirectory(settings, rootDir, false, 5);
            if (_knowledgeTestStatusTextBlock is not null)
            {
                _knowledgeTestStatusTextBlock.Text = $"资料库连接成功。路径 {rootDir} 可访问，返回 {entries.Count} 条记录。";
            }
        }
        catch (Exception ex)
        {
            if (_knowledgeTestStatusTextBlock is not null)
            {
                _knowledgeTestStatusTextBlock.Text = $"资料库连接失败: {ex.Message}";
            }
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void AddTextBox(Panel panel, string key, string label, string value)
    {
        panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 4), FontWeight = FontWeights.SemiBold });
        var box = new TextBox { Text = value };
        _controls[key] = box;
        panel.Children.Add(box);
    }

    private void AddMultiLineTextBox(Panel panel, string key, string label, string value)
    {
        panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 4), FontWeight = FontWeights.SemiBold });
        var box = new TextBox { Text = value, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 120, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        _controls[key] = box;
        panel.Children.Add(box);
    }

    private void AddCheckBox(Panel panel, string key, string label, bool value)
    {
        var box = new CheckBox { Content = label, IsChecked = value, Margin = new Thickness(0, 6, 0, 8) };
        _controls[key] = box;
        panel.Children.Add(box);
    }

    private void AddComboBox(Panel panel, string key, string label, IReadOnlyList<string> items, string selected, Func<string, string>? displaySelector = null, bool allowEmpty = false, SelectionChangedEventHandler? selectionChanged = null)
    {
        panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 4), FontWeight = FontWeights.SemiBold });
        var comboBox = new ComboBox { DisplayMemberPath = "Display", SelectedValuePath = "Value" };
        var source = new List<ComboItem>();
        if (allowEmpty)
        {
            source.Add(new ComboItem(string.Empty, "未选择"));
        }
        source.AddRange(items.Select(item => new ComboItem(item, displaySelector?.Invoke(item) ?? item)));
        comboBox.ItemsSource = source;
        comboBox.SelectedValue = selected;
        if (selectionChanged is not null)
        {
            comboBox.SelectionChanged += selectionChanged;
        }
        _controls[key] = comboBox;
        panel.Children.Add(comboBox);
    }

    private void AddInfoText(Panel panel, string text, Thickness? margin = null)
    {
        panel.Children.Add(new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (System.Windows.Media.Brush?)FindResource("MutedBrush"),
            Margin = margin ?? new Thickness(0, 4, 0, 8),
        });
    }

    private string ReadText(string key, string fallback) => _controls[key] is TextBox box ? box.Text.Trim() : fallback;

    private string ReadComboBoxText(string key, string fallback) => _controls[key] is ComboBox box ? box.SelectedValue?.ToString() ?? fallback : fallback;

    private int ReadInt(string key, int fallback) => int.TryParse(ReadText(key, fallback.ToString()), out var value) ? value : fallback;

    private double ReadDouble(string key, double fallback) => double.TryParse(ReadText(key, fallback.ToString("0.##")), out var value) ? value : fallback;

    private bool ReadCheckBox(string key, bool fallback) => _controls[key] is CheckBox box ? box.IsChecked == true : fallback;

    private void ThemeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox comboBox || comboBox.SelectedValue is not string themeId || string.IsNullOrWhiteSpace(themeId) || Application.Current is null)
        {
            return;
        }

        ThemeManager.ApplyTheme(Application.Current.Resources, themeId);
    }

    private List<string> ReadLines(string key)
    {
        return ReadText(key, string.Empty)
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
            .Select(static line => line.Trim())
            .Where(static line => !string.IsNullOrWhiteSpace(line))
            .ToList();
    }

    private sealed record ComboItem(string Value, string Display);
}