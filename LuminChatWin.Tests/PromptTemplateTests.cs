using LuminChatWin.Core.Models;
using LuminChatWin.Core.Services;

namespace LuminChatWin.Tests;

public sealed class PromptTemplateTests
{
    [Fact]
    public void PromptTemplateService_AppliesInlineUserTemplate()
    {
        var config = AppConfig.CreateDefault();
        config.Prompts.UserPromptTemplate = "请按企业规范处理以下请求：\n{input}";

        var result = PromptTemplateService.ApplyUserPromptTemplate(config, "检查当前 git diff");

        Assert.Contains("企业规范", result);
        Assert.Contains("检查当前 git diff", result);
    }

    [Fact]
    public void PromptTemplateService_LoadsTemplateFromLibraryFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "LuminChatWinTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "system.md"), "这是外部系统提示词。");
        var config = AppConfig.CreateDefault();
        config.Prompts.PromptLibraryDir = root;
        config.Prompts.SelectedSystemPromptFile = "system.md";

        var result = PromptTemplateService.ResolveSystemPromptTemplate(config);

        Assert.Equal("这是外部系统提示词。", result);
    }
}