using System.Text;
using LuminChatWin.Core.Models;

namespace LuminChatWin.Core.Services;

public static class PromptTemplateService
{
    public static string ResolveSystemPromptTemplate(AppConfig config)
    {
        var baseTemplate = LoadTemplate(config.Prompts.PromptLibraryDir, "default-system.md");
        return AppendTemplate(baseTemplate, config.Prompts.SystemPromptTemplate);
    }

    public static string ResolveUserPromptTemplate(AppConfig config)
    {
        var baseTemplate = LoadTemplate(config.Prompts.PromptLibraryDir, "default-user.prompt");
        var inlineTemplate = string.IsNullOrWhiteSpace(config.Prompts.UserPromptTemplate) ? string.Empty : config.Prompts.UserPromptTemplate;
        var combined = AppendTemplate(baseTemplate, inlineTemplate);
        return string.IsNullOrWhiteSpace(combined) ? "{input}" : combined;
    }

    public static IReadOnlyList<string> ListPromptFiles(AppConfig config)
    {
        var root = ConfigService.ExpandPath(config.Prompts.PromptLibraryDir);
        Directory.CreateDirectory(root);
        return Directory.EnumerateFiles(root, "*.*", SearchOption.TopDirectoryOnly)
            .Where(path => path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".prompt", StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFileName)
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .OrderBy(static name => name, StringComparer.OrdinalIgnoreCase)
            .ToList()!;
    }

    public static string ApplyUserPromptTemplate(AppConfig config, string userInput)
    {
        var template = ResolveUserPromptTemplate(config);
        if (string.IsNullOrWhiteSpace(template))
        {
            return userInput;
        }

        if (template.Contains("{input}", StringComparison.Ordinal))
        {
            return template.Replace("{input}", userInput, StringComparison.Ordinal);
        }

        var builder = new StringBuilder();
        builder.AppendLine(template.Trim());
        builder.AppendLine();
        builder.Append(userInput);
        return builder.ToString();
    }

    private static string LoadTemplate(string libraryDir, string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return string.Empty;
        }

        var root = ConfigService.ExpandPath(libraryDir);
        Directory.CreateDirectory(root);
        var fullPath = Path.Combine(root, fileName);
        return File.Exists(fullPath) ? File.ReadAllText(fullPath) : string.Empty;
    }

    private static string AppendTemplate(string baseTemplate, string appendedTemplate)
    {
        var baseText = baseTemplate.Trim();
        var appendText = appendedTemplate.Trim();

        if (string.IsNullOrWhiteSpace(baseText))
        {
            return appendText;
        }

        if (string.IsNullOrWhiteSpace(appendText))
        {
            return baseText;
        }

        return $"{baseText}{Environment.NewLine}{Environment.NewLine}{appendText}";
    }
}