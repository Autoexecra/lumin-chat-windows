using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using LuminChatWin.Core.Models;

namespace LuminChatWin.Core.Services;

public sealed class MemoryStore
{
    private static readonly Regex TokenPattern = new("[A-Za-z0-9_./:-]{2,}|[\u4e00-\u9fff]{2,}", RegexOptions.Compiled);
    private static readonly string[] NoteKeywords =
    [
        "记住", "偏好", "习惯", "默认", "总是", "不要", "必须", "优先", "项目", "开发板", "部署", "文档库", "rpm", "中文", "黑名单", "白名单", "模型", "端口", "主机", "密码", "用户名", "路径", "lumin-chat",
    ];

    private readonly string _connectionString;

    public MemoryStore(string rootDir)
    {
        var expanded = ConfigService.ExpandPath(rootDir);
        Directory.CreateDirectory(expanded);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(expanded, "memory.db") }.ToString();
        Initialize();
    }

    public void EnsureSession(string sessionId, string? createdAt = null)
    {
        var now = createdAt ?? DateTime.UtcNow.ToString("O");
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = @"
INSERT INTO sessions(session_id, created_at, updated_at)
VALUES($id, $created, $updated)
ON CONFLICT(session_id) DO UPDATE SET updated_at = $updated;";
        command.Parameters.AddWithValue("$id", sessionId);
        command.Parameters.AddWithValue("$created", now);
        command.Parameters.AddWithValue("$updated", now);
        command.ExecuteNonQuery();
    }

    public void RecordTurn(string sessionId, string userInput, string assistantOutput)
    {
        if (string.IsNullOrWhiteSpace(userInput) && string.IsNullOrWhiteSpace(assistantOutput))
        {
            return;
        }

        EnsureSession(sessionId);
        var createdAt = DateTime.UtcNow.ToString("O");
        var title = BuildTitle(userInput);
        var summary = BuildSummary(userInput, assistantOutput);
        var content = $"用户: {userInput}\n\n助手: {assistantOutput}";
        var keywords = string.Join(' ', Tokenize($"{userInput}\n{assistantOutput}").Take(24));
        var importance = EstimateImportance(userInput);

        using var connection = Open();
        using var transaction = connection.BeginTransaction();

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = @"
INSERT INTO memory_items(session_id, created_at, title, summary, content, keywords, importance)
VALUES($sessionId, $createdAt, $title, $summary, $content, $keywords, $importance);";
            command.Parameters.AddWithValue("$sessionId", sessionId);
            command.Parameters.AddWithValue("$createdAt", createdAt);
            command.Parameters.AddWithValue("$title", title);
            command.Parameters.AddWithValue("$summary", summary);
            command.Parameters.AddWithValue("$content", content);
            command.Parameters.AddWithValue("$keywords", keywords);
            command.Parameters.AddWithValue("$importance", importance);
            command.ExecuteNonQuery();
        }

        foreach (var note in ExtractNotes(userInput))
        {
            using var noteCommand = connection.CreateCommand();
            noteCommand.Transaction = transaction;
            noteCommand.CommandText = @"
INSERT OR IGNORE INTO session_notes(session_id, note, created_at)
VALUES($sessionId, $note, $createdAt);";
            noteCommand.Parameters.AddWithValue("$sessionId", sessionId);
            noteCommand.Parameters.AddWithValue("$note", note);
            noteCommand.Parameters.AddWithValue("$createdAt", createdAt);
            noteCommand.ExecuteNonQuery();
        }

        using (var updateCommand = connection.CreateCommand())
        {
            updateCommand.Transaction = transaction;
            updateCommand.CommandText = "UPDATE sessions SET updated_at = $updated WHERE session_id = $id;";
            updateCommand.Parameters.AddWithValue("$updated", createdAt);
            updateCommand.Parameters.AddWithValue("$id", sessionId);
            updateCommand.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public IReadOnlyList<MemoryItem> Query(string sessionId, string queryText, int limit = 5)
    {
        var tokens = Tokenize(queryText).ToHashSet(StringComparer.OrdinalIgnoreCase);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT id, title, summary, created_at, content, keywords, importance
FROM memory_items
WHERE session_id = $sessionId
ORDER BY importance DESC, created_at DESC;";
        command.Parameters.AddWithValue("$sessionId", sessionId);

        var results = new List<MemoryItem>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var joined = string.Join('\n', reader.GetString(1), reader.GetString(2), reader.GetString(4), reader.GetString(5));
            var score = Tokenize(joined).Count(token => tokens.Contains(token)) + reader.GetInt32(6) * 0.3;
            if (score <= 0 && tokens.Count > 0)
            {
                continue;
            }

            results.Add(new MemoryItem
            {
                MemoryId = reader.GetInt64(0),
                Title = reader.GetString(1),
                Summary = reader.GetString(2),
                CreatedAt = reader.GetString(3),
                Score = score,
            });
        }

        return results.OrderByDescending(item => item.Score).ThenByDescending(item => item.CreatedAt).Take(Math.Clamp(limit, 1, 10)).ToList();
    }

    public IReadOnlyList<string> GetNotes(string sessionId, int limit = 8)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT note FROM session_notes WHERE session_id = $sessionId ORDER BY created_at DESC LIMIT $limit;";
        command.Parameters.AddWithValue("$sessionId", sessionId);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 50));

        var notes = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            notes.Add(reader.GetString(0));
        }

        return notes;
    }

    public string BuildContext(string sessionId, string queryText, int limit = 5, int maxChars = 1600)
    {
        var notes = GetRelevantNotes(sessionId, queryText, 6);
        var memories = Query(sessionId, queryText, limit);
        if (notes.Count == 0 && memories.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder("以下是当前会话的长期记忆，仅在与当前问题相关时参考：");
        if (notes.Count > 0)
        {
            builder.AppendLine().AppendLine("[稳定偏好/事实]");
            foreach (var note in notes)
            {
                builder.AppendLine($"- {note}");
            }
        }

        if (memories.Count > 0)
        {
            builder.AppendLine("[相关历史片段]");
            for (var index = 0; index < memories.Count; index++)
            {
                builder.AppendLine($"{index + 1}. {memories[index].Title}");
                builder.AppendLine($"   摘要: {memories[index].Summary}");
            }
        }

        var text = builder.ToString();
        return text.Length <= maxChars ? text : text[..maxChars] + "\n...<长期记忆已截断>...";
    }

    public Dictionary<string, object> Describe(string sessionId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT
    (SELECT COUNT(*) FROM memory_items WHERE session_id = $id),
    (SELECT COUNT(*) FROM session_notes WHERE session_id = $id);";
        command.Parameters.AddWithValue("$id", sessionId);
        using var reader = command.ExecuteReader();
        reader.Read();
        return new Dictionary<string, object>
        {
            ["session_id"] = sessionId,
            ["memory_count"] = reader.GetInt32(0),
            ["note_count"] = reader.GetInt32(1),
        };
    }

    private IReadOnlyList<string> GetRelevantNotes(string sessionId, string queryText, int limit)
    {
        var queryTokens = Tokenize(queryText).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return GetNotes(sessionId, 50)
            .Select(note => new
            {
                Note = note,
                Score = Tokenize(note).Count(token => queryTokens.Contains(token)) + (note.Contains("中文", StringComparison.Ordinal) ? 0.5 : 0),
            })
            .Where(item => item.Score > 0)
            .OrderByDescending(item => item.Score)
            .Take(limit)
            .Select(item => item.Note)
            .ToList();
    }

    private void Initialize()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = @"
CREATE TABLE IF NOT EXISTS sessions(
    session_id TEXT PRIMARY KEY,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS memory_items(
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    session_id TEXT NOT NULL,
    created_at TEXT NOT NULL,
    title TEXT NOT NULL,
    summary TEXT NOT NULL,
    content TEXT NOT NULL,
    keywords TEXT NOT NULL,
    importance INTEGER NOT NULL DEFAULT 1
);
CREATE TABLE IF NOT EXISTS session_notes(
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    session_id TEXT NOT NULL,
    note TEXT NOT NULL,
    created_at TEXT NOT NULL,
    UNIQUE(session_id, note)
);";
        command.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private static IReadOnlyList<string> Tokenize(string input) => TokenPattern.Matches(input).Select(match => match.Value.ToLowerInvariant()).ToList();

    private static string BuildTitle(string text)
    {
        var trimmed = (text ?? string.Empty).Trim();
        if (trimmed.Length <= 30)
        {
            return trimmed;
        }

        return trimmed[..30] + "...";
    }

    private static string BuildSummary(string userInput, string assistantOutput)
    {
        var summary = $"用户请求: {userInput} 助手结论: {assistantOutput}".Replace(Environment.NewLine, " ").Trim();
        return summary.Length <= 120 ? summary : summary[..120] + "...";
    }

    private static int EstimateImportance(string userInput)
    {
        var score = NoteKeywords.Count(keyword => userInput.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        return Math.Clamp(score == 0 ? 1 : 1 + score, 1, 5);
    }

    private static IReadOnlyList<string> ExtractNotes(string userInput)
    {
        return userInput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => NoteKeywords.Any(keyword => line.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();
    }
}