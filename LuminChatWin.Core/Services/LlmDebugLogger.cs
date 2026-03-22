using System.Text;
using LuminChatWin.Core.Models;

namespace LuminChatWin.Core.Services;

internal static class LlmDebugLogger
{
    private static readonly object SyncRoot = new();

    public static void LogRequest(AppConfig config, int modelLevel, string endpoint, string requestBody)
    {
        if (!config.Log.DebugMode.Enabled || !config.Log.DebugMode.ShowLlmPrompts)
        {
            return;
        }

        WriteEntry(config, modelLevel, "request", $"Endpoint: {endpoint}\r\n\r\n{requestBody}");
    }

    public static void LogResponse(AppConfig config, int modelLevel, string responseBody, bool success)
    {
        if (!config.Log.DebugMode.Enabled || !config.Log.DebugMode.ShowLlmResponses)
        {
            return;
        }

        WriteEntry(config, modelLevel, success ? "response" : "error", responseBody);
    }

    private static void WriteEntry(AppConfig config, int modelLevel, string kind, string content)
    {
        try
        {
            var logDir = ConfigService.ExpandPath(config.Log.DebugMode.LogDir);
            Directory.CreateDirectory(logDir);
            var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
            var filePath = Path.Combine(logDir, $"level{modelLevel}-{timestamp}-{kind}.log");

            lock (SyncRoot)
            {
                File.WriteAllText(filePath, new StringBuilder()
                    .AppendLine($"timestamp: {DateTime.Now:O}")
                    .AppendLine($"kind: {kind}")
                    .AppendLine()
                    .Append(content)
                    .ToString(), Encoding.UTF8);
            }
        }
        catch
        {
        }
    }
}