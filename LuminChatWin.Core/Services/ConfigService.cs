using System.Text.Json;
using LuminChatWin.Core.Models;

namespace LuminChatWin.Core.Services;

public sealed class ConfigService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = null,
        WriteIndented = true,
    };

    public string ConfigPath { get; }

    public ConfigService(string configPath)
    {
        ConfigPath = ExpandPath(configPath);
    }

    public AppConfig LoadOrCreate()
    {
        var config = AppConfig.CreateDefault();
        if (File.Exists(ConfigPath))
        {
            var json = File.ReadAllText(ConfigPath);
            var loaded = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? AppConfig.CreateDefault();
            config = Merge(config, loaded);
        }

        ApplyEnvironmentOverrides(config);
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        if (!File.Exists(ConfigPath))
        {
            Save(config);
        }

        return config;
    }

    public void Save(AppConfig config)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(config, JsonOptions));
    }

    public static string ExpandPath(string rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return rawPath;
        }

        if (rawPath.StartsWith("~/", StringComparison.Ordinal) || rawPath.StartsWith("~\\", StringComparison.Ordinal))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, rawPath[2..].Replace('/', Path.DirectorySeparatorChar));
        }

        return Path.GetFullPath(Environment.ExpandEnvironmentVariables(rawPath));
    }

    private static void ApplyEnvironmentOverrides(AppConfig config)
    {
        foreach (var (_, model) in config.Ai)
        {
            if (!string.IsNullOrWhiteSpace(model.ApiKeyEnv))
            {
                var value = Environment.GetEnvironmentVariable(model.ApiKeyEnv);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    model.ApiKey = value;
                }
            }

            if (string.IsNullOrWhiteSpace(model.ApiKey))
            {
                var shared = Environment.GetEnvironmentVariable("SILICONFLOW_API_KEY");
                if (!string.IsNullOrWhiteSpace(shared))
                {
                    model.ApiKey = shared;
                }
            }
        }
    }

    private static AppConfig Merge(AppConfig target, AppConfig source)
    {
        target.App = source.App ?? target.App;
        target.CommandPolicy = source.CommandPolicy ?? target.CommandPolicy;
        target.SecondaryServer = source.SecondaryServer ?? target.SecondaryServer;
        target.ModelEscalation = source.ModelEscalation ?? target.ModelEscalation;
        target.KnowledgeBase = source.KnowledgeBase ?? target.KnowledgeBase;
        target.License = source.License ?? target.License;
        target.Deploy = source.Deploy ?? target.Deploy;
        target.BuildServer = source.BuildServer ?? target.BuildServer;
        target.Log = source.Log ?? target.Log;
        target.Prompts = source.Prompts ?? target.Prompts;
        if (source.Ai.Count > 0)
        {
            foreach (var (key, model) in source.Ai)
            {
                target.Ai[key] = model;
            }
        }

        return target;
    }
}