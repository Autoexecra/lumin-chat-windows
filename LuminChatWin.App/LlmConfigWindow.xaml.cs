using System.Windows;
using System.Windows.Controls;
using LuminChatWin.Core.Models;
using LuminChatWin.Core.Services;

namespace LuminChatWin.App;

public partial class LlmConfigWindow : Window
{
    private readonly AppRuntime _runtime;
    private readonly AppConfig _draft;
    private readonly Dictionary<string, Dictionary<string, Control>> _controls = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _levelOrder = [];

    public LlmConfigWindow(AppRuntime runtime)
    {
        _runtime = runtime;
        _draft = _runtime.ConfigService.LoadOrCreate();
        InitializeComponent();
        RebuildTabs();
    }

    private void RebuildTabs()
    {
        ModelTabControl.Items.Clear();
        _controls.Clear();
        _levelOrder.Clear();

        var overview = new TabItem
        {
            Header = "概览",
            Content = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = new StackPanel
                {
                    Margin = new Thickness(20, 0, 0, 0),
                    Children =
                    {
                        new TextBlock { Text = "模型说明", FontSize = 22, FontWeight = FontWeights.SemiBold },
                        new TextBlock { Text = "每个标签页对应一个模型等级。可以单独设置模型名、接口地址、API Key、温度、最大 token，并支持随时新增或删除模型。", Margin = new Thickness(0, 12, 0, 0), TextWrapping = TextWrapping.Wrap },
                    }
                }
            }
        };
        ModelTabControl.Items.Add(overview);

        foreach (var levelKey in _draft.Ai.Keys.OrderBy(ParseLevel).ThenBy(static item => item, StringComparer.OrdinalIgnoreCase))
        {
            _levelOrder.Add(levelKey);
            var panel = new StackPanel { Margin = new Thickness(20, 0, 0, 0) };
            BuildPanel(panel, levelKey);
            ModelTabControl.Items.Add(new TabItem
            {
                Header = $"Level {ParseLevel(levelKey)}",
                Tag = levelKey,
                Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            });
        }
    }

    private void BuildPanel(Panel panel, string key)
    {
        var model = _draft.Ai.TryGetValue(key, out var config) ? config : new AiModelConfig();
        var map = new Dictionary<string, Control>(StringComparer.OrdinalIgnoreCase);
        _controls[key] = map;
        panel.Children.Add(new TextBlock { Text = key.ToUpperInvariant(), FontSize = 22, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = "填写 OpenAI 兼容接口参数。", Margin = new Thickness(0, 8, 0, 16) });
        AddTextBox(panel, map, "name", "显示名称", model.Name);
        AddTextBox(panel, map, "provider", "提供商", model.Provider);
        AddTextBox(panel, map, "model", "模型 ID", model.Model);
        AddTextBox(panel, map, "base_url", "Base URL", model.BaseUrl);
        AddTextBox(panel, map, "api_key_env", "API Key 环境变量", model.ApiKeyEnv);
        AddTextBox(panel, map, "api_key", "API Key", model.ApiKey);
        AddTextBox(panel, map, "temperature", "Temperature", model.Temperature.ToString(System.Globalization.CultureInfo.InvariantCulture));
        AddTextBox(panel, map, "max_tokens", "Max Tokens", model.MaxTokens.ToString());
        var thinkingCheckBox = new CheckBox { Content = "启用 thinking", IsChecked = model.EnableThinking, Margin = new Thickness(0, 6, 0, 8) };
        map["enable_thinking"] = thinkingCheckBox;
        panel.Children.Add(thinkingCheckBox);
        panel.Children.Add(new Separator { Margin = new Thickness(0, 12, 0, 12) });
        panel.Children.Add(new TextBlock { Text = "测试配置", FontSize = 18, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = "可以直接测试当前页参数，不需要先保存。", Margin = new Thickness(0, 8, 0, 10), TextWrapping = TextWrapping.Wrap });
        AddTextBox(panel, map, "test_prompt", "测试输入", "请只回复 TEST_OK");
        var testButton = new Button { Content = "测试当前配置", Margin = new Thickness(0, 10, 0, 10), Tag = key };
        testButton.Click += TestModel_Click;
        panel.Children.Add(testButton);
        var resultBox = new TextBox
        {
            Height = 130,
            AcceptsReturn = true,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        map["test_result"] = resultBox;
        panel.Children.Add(resultBox);
    }

    private static void AddTextBox(Panel panel, Dictionary<string, Control> map, string key, string label, string value)
    {
        panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 4), FontWeight = FontWeights.SemiBold });
        var box = new TextBox { Text = value };
        map[key] = box;
        panel.Children.Add(box);
    }

    private void AddModel_Click(object sender, RoutedEventArgs e)
    {
        var nextLevel = _draft.Ai.Keys.Select(ParseLevel).DefaultIfEmpty(0).Max() + 1;
        _draft.Ai[$"level{nextLevel}"] = new AiModelConfig
        {
            Name = $"Custom Level {nextLevel}",
            Provider = "custom",
            Model = string.Empty,
            BaseUrl = string.Empty,
            Temperature = 0.1,
            MaxTokens = 8192,
            EnableThinking = true,
        };
        RebuildTabs();
        ModelTabControl.SelectedIndex = ModelTabControl.Items.Count - 1;
    }

    private void RemoveModel_Click(object sender, RoutedEventArgs e)
    {
        if (ModelTabControl.SelectedItem is not TabItem tabItem || tabItem.Tag is not string levelKey)
        {
            return;
        }

        if (_draft.Ai.Count <= 1)
        {
            MessageBox.Show(this, "至少保留一个模型配置。", "无法删除", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _draft.Ai.Remove(levelKey);
        var reordered = _draft.Ai.OrderBy(item => ParseLevel(item.Key)).Select(item => item.Value).ToList();
        _draft.Ai = new Dictionary<string, AiModelConfig>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < reordered.Count; index++)
        {
            _draft.Ai[$"level{index + 1}"] = reordered[index];
        }

        if (_draft.App.DefaultModelLevel > _draft.Ai.Count)
        {
            _draft.App.DefaultModelLevel = _draft.Ai.Count;
        }

        RebuildTabs();
        ModelTabControl.SelectedIndex = Math.Min(ModelTabControl.Items.Count - 1, 1);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        foreach (var (levelKey, controls) in _controls)
        {
            if (!_draft.Ai.TryGetValue(levelKey, out var model))
            {
                continue;
            }
            ApplyControlsToModel(model, controls);
        }

        _runtime.SaveConfig(_draft);
        MessageBox.Show(this, "模型配置已保存。", "保存成功", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private async void TestModel_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string levelKey } || !_controls.TryGetValue(levelKey, out var controls))
        {
            return;
        }

        var resultBox = (TextBox)controls["test_result"];
        resultBox.Text = "测试中...";

        try
        {
            var config = _runtime.ConfigService.LoadOrCreate();
            if (!config.Ai.TryGetValue(levelKey, out var model))
            {
                model = new AiModelConfig();
                config.Ai[levelKey] = model;
            }

            ApplyControlsToModel(model, controls);
            if (string.IsNullOrWhiteSpace(model.ApiKey) && !string.IsNullOrWhiteSpace(model.ApiKeyEnv))
            {
                model.ApiKey = Environment.GetEnvironmentVariable(model.ApiKeyEnv) ?? string.Empty;
            }

            var prompt = ((TextBox)controls["test_prompt"]).Text.Trim();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var client = new OpenAiCompatibleChatClient();
            var response = await client.CompleteAsync(
                config,
                ParseLevel(levelKey),
                [new PersistedChatMessage { Role = "user", Content = string.IsNullOrWhiteSpace(prompt) ? "请只回复 TEST_OK" : prompt }],
                null,
                cts.Token);

            resultBox.Text = response.Success
                ? $"测试成功\r\n\r\n内容:\r\n{response.Content}\r\n\r\n思考:\r\n{response.ReasoningContent}"
                : $"测试失败\r\n\r\n{response.Error}";
        }
        catch (Exception ex)
        {
            resultBox.Text = $"测试失败\r\n\r\n{ex.Message}";
        }
    }

    private static void ApplyControlsToModel(AiModelConfig model, Dictionary<string, Control> controls)
    {
        model.Name = ((TextBox)controls["name"]).Text.Trim();
        model.Provider = ((TextBox)controls["provider"]).Text.Trim();
        model.Model = ((TextBox)controls["model"]).Text.Trim();
        model.BaseUrl = ((TextBox)controls["base_url"]).Text.Trim();
        model.ApiKeyEnv = ((TextBox)controls["api_key_env"]).Text.Trim();
        model.ApiKey = ((TextBox)controls["api_key"]).Text.Trim();
        model.Temperature = double.TryParse(((TextBox)controls["temperature"]).Text.Trim(), out var temperature) ? temperature : model.Temperature;
        model.MaxTokens = int.TryParse(((TextBox)controls["max_tokens"]).Text.Trim(), out var maxTokens) ? maxTokens : model.MaxTokens;
        model.EnableThinking = ((CheckBox)controls["enable_thinking"]).IsChecked == true;
    }

    private static int ParseLevel(string key)
    {
        return key.StartsWith("level", StringComparison.OrdinalIgnoreCase) && int.TryParse(key[5..], out var level) ? level : int.MaxValue;
    }
}