using System.Text.Json;
using LuminChatWin.Core.Models;

namespace LuminChatWin.Core.Services;

public sealed class SessionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _rootDir;

    public SessionStore(string rootDir)
    {
        _rootDir = ConfigService.ExpandPath(rootDir);
        Directory.CreateDirectory(_rootDir);
    }

    public SessionState Create(int modelLevel, string approvalPolicy, string cwd, string systemPrompt)
    {
        var session = new SessionState
        {
            SessionId = Guid.NewGuid().ToString("N"),
            CreatedAt = DateTime.UtcNow.ToString("O"),
            ModelLevel = modelLevel,
            ApprovalPolicy = approvalPolicy,
            Cwd = cwd,
            Messages =
            [
                new PersistedChatMessage { Role = "system", Content = systemPrompt },
            ],
        };
        Save(session);
        return session;
    }

    public string Save(SessionState session)
    {
        var path = GetPath(session.SessionId);
        File.WriteAllText(path, JsonSerializer.Serialize(session, JsonOptions));
        return path;
    }

    public SessionState Load(string sessionIdOrPath)
    {
        var path = File.Exists(sessionIdOrPath) ? sessionIdOrPath : GetPath(sessionIdOrPath);
        return JsonSerializer.Deserialize<SessionState>(File.ReadAllText(path))
            ?? throw new InvalidOperationException("会话文件解析失败。");
    }

    public IReadOnlyList<Dictionary<string, string>> ListSessions(int limit = 20)
    {
        var files = Directory.EnumerateFiles(_rootDir, "*.json")
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .Take(Math.Clamp(limit, 1, 100));

        var result = new List<Dictionary<string, string>>();
        foreach (var file in files)
        {
            try
            {
                var session = Load(file.FullName);
                var preview = session.Messages.FirstOrDefault(message => message.Role == "user")?.Content ?? "<empty>";
                result.Add(new Dictionary<string, string>
                {
                    ["session_id"] = session.SessionId,
                    ["created_at"] = session.CreatedAt,
                    ["cwd"] = session.Cwd,
                    ["path"] = file.FullName,
                    ["preview"] = preview.Replace(Environment.NewLine, " ")[..Math.Min(80, preview.Length)],
                });
            }
            catch
            {
            }
        }

        return result;
    }

    public string GetPath(string sessionId) => Path.Combine(_rootDir, $"{sessionId}.json");
}