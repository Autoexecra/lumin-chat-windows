using System.Windows;
using System.Windows.Controls;
using LuminChatWin.Core.Models;

namespace LuminChatWin.App;

public partial class SettingsWindow : Window
{
    private readonly AppRuntime _runtime;
    private readonly AppConfig _draft;
    private readonly Dictionary<string, Control> _controls = new(StringComparer.OrdinalIgnoreCase);

    public SettingsWindow(AppRuntime runtime)
    {
        _runtime = runtime;
        _draft = _runtime.ConfigService.LoadOrCreate();
        InitializeComponent();
        BuildGeneral();
        BuildPrompts();
        BuildPolicy();
        BuildSecondary();
        BuildKnowledge();
        BuildDeploy();
        BuildLicense();
    }

    private void BuildGeneral()
    {
        AddTextBox(GeneralPanel, "default_model_level", "默认模型级别", _draft.App.DefaultModelLevel.ToString());
        AddComboBox(GeneralPanel, "theme_id", "主题", ThemeManager.Themes.Select(static theme => theme.Id).ToList(), _draft.App.ThemeId, themeId => ThemeManager.GetTheme(themeId).Name);
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
        AddComboBox(PromptPanel, "selected_system_prompt_file", "系统提示词文件", _runtime.GetPromptFiles().ToList(), _draft.Prompts.SelectedSystemPromptFile, value => string.IsNullOrWhiteSpace(value) ? "未选择" : value, true);
        AddMultiLineTextBox(PromptPanel, "system_prompt_template", "系统提示词补充模板", _draft.Prompts.SystemPromptTemplate);
        AddComboBox(PromptPanel, "selected_user_prompt_file", "用户提示词文件", _runtime.GetPromptFiles().ToList(), _draft.Prompts.SelectedUserPromptFile, value => string.IsNullOrWhiteSpace(value) ? "未选择" : value, true);
        AddMultiLineTextBox(PromptPanel, "user_prompt_template", "用户提示词模板", _draft.Prompts.UserPromptTemplate);
        PromptPanel.Children.Add(new TextBlock
        {
            Text = "用户提示词模板可使用 {input} 占位符。若选择了外部文件，则优先读取提示词库中的文件内容。",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
        });
    }

    private void BuildPolicy()
    {
        AddTextBox(PolicyPanel, "command_policy_mode", "策略模式", _draft.CommandPolicy.Mode);
        AddMultiLineTextBox(PolicyPanel, "command_policy_blacklist", "黑名单", string.Join(Environment.NewLine, _draft.CommandPolicy.Blacklist));
        AddMultiLineTextBox(PolicyPanel, "command_policy_whitelist", "白名单", string.Join(Environment.NewLine, _draft.CommandPolicy.Whitelist));
        AddMultiLineTextBox(PolicyPanel, "command_policy_extension_rules", "扩展规则", string.Join(Environment.NewLine, _draft.CommandPolicy.ExtensionRules));
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
        AddCheckBox(KnowledgePanel, "knowledge_enabled", "启用知识库", _draft.KnowledgeBase.Enabled);
        AddTextBox(KnowledgePanel, "knowledge_host", "主机", _draft.KnowledgeBase.Host);
        AddTextBox(KnowledgePanel, "knowledge_port", "端口", _draft.KnowledgeBase.Port.ToString());
        AddTextBox(KnowledgePanel, "knowledge_username", "用户", _draft.KnowledgeBase.Username);
        AddTextBox(KnowledgePanel, "knowledge_password", "密码", _draft.KnowledgeBase.Password);
        AddTextBox(KnowledgePanel, "knowledge_root_dir", "根目录", _draft.KnowledgeBase.RootDir);
        AddMultiLineTextBox(KnowledgePanel, "knowledge_patterns", "匹配模式", string.Join(Environment.NewLine, _draft.KnowledgeBase.Patterns));
    }

    private void BuildDeploy()
    {
        AddTextBox(DeployPanel, "deploy_host", "部署主机", _draft.Deploy.Host);
        AddTextBox(DeployPanel, "deploy_port", "部署端口", _draft.Deploy.Port.ToString());
        AddTextBox(DeployPanel, "deploy_user", "部署用户", _draft.Deploy.User);
        AddTextBox(DeployPanel, "deploy_remote_dir", "部署目录", _draft.Deploy.RemoteDir);
        AddCheckBox(DeployPanel, "build_enabled", "启用构建服务器", _draft.BuildServer.Enabled);
        AddTextBox(DeployPanel, "build_host", "构建主机", _draft.BuildServer.Host);
        AddTextBox(DeployPanel, "build_port", "构建端口", _draft.BuildServer.Port.ToString());
        AddTextBox(DeployPanel, "build_user", "构建用户", _draft.BuildServer.User);
        AddTextBox(DeployPanel, "build_password", "构建密码", _draft.BuildServer.Password);
        AddTextBox(DeployPanel, "build_remote_dir", "构建目录", _draft.BuildServer.RemoteDir);
    }

    private void BuildLicense()
    {
        AddCheckBox(LicensePanel, "license_enabled", "启用许可证校验", _draft.License.Enabled);
        AddTextBox(LicensePanel, "license_subject", "Subject", _draft.License.Subject);
        AddTextBox(LicensePanel, "license_file", "许可证文件", _draft.License.LicenseFile);
        AddTextBox(LicensePanel, "license_secret_env", "密钥环境变量", _draft.License.SecretEnv);
        AddTextBox(LicensePanel, "license_secret", "密钥", _draft.License.Secret);
        AddCheckBox(LicensePanel, "debug_enabled", "启用调试模式", _draft.Log.DebugMode.Enabled);
        AddCheckBox(LicensePanel, "debug_prompts", "显示 LLM Prompt", _draft.Log.DebugMode.ShowLlmPrompts);
        AddCheckBox(LicensePanel, "debug_responses", "显示 LLM Response", _draft.Log.DebugMode.ShowLlmResponses);
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
        _draft.Prompts.SelectedSystemPromptFile = ReadComboBoxText("selected_system_prompt_file", _draft.Prompts.SelectedSystemPromptFile);
        _draft.Prompts.SystemPromptTemplate = ReadText("system_prompt_template", _draft.Prompts.SystemPromptTemplate);
        _draft.Prompts.SelectedUserPromptFile = ReadComboBoxText("selected_user_prompt_file", _draft.Prompts.SelectedUserPromptFile);
        _draft.Prompts.UserPromptTemplate = ReadText("user_prompt_template", _draft.Prompts.UserPromptTemplate);

        _draft.CommandPolicy.Mode = ReadText("command_policy_mode", _draft.CommandPolicy.Mode);
        _draft.CommandPolicy.Blacklist = ReadLines("command_policy_blacklist");
        _draft.CommandPolicy.Whitelist = ReadLines("command_policy_whitelist");
        _draft.CommandPolicy.ExtensionRules = ReadLines("command_policy_extension_rules");

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
        _draft.KnowledgeBase.Patterns = ReadLines("knowledge_patterns");

        _draft.Deploy.Host = ReadText("deploy_host", _draft.Deploy.Host);
        _draft.Deploy.Port = ReadInt("deploy_port", _draft.Deploy.Port);
        _draft.Deploy.User = ReadText("deploy_user", _draft.Deploy.User);
        _draft.Deploy.RemoteDir = ReadText("deploy_remote_dir", _draft.Deploy.RemoteDir);
        _draft.BuildServer.Enabled = ReadCheckBox("build_enabled", _draft.BuildServer.Enabled);
        _draft.BuildServer.Host = ReadText("build_host", _draft.BuildServer.Host);
        _draft.BuildServer.Port = ReadInt("build_port", _draft.BuildServer.Port);
        _draft.BuildServer.User = ReadText("build_user", _draft.BuildServer.User);
        _draft.BuildServer.Password = ReadText("build_password", _draft.BuildServer.Password);
        _draft.BuildServer.RemoteDir = ReadText("build_remote_dir", _draft.BuildServer.RemoteDir);

        _draft.License.Enabled = ReadCheckBox("license_enabled", _draft.License.Enabled);
        _draft.License.Subject = ReadText("license_subject", _draft.License.Subject);
        _draft.License.LicenseFile = ReadText("license_file", _draft.License.LicenseFile);
        _draft.License.SecretEnv = ReadText("license_secret_env", _draft.License.SecretEnv);
        _draft.License.Secret = ReadText("license_secret", _draft.License.Secret);
        _draft.Log.DebugMode.Enabled = ReadCheckBox("debug_enabled", _draft.Log.DebugMode.Enabled);
        _draft.Log.DebugMode.ShowLlmPrompts = ReadCheckBox("debug_prompts", _draft.Log.DebugMode.ShowLlmPrompts);
        _draft.Log.DebugMode.ShowLlmResponses = ReadCheckBox("debug_responses", _draft.Log.DebugMode.ShowLlmResponses);

        _runtime.SaveConfig(_draft);
        MessageBox.Show(this, "综合设置已保存。", "保存成功", MessageBoxButton.OK, MessageBoxImage.Information);
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

    private void AddComboBox(Panel panel, string key, string label, IReadOnlyList<string> items, string selected, Func<string, string>? displaySelector = null, bool allowEmpty = false)
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
        _controls[key] = comboBox;
        panel.Children.Add(comboBox);
    }

    private string ReadText(string key, string fallback) => _controls[key] is TextBox box ? box.Text.Trim() : fallback;

    private string ReadComboBoxText(string key, string fallback) => _controls[key] is ComboBox box ? box.SelectedValue?.ToString() ?? fallback : fallback;

    private int ReadInt(string key, int fallback) => int.TryParse(ReadText(key, fallback.ToString()), out var value) ? value : fallback;

    private bool ReadCheckBox(string key, bool fallback) => _controls[key] is CheckBox box ? box.IsChecked == true : fallback;

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