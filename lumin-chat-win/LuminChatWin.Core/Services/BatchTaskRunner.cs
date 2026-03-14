using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LuminChatWin.Core.Models;

namespace LuminChatWin.Core.Services;

public sealed class BatchTaskRunner
{
    private readonly Func<ChatAgent> _agentFactory;
    private string _reportDir;

    public BatchTaskRunner(Func<ChatAgent> agentFactory, string? reportDir = null)
    {
        _agentFactory = agentFactory;
        _reportDir = ConfigService.ExpandPath(reportDir ?? "~/lumin-chat-win-reports");
    }

    public async Task<IReadOnlyList<BatchTaskResult>> RunFileAsync(string taskFile, string? reportDir = null, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(reportDir))
        {
            _reportDir = ConfigService.ExpandPath(reportDir);
        }

        Directory.CreateDirectory(_reportDir);
        var tasks = LoadTasks(taskFile);
        var results = new List<BatchTaskResult>();
        ChatAgent? agent = null;

        for (var index = 0; index < tasks.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            agent ??= _agentFactory();
            if (index > 0 && tasks[index].NewSession)
            {
                agent.CreateNewSession();
            }

            var startedAt = DateTime.UtcNow.ToString("O");
            AgentRunResult execution;
            try
            {
                execution = await agent.RunWithTraceAsync(tasks[index].Task, null, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                execution = new AgentRunResult
                {
                    Success = false,
                    Error = ex.Message,
                    SessionId = agent.CurrentSession.SessionId,
                    Cwd = agent.Cwd,
                };
            }

            var result = new BatchTaskResult
            {
                Index = index + 1,
                Task = tasks[index].Task,
                NewSession = tasks[index].NewSession,
                StartedAt = startedAt,
                FinishedAt = DateTime.UtcNow.ToString("O"),
                Success = execution.Success,
                Content = execution.Content,
                Error = execution.Error,
                ToolRecords = execution.ToolRecords,
                SessionId = execution.SessionId,
                Cwd = execution.Cwd,
            };
            result.ReportPath = WriteReport(result);
            results.Add(result);
        }

        return results;
    }

    private List<(string Task, bool NewSession)> LoadTasks(string taskFile)
    {
        var root = JsonDocument.Parse(File.ReadAllText(ConfigService.ExpandPath(taskFile))).RootElement;
        if (root.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("批量任务文件必须是 JSON 数组。");
        }

        var tasks = new List<(string Task, bool NewSession)>();
        foreach (var item in root.EnumerateArray())
        {
            var task = item.TryGetProperty("task", out var taskElement) ? taskElement.GetString() ?? string.Empty : string.Empty;
            if (string.IsNullOrWhiteSpace(task))
            {
                throw new InvalidOperationException("批量任务缺少 task 字段。");
            }

            var newSession = item.TryGetProperty("new_session", out var sessionElement) && sessionElement.ValueKind is JsonValueKind.False or JsonValueKind.True
                ? sessionElement.GetBoolean()
                : true;
            tasks.Add((task, newSession));
        }

        return tasks;
    }

    private string WriteReport(BatchTaskResult result)
    {
        var path = Path.Combine(_reportDir, $"{result.Index:00}-{Slugify(result.Task)}.md");
        var builder = new StringBuilder();
        builder.AppendLine($"# 任务报告 {result.Index:00}");
        builder.AppendLine();
        builder.AppendLine("## 1. 基本信息");
        builder.AppendLine();
        builder.AppendLine($"- 任务描述: {result.Task}");
        builder.AppendLine($"- 是否新建会话: {(result.NewSession ? "是" : "否")}");
        builder.AppendLine($"- 会话 ID: {result.SessionId}");
        builder.AppendLine($"- 工作目录: {result.Cwd}");
        builder.AppendLine($"- 开始时间: {result.StartedAt}");
        builder.AppendLine($"- 结束时间: {result.FinishedAt}");
        builder.AppendLine($"- 执行结果: {(result.Success ? "成功" : "失败")}");
        builder.AppendLine();
        builder.AppendLine("## 2. 工具执行记录");
        builder.AppendLine();

        if (result.ToolRecords.Count == 0)
        {
            builder.AppendLine("本任务未触发工具调用。\n");
        }
        else
        {
            for (var index = 0; index < result.ToolRecords.Count; index++)
            {
                var record = result.ToolRecords[index];
                builder.AppendLine($"### 2.{index + 1} `{record.Name}`");
                builder.AppendLine();
                builder.AppendLine("参数：");
                builder.AppendLine("```json");
                builder.AppendLine(JsonSerializer.Serialize(record.Arguments, new JsonSerializerOptions { WriteIndented = true }));
                builder.AppendLine("```");
                builder.AppendLine();
                builder.AppendLine($"结果: {(record.Ok ? "成功" : "失败")}");
                builder.AppendLine();
                builder.AppendLine("输出：");
                builder.AppendLine("```text");
                builder.AppendLine(record.Output);
                builder.AppendLine("```");
                builder.AppendLine();
            }
        }

        builder.AppendLine("## 3. 最终输出");
        builder.AppendLine();
        if (string.IsNullOrWhiteSpace(result.Content))
        {
            builder.AppendLine("本任务没有生成最终正文输出。\n");
        }
        else
        {
            builder.AppendLine("```text");
            builder.AppendLine(result.Content);
            builder.AppendLine("```\n");
        }

        if (!string.IsNullOrWhiteSpace(result.Error))
        {
            builder.AppendLine("## 4. 错误信息\n");
            builder.AppendLine("```text");
            builder.AppendLine(result.Error);
            builder.AppendLine("```\n");
        }

        builder.AppendLine("## 5. 总结\n");
        builder.AppendLine(result.Success
            ? $"任务已完成，共执行 {result.ToolRecords.Count} 次工具调用。"
            : $"任务执行失败，但未中断后续批量任务。失败原因：{result.Error}");
        File.WriteAllText(path, builder.ToString());
        return path;
    }

    private static string Slugify(string text)
    {
        var cleaned = Regex.Replace(text.Trim(), "[^0-9A-Za-z\u4e00-\u9fff._-]+", "-");
        cleaned = Regex.Replace(cleaned, "-+", "-").Trim('-');
        return string.IsNullOrWhiteSpace(cleaned) ? "task" : cleaned[..Math.Min(60, cleaned.Length)];
    }
}