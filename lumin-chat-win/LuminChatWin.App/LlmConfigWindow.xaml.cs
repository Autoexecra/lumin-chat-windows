using System.Windows;
using System.Windows.Controls;
using LuminChatWin.Core.Models;

namespace LuminChatWin.App;

public partial class LlmConfigWindow : Window
{
    private readonly AppRuntime _runtime;
    private readonly AppConfig _draft;
    private readonly Dictionary<string, Dictionary<string, Control>> _controls = new(StringComparer.OrdinalIgnoreCase);

    public LlmConfigWindow(AppRuntime runtime)
    {
        _runtime = runtime;
        _draft = _runtime.ConfigService.LoadOrCreate();
        InitializeComponent();
        BuildPanel(Level1Panel, "level1");
        BuildPanel(Level2Panel, "level2");
        BuildPanel(Level3Panel, "level3");
        BuildPanel(Level4Panel, "level4");
        BuildPanel(Level5Panel, "level5");
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
    }

    private static void AddTextBox(Panel panel, Dictionary<string, Control> map, string key, string label, string value)
    {
        panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 4), FontWeight = FontWeights.SemiBold });
        var box = new TextBox { Text = value };
        map[key] = box;
        panel.Children.Add(box);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        foreach (var (levelKey, controls) in _controls)
        {
            var model = _draft.Ai[levelKey];
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

        _runtime.SaveConfig(_draft);
        MessageBox.Show(this, "模型配置已保存。", "保存成功", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}