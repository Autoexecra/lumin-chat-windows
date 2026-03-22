using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LuminChatWin.Core.Models;

namespace LuminChatWin.Core.Services;

public sealed class ToolExecutor
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly Regex MutatingCommandPattern = new(@"\b(remove-item|del|rd|move-item|rename-item|copy-item|new-item|set-content|add-content|out-file|git\s+commit|git\s+push|dotnet\s+publish|msbuild|npm\s+install|pip\s+install)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private readonly AppConfig _config;
    private readonly SshService _sshService = new();
    private readonly WebToolClient _webToolClient = new();
    private readonly Func<string, string, bool>? _confirmCallback;
    private readonly Dictionary<string, Dictionary<string, object?>> _knowledgeDocumentIndex = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _knowledgeDocumentCache = new(StringComparer.OrdinalIgnoreCase);
    private bool _knowledgeIndexLoaded;

    public ToolExecutor(AppConfig config, string cwd, string approvalPolicy = "auto", Func<string, string, bool>? confirmCallback = null)
    {
        _config = config;
        _confirmCallback = confirmCallback;
        Cwd = ConfigService.ExpandPath(cwd);
        ApprovalPolicy = approvalPolicy;
        Directory.CreateDirectory(Cwd);
    }

    public string Cwd { get; private set; }

    public string ApprovalPolicy { get; private set; }

    public IReadOnlyList<Dictionary<string, object?>> Definitions()
    {
        return
        [
            CreateTool("run_shell_command", "Run a PowerShell command in the current workspace.", new Dictionary<string, object?>
            {
                ["command"] = new Dictionary<string, object?> { ["type"] = "string" },
                ["timeout_seconds"] = new Dictionary<string, object?> { ["type"] = "integer", ["default"] = 30 },
                ["cwd"] = new Dictionary<string, object?> { ["type"] = "string" },
            }, ["command"]),
            CreateTool("change_directory", "Change current working directory.", new Dictionary<string, object?>
            {
                ["path"] = new Dictionary<string, object?> { ["type"] = "string" },
            }, ["path"]),
            CreateTool("list_directory", "List files and directories.", new Dictionary<string, object?>
            {
                ["path"] = new Dictionary<string, object?> { ["type"] = "string" },
                ["recursive"] = new Dictionary<string, object?> { ["type"] = "boolean", ["default"] = false },
                ["max_entries"] = new Dictionary<string, object?> { ["type"] = "integer", ["default"] = 200 },
            }, []),
            CreateTool("search_text", "Search text in files.", new Dictionary<string, object?>
            {
                ["pattern"] = new Dictionary<string, object?> { ["type"] = "string" },
                ["path"] = new Dictionary<string, object?> { ["type"] = "string" },
                ["glob"] = new Dictionary<string, object?> { ["type"] = "string", ["default"] = "**/*" },
                ["case_sensitive"] = new Dictionary<string, object?> { ["type"] = "boolean", ["default"] = false },
                ["max_matches"] = new Dictionary<string, object?> { ["type"] = "integer", ["default"] = 50 },
            }, ["pattern"]),
            CreateTool("find_files", "Find files by glob pattern.", new Dictionary<string, object?>
            {
                ["pattern"] = new Dictionary<string, object?> { ["type"] = "string", ["default"] = "**/*" },
                ["path"] = new Dictionary<string, object?> { ["type"] = "string" },
                ["max_results"] = new Dictionary<string, object?> { ["type"] = "integer", ["default"] = 200 },
                ["include_hidden"] = new Dictionary<string, object?> { ["type"] = "boolean", ["default"] = false },
            }, []),
            CreateTool("read_file", "Read a text file.", new Dictionary<string, object?>
            {
                ["path"] = new Dictionary<string, object?> { ["type"] = "string" },
                ["start_line"] = new Dictionary<string, object?> { ["type"] = "integer", ["default"] = 1 },
                ["end_line"] = new Dictionary<string, object?> { ["type"] = "integer", ["default"] = 200 },
            }, ["path"]),
            CreateTool("write_file", "Write a text file.", new Dictionary<string, object?>
            {
                ["path"] = new Dictionary<string, object?> { ["type"] = "string" },
                ["content"] = new Dictionary<string, object?> { ["type"] = "string" },
                ["append"] = new Dictionary<string, object?> { ["type"] = "boolean", ["default"] = false },
            }, ["path", "content"]),
            CreateTool("replace_in_file", "Replace exact text in a file.", new Dictionary<string, object?>
            {
                ["path"] = new Dictionary<string, object?> { ["type"] = "string" },
                ["search_text"] = new Dictionary<string, object?> { ["type"] = "string" },
                ["replace_text"] = new Dictionary<string, object?> { ["type"] = "string" },
                ["replace_all"] = new Dictionary<string, object?> { ["type"] = "boolean", ["default"] = false },
            }, ["path", "search_text", "replace_text"]),
            CreateTool("insert_in_file", "Insert text at a line number.", new Dictionary<string, object?>
            {
                ["path"] = new Dictionary<string, object?> { ["type"] = "string" },
                ["content"] = new Dictionary<string, object?> { ["type"] = "string" },
                ["line_number"] = new Dictionary<string, object?> { ["type"] = "integer", ["default"] = 1 },
            }, ["path", "content"]),
            CreateTool("get_environment", "Get current environment information.", new Dictionary<string, object?>(), []),
            CreateTool("get_workspace_overview", "Summarize workspace structure and git state.", new Dictionary<string, object?>
            {
                ["path"] = new Dictionary<string, object?> { ["type"] = "string" },
                ["max_depth"] = new Dictionary<string, object?> { ["type"] = "integer", ["default"] = 2 },
                ["max_entries"] = new Dictionary<string, object?> { ["type"] = "integer", ["default"] = 80 },
            }, []),
            CreateTool("git_status", "Get git status.", new Dictionary<string, object?>
            {
                ["repo_path"] = new Dictionary<string, object?> { ["type"] = "string" },
                ["include_untracked"] = new Dictionary<string, object?> { ["type"] = "boolean", ["default"] = true },
            }, []),
            CreateTool("git_diff", "Get git diff.", new Dictionary<string, object?>
            {
                ["repo_path"] = new Dictionary<string, object?> { ["type"] = "string" },
                ["pathspec"] = new Dictionary<string, object?> { ["type"] = "string" },
                ["cached"] = new Dictionary<string, object?> { ["type"] = "boolean", ["default"] = false },
                ["max_chars"] = new Dictionary<string, object?> { ["type"] = "integer", ["default"] = 12000 },
            }, []),
            CreateTool("ssh_execute_command", "Run a command on a remote SSH host.", new Dictionary<string, object?>
            {
                ["host"] = new Dictionary<string, object?> { ["type"] = "string" },
                ["port"] = new Dictionary<string, object?> { ["type"] = "integer", ["default"] = 22 },
                ["username"] = new Dictionary<string, object?> { ["type"] = "string" },
                ["password"] = new Dictionary<string, object?> { ["type"] = "string" },
                ["command"] = new Dictionary<string, object?> { ["type"] = "string" },
                ["timeout_seconds"] = new Dictionary<string, object?> { ["type"] = "integer", ["default"] = 60 },
                ["cwd"] = new Dictionary<string, object?> { ["type"] = "string" },
            }, ["host", "username", "command"]),
            CreateTool("ssh_upload_file", "Upload a local file over SFTP.", new Dictionary<string, object?>
            {
                ["host"] = new Dictionary<string, object?> { ["type"] = "string" },
                ["port"] = new Dictionary<string, object?> { ["type"] = "integer", ["default"] = 22 },
                ["username"] = new Dictionary<string, object?> { ["type"] = "string" },
                ["password"] = new Dictionary<string, object?> { ["type"] = "string" },
                ["local_path"] = new Dictionary<string, object?> { ["type"] = "string" },
                ["remote_path"] = new Dictionary<string, object?> { ["type"] = "string" },
            }, ["host", "username", "local_path", "remote_path"]),
            CreateTool("ssh_download_file", "Download a remote file over SFTP.", new Dictionary<string, object?>
            {
                ["host"] = new Dictionary<string, object?> { ["type"] = "string" },
                ["port"] = new Dictionary<string, object?> { ["type"] = "integer", ["default"] = 22 },
                ["username"] = new Dictionary<string, object?> { ["type"] = "string" },
                ["password"] = new Dictionary<string, object?> { ["type"] = "string" },
                ["remote_path"] = new Dictionary<string, object?> { ["type"] = "string" },
                ["local_path"] = new Dictionary<string, object?> { ["type"] = "string" },
            }, ["host", "username", "remote_path", "local_path"]),
            CreateTool("ssh_list_directory", "List a remote directory.", new Dictionary<string, object?>
            {
                ["host"] = new Dictionary<string, object?> { ["type"] = "string" },
                ["port"] = new Dictionary<string, object?> { ["type"] = "integer", ["default"] = 22 },
                ["username"] = new Dictionary<string, object?> { ["type"] = "string" },
                ["password"] = new Dictionary<string, object?> { ["type"] = "string" },
                ["path"] = new Dictionary<string, object?> { ["type"] = "string" },
                ["recursive"] = new Dictionary<string, object?> { ["type"] = "boolean", ["default"] = false },
                ["max_entries"] = new Dictionary<string, object?> { ["type"] = "integer", ["default"] = 200 },
            }, ["host", "username", "path"]),
            CreateTool("ssh_read_file", "Read a remote file.", new Dictionary<string, object?>
            {
                ["host"] = new Dictionary<string, object?> { ["type"] = "string" },
                ["port"] = new Dictionary<string, object?> { ["type"] = "integer", ["default"] = 22 },
                ["username"] = new Dictionary<string, object?> { ["type"] = "string" },
                ["password"] = new Dictionary<string, object?> { ["type"] = "string" },
                ["path"] = new Dictionary<string, object?> { ["type"] = "string" },
                ["start_line"] = new Dictionary<string, object?> { ["type"] = "integer", ["default"] = 1 },
                ["end_line"] = new Dictionary<string, object?> { ["type"] = "integer", ["default"] = 200 },
            }, ["host", "username", "path"]),
            CreateTool("ssh_write_file", "Write a remote file.", new Dictionary<string, object?>
            {
                ["host"] = new Dictionary<string, object?> { ["type"] = "string" },
                ["port"] = new Dictionary<string, object?> { ["type"] = "integer", ["default"] = 22 },
                ["username"] = new Dictionary<string, object?> { ["type"] = "string" },
                ["password"] = new Dictionary<string, object?> { ["type"] = "string" },
                ["path"] = new Dictionary<string, object?> { ["type"] = "string" },
                ["content"] = new Dictionary<string, object?> { ["type"] = "string" },
                ["append"] = new Dictionary<string, object?> { ["type"] = "boolean", ["default"] = false },
            }, ["host", "username", "path", "content"]),
            CreateTool("ssh_make_directory", "Create a remote directory.", new Dictionary<string, object?>
            {
                ["host"] = new Dictionary<string, object?> { ["type"] = "string" },
                ["port"] = new Dictionary<string, object?> { ["type"] = "integer", ["default"] = 22 },
                ["username"] = new Dictionary<string, object?> { ["type"] = "string" },
                ["password"] = new Dictionary<string, object?> { ["type"] = "string" },
                ["path"] = new Dictionary<string, object?> { ["type"] = "string" },
            }, ["host", "username", "path"]),
            CreateTool("ssh_remove_path", "Remove a remote file or directory.", new Dictionary<string, object?>
            {
                ["host"] = new Dictionary<string, object?> { ["type"] = "string" },
                ["port"] = new Dictionary<string, object?> { ["type"] = "integer", ["default"] = 22 },
                ["username"] = new Dictionary<string, object?> { ["type"] = "string" },
                ["password"] = new Dictionary<string, object?> { ["type"] = "string" },
                ["path"] = new Dictionary<string, object?> { ["type"] = "string" },
            }, ["host", "username", "path"]),
            CreateTool("ssh_path_exists", "Check whether a remote path exists.", new Dictionary<string, object?>
            {
                ["host"] = new Dictionary<string, object?> { ["type"] = "string" },
                ["port"] = new Dictionary<string, object?> { ["type"] = "integer", ["default"] = 22 },
                ["username"] = new Dictionary<string, object?> { ["type"] = "string" },
                ["password"] = new Dictionary<string, object?> { ["type"] = "string" },
                ["path"] = new Dictionary<string, object?> { ["type"] = "string" },
            }, ["host", "username", "path"]),
            CreateTool("fetch_web_page", "Fetch and summarize a web page.", new Dictionary<string, object?>
            {
                ["url"] = new Dictionary<string, object?> { ["type"] = "string" },
                ["max_chars"] = new Dictionary<string, object?> { ["type"] = "integer", ["default"] = 12000 },
            }, ["url"]),
            CreateTool("search_web", "Search the public web.", new Dictionary<string, object?>
            {
                ["query"] = new Dictionary<string, object?> { ["type"] = "string" },
                ["limit"] = new Dictionary<string, object?> { ["type"] = "integer", ["default"] = 5 },
            }, ["query"]),
            CreateTool("list_knowledge_documents", "List configured repository documents.", new Dictionary<string, object?>
            {
                ["keyword"] = new Dictionary<string, object?> { ["type"] = "string" },
                ["limit"] = new Dictionary<string, object?> { ["type"] = "integer", ["default"] = 50 },
            }, []),
            CreateTool("read_knowledge_document", "Read a repository document.", new Dictionary<string, object?>
            {
                ["path"] = new Dictionary<string, object?> { ["type"] = "string" },
                ["start_line"] = new Dictionary<string, object?> { ["type"] = "integer", ["default"] = 1 },
                ["end_line"] = new Dictionary<string, object?> { ["type"] = "integer", ["default"] = 200 },
            }, ["path"]),
            CreateTool("write_knowledge_document", "Write a repository document.", new Dictionary<string, object?>
            {
                ["path"] = new Dictionary<string, object?> { ["type"] = "string" },
                ["content"] = new Dictionary<string, object?> { ["type"] = "string" },
                ["append"] = new Dictionary<string, object?> { ["type"] = "boolean", ["default"] = false },
            }, ["path", "content"]),
        ];
    }

    public void SetApprovalPolicy(string approvalPolicy)
    {
        ApprovalPolicy = approvalPolicy;
    }

    public void SetCommandPolicyMode(string mode)
    {
        _config.CommandPolicy.Mode = mode;
    }

    public async Task<ToolExecutionResult> ExecuteAsync(ToolCall toolCall, CancellationToken cancellationToken = default)
    {
        try
        {
            return toolCall.Name switch
            {
                "run_shell_command" => await RunShellCommandAsync(GetRequiredString(toolCall.Arguments, "command"), GetInt(toolCall.Arguments, "timeout_seconds", 30), GetOptionalString(toolCall.Arguments, "cwd"), cancellationToken).ConfigureAwait(false),
                "change_directory" => ChangeDirectory(GetRequiredString(toolCall.Arguments, "path")),
                "list_directory" => ListDirectory(GetOptionalString(toolCall.Arguments, "path"), GetBool(toolCall.Arguments, "recursive"), GetInt(toolCall.Arguments, "max_entries", 200)),
                "search_text" => SearchText(GetRequiredString(toolCall.Arguments, "pattern"), GetOptionalString(toolCall.Arguments, "path"), GetOptionalString(toolCall.Arguments, "glob") ?? "**/*", GetBool(toolCall.Arguments, "case_sensitive"), GetInt(toolCall.Arguments, "max_matches", 50)),
                "find_files" => FindFiles(GetOptionalString(toolCall.Arguments, "pattern") ?? "**/*", GetOptionalString(toolCall.Arguments, "path"), GetInt(toolCall.Arguments, "max_results", 200), GetBool(toolCall.Arguments, "include_hidden")),
                "read_file" => ReadFile(GetRequiredString(toolCall.Arguments, "path"), GetInt(toolCall.Arguments, "start_line", 1), GetInt(toolCall.Arguments, "end_line", 200)),
                "write_file" => WriteFile(GetRequiredString(toolCall.Arguments, "path"), GetRequiredString(toolCall.Arguments, "content"), GetBool(toolCall.Arguments, "append")),
                "replace_in_file" => ReplaceInFile(GetRequiredString(toolCall.Arguments, "path"), GetRequiredString(toolCall.Arguments, "search_text"), GetRequiredString(toolCall.Arguments, "replace_text"), GetBool(toolCall.Arguments, "replace_all")),
                "insert_in_file" => InsertInFile(GetRequiredString(toolCall.Arguments, "path"), GetRequiredString(toolCall.Arguments, "content"), GetInt(toolCall.Arguments, "line_number", 1)),
                "get_environment" => GetEnvironment(),
                "get_workspace_overview" => GetWorkspaceOverview(GetOptionalString(toolCall.Arguments, "path"), GetInt(toolCall.Arguments, "max_depth", 2), GetInt(toolCall.Arguments, "max_entries", 80)),
                "git_status" => await GitStatusAsync(GetOptionalString(toolCall.Arguments, "repo_path"), GetBool(toolCall.Arguments, "include_untracked", true), cancellationToken).ConfigureAwait(false),
                "git_diff" => await GitDiffAsync(GetOptionalString(toolCall.Arguments, "repo_path"), GetOptionalString(toolCall.Arguments, "pathspec"), GetBool(toolCall.Arguments, "cached"), GetInt(toolCall.Arguments, "max_chars", 12000), cancellationToken).ConfigureAwait(false),
                "ssh_execute_command" => SshExecuteCommand(toolCall.Arguments),
                "ssh_upload_file" => SshUploadFile(toolCall.Arguments),
                "ssh_download_file" => SshDownloadFile(toolCall.Arguments),
                "ssh_list_directory" => SshListDirectory(toolCall.Arguments),
                "ssh_read_file" => SshReadFile(toolCall.Arguments),
                "ssh_write_file" => SshWriteFile(toolCall.Arguments),
                "ssh_make_directory" => SshMakeDirectory(toolCall.Arguments),
                "ssh_remove_path" => SshRemovePath(toolCall.Arguments),
                "ssh_path_exists" => SshPathExists(toolCall.Arguments),
                "fetch_web_page" => await FetchWebPageAsync(GetRequiredString(toolCall.Arguments, "url"), GetInt(toolCall.Arguments, "max_chars", 12000), cancellationToken).ConfigureAwait(false),
                "search_web" => await SearchWebAsync(GetRequiredString(toolCall.Arguments, "query"), GetInt(toolCall.Arguments, "limit", 5), cancellationToken).ConfigureAwait(false),
                "list_knowledge_documents" => ListKnowledgeDocuments(GetOptionalString(toolCall.Arguments, "keyword"), GetInt(toolCall.Arguments, "limit", 50)),
                "read_knowledge_document" => ReadKnowledgeDocument(GetRequiredString(toolCall.Arguments, "path"), GetInt(toolCall.Arguments, "start_line", 1), GetInt(toolCall.Arguments, "end_line", 200)),
                "write_knowledge_document" => WriteKnowledgeDocument(GetRequiredString(toolCall.Arguments, "path"), GetRequiredString(toolCall.Arguments, "content"), GetBool(toolCall.Arguments, "append")),
                _ => new ToolExecutionResult(toolCall.Name, false, $"Unknown tool: {toolCall.Name}"),
            };
        }
        catch (Exception ex)
        {
            return new ToolExecutionResult(toolCall.Name, false, $"工具执行失败: {ex.Message}");
        }
    }

    public string BuildWorkspaceContext(int maxDepth = 2, int maxEntries = 40)
    {
        return GetWorkspaceOverview(null, maxDepth, maxEntries).Output;
    }

    public ToolExecutionResult ChangeDirectory(string path)
    {
        var resolved = ResolvePath(path);
        if (!Directory.Exists(resolved))
        {
            return new ToolExecutionResult("change_directory", false, $"目录不存在: {resolved}");
        }

        Cwd = resolved;
        return new ToolExecutionResult("change_directory", true, Json(new Dictionary<string, object?>
        {
            ["cwd"] = Cwd,
        }));
    }

    public ToolExecutionResult ListDirectory(string? path = null, bool recursive = false, int maxEntries = 200)
    {
        var root = ResolvePath(path);
        if (!Directory.Exists(root))
        {
            return new ToolExecutionResult("list_directory", false, $"目录不存在: {root}");
        }

        var entries = Directory.EnumerateFileSystemEntries(root, "*", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
            .Take(Math.Clamp(maxEntries, 1, 1000))
            .Select(entry => new Dictionary<string, object?>
            {
                ["path"] = entry,
                ["relative_path"] = Path.GetRelativePath(root, entry),
                ["is_dir"] = Directory.Exists(entry),
                ["size"] = Directory.Exists(entry) ? 0 : new FileInfo(entry).Length,
            })
            .ToList();
        return new ToolExecutionResult("list_directory", true, Json(entries));
    }

    public ToolExecutionResult SearchText(string pattern, string? path = null, string glob = "**/*", bool caseSensitive = false, int maxMatches = 50)
    {
        var root = ResolveExistingBase(path);
        var matches = new List<Dictionary<string, object?>>();
        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        foreach (var file in EnumerateMatchingFiles(root, glob, false))
        {
            var lineNumber = 0;
            foreach (var line in File.ReadLines(file))
            {
                lineNumber++;
                if (!line.Contains(pattern, comparison))
                {
                    continue;
                }

                matches.Add(new Dictionary<string, object?>
                {
                    ["path"] = file,
                    ["relative_path"] = Path.GetRelativePath(root, file),
                    ["line_number"] = lineNumber,
                    ["line"] = line,
                });
                if (matches.Count >= Math.Clamp(maxMatches, 1, 500))
                {
                    return new ToolExecutionResult("search_text", true, Json(matches));
                }
            }
        }

        return new ToolExecutionResult("search_text", true, Json(matches));
    }

    public ToolExecutionResult FindFiles(string pattern = "**/*", string? path = null, int maxResults = 200, bool includeHidden = false)
    {
        var root = ResolveExistingBase(path);
        var files = EnumerateMatchingFiles(root, pattern, includeHidden)
            .Take(Math.Clamp(maxResults, 1, 1000))
            .Select(file => new Dictionary<string, object?>
            {
                ["path"] = file,
                ["relative_path"] = Path.GetRelativePath(root, file),
            })
            .ToList();
        return new ToolExecutionResult("find_files", true, Json(files));
    }

    public ToolExecutionResult ReadFile(string path, int startLine = 1, int endLine = 200)
    {
        var resolved = ResolvePath(path);
        if (!File.Exists(resolved))
        {
            return new ToolExecutionResult("read_file", false, $"文件不存在: {resolved}");
        }

        var lines = File.ReadLines(resolved)
            .Skip(Math.Max(0, startLine - 1))
            .Take(Math.Max(1, endLine - startLine + 1))
            .Select((line, index) => $"{startLine + index}: {line}");
        return new ToolExecutionResult("read_file", true, string.Join(Environment.NewLine, lines));
    }

    public ToolExecutionResult WriteFile(string path, string content, bool append = false)
    {
        var resolved = ResolvePath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(resolved)!);
        if (append)
        {
            File.AppendAllText(resolved, content);
        }
        else
        {
            File.WriteAllText(resolved, content);
        }

        return new ToolExecutionResult("write_file", true, Json(new Dictionary<string, object?>
        {
            ["path"] = resolved,
            ["append"] = append,
            ["bytes"] = Encoding.UTF8.GetByteCount(content),
        }));
    }

    public ToolExecutionResult ReplaceInFile(string path, string searchText, string replaceText, bool replaceAll = false)
    {
        var resolved = ResolvePath(path);
        var content = File.ReadAllText(resolved);
        var occurrences = CountOccurrences(content, searchText);
        if (occurrences == 0)
        {
            return new ToolExecutionResult("replace_in_file", false, "未找到要替换的文本。");
        }

        if (!replaceAll && occurrences != 1)
        {
            return new ToolExecutionResult("replace_in_file", false, $"匹配到 {occurrences} 处文本，未开启 replace_all。");
        }

        var updated = replaceAll
            ? content.Replace(searchText, replaceText, StringComparison.Ordinal)
            : ReplaceOnce(content, searchText, replaceText);
        File.WriteAllText(resolved, updated);
        return new ToolExecutionResult("replace_in_file", true, Json(new Dictionary<string, object?>
        {
            ["path"] = resolved,
            ["occurrences"] = occurrences,
        }));
    }

    public ToolExecutionResult InsertInFile(string path, string content, int lineNumber = 1)
    {
        var resolved = ResolvePath(path);
        var lines = File.Exists(resolved) ? File.ReadAllLines(resolved).ToList() : [];
        var index = Math.Clamp(lineNumber - 1, 0, lines.Count);
        lines.Insert(index, content);
        Directory.CreateDirectory(Path.GetDirectoryName(resolved)!);
        File.WriteAllLines(resolved, lines);
        return new ToolExecutionResult("insert_in_file", true, Json(new Dictionary<string, object?>
        {
            ["path"] = resolved,
            ["line_number"] = lineNumber,
        }));
    }

    public ToolExecutionResult GetEnvironment()
    {
        var payload = new Dictionary<string, object?>
        {
            ["os_description"] = RuntimeInformation.OSDescription,
            ["framework"] = RuntimeInformation.FrameworkDescription,
            ["process_architecture"] = RuntimeInformation.ProcessArchitecture.ToString(),
            ["cwd"] = Cwd,
            ["machine_name"] = Environment.MachineName,
            ["user_name"] = Environment.UserName,
        };
        return new ToolExecutionResult("get_environment", true, Json(payload));
    }

    public ToolExecutionResult GetWorkspaceOverview(string? path = null, int maxDepth = 2, int maxEntries = 80)
    {
        var root = ResolveExistingBase(path);
        var builder = new StringBuilder();
        builder.AppendLine($"工作区根目录: {root}")
            .AppendLine($"当前工作目录: {Cwd}")
            .AppendLine("目录摘要:");
        AppendTree(builder, root, 0, Math.Clamp(maxDepth, 1, 6), ref maxEntries);
        var extensions = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Take(500)
            .Select(Path.GetExtension)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .GroupBy(static value => value, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .Take(10)
            .Select(group => $"{group.Key}:{group.Count()}");
        builder.AppendLine().AppendLine("主要文件类型: " + string.Join(", ", extensions));

        var git = GitStatusAsync(root, true, CancellationToken.None).GetAwaiter().GetResult();
        if (git.Ok)
        {
            builder.AppendLine().AppendLine("Git 状态:").AppendLine(git.Output);
        }

        return new ToolExecutionResult("get_workspace_overview", true, builder.ToString().Trim());
    }

    public async Task<ToolExecutionResult> GitStatusAsync(string? repoPath = null, bool includeUntracked = true, CancellationToken cancellationToken = default)
    {
        var root = ResolveExistingBase(repoPath);
        var args = includeUntracked ? "status --short --branch" : "status --short --branch --untracked-files=no";
        return await RunGitAsync(args, root, "git_status", cancellationToken).ConfigureAwait(false);
    }

    public async Task<ToolExecutionResult> GitDiffAsync(string? repoPath = null, string? pathspec = null, bool cached = false, int maxChars = 12000, CancellationToken cancellationToken = default)
    {
        var root = ResolveExistingBase(repoPath);
        var args = new StringBuilder("diff --no-ext-diff --minimal");
        if (cached)
        {
            args.Append(" --cached");
        }

        if (!string.IsNullOrWhiteSpace(pathspec))
        {
            args.Append(" -- ").Append(pathspec);
        }

        var result = await RunGitAsync(args.ToString(), root, "git_diff", cancellationToken).ConfigureAwait(false);
        if (result.Output.Length > maxChars)
        {
            return result with { Output = result.Output[..maxChars] + Environment.NewLine + "...<diff truncated>..." };
        }

        return result;
    }

    public async Task<ToolExecutionResult> RunShellCommandAsync(string command, int timeoutSeconds = 30, string? cwd = null, CancellationToken cancellationToken = default)
    {
        var allowed = CheckCommandPolicy(command);
        if (!allowed.Ok)
        {
            return new ToolExecutionResult("run_shell_command", false, allowed.Message);
        }

        var approval = CheckApproval("run_shell_command", command);
        if (!approval.Ok)
        {
            return new ToolExecutionResult("run_shell_command", false, approval.Message);
        }

        var execCwd = ResolveExistingBase(cwd);
        var psi = new ProcessStartInfo
        {
            FileName = "pwsh",
            Arguments = $"-NoProfile -Command \"{command.Replace("\"", "\\\"", StringComparison.Ordinal)}\"",
            WorkingDirectory = execCwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = new Process { StartInfo = psi };
        process.Start();
        await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(timeoutSeconds), cancellationToken).ConfigureAwait(false);
        var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        var stderr = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        return new ToolExecutionResult("run_shell_command", process.ExitCode == 0, Json(new Dictionary<string, object?>
        {
            ["command"] = command,
            ["cwd"] = execCwd,
            ["exit_code"] = process.ExitCode,
            ["stdout"] = stdout,
            ["stderr"] = stderr,
        }));
    }

    private async Task<ToolExecutionResult> FetchWebPageAsync(string url, int maxChars, CancellationToken cancellationToken)
    {
        var payload = await _webToolClient.FetchPageAsync(url, maxChars, cancellationToken).ConfigureAwait(false);
        return new ToolExecutionResult("fetch_web_page", true, WebToolClient.FormatPayload(payload));
    }

    private async Task<ToolExecutionResult> SearchWebAsync(string query, int limit, CancellationToken cancellationToken)
    {
        var payload = await _webToolClient.SearchAsync(query, limit, cancellationToken).ConfigureAwait(false);
        return new ToolExecutionResult("search_web", true, WebToolClient.FormatPayload(payload));
    }

    private ToolExecutionResult SshExecuteCommand(IReadOnlyDictionary<string, object?> args)
    {
        var settings = CreateSshSettings(args);
        var payload = _sshService.RunCommand(settings, GetRequiredString(args, "command"), GetInt(args, "timeout_seconds", 60), GetOptionalString(args, "cwd"));
        return new ToolExecutionResult("ssh_execute_command", Convert.ToInt32(payload["exit_code"] ?? 1) == 0, Json(payload));
    }

    private ToolExecutionResult SshUploadFile(IReadOnlyDictionary<string, object?> args)
    {
        var settings = CreateSshSettings(args);
        _sshService.UploadFile(settings, ResolvePath(GetRequiredString(args, "local_path")), GetRequiredString(args, "remote_path"));
        return new ToolExecutionResult("ssh_upload_file", true, "上传完成。");
    }

    private ToolExecutionResult SshDownloadFile(IReadOnlyDictionary<string, object?> args)
    {
        var settings = CreateSshSettings(args);
        _sshService.DownloadFile(settings, GetRequiredString(args, "remote_path"), ResolvePath(GetRequiredString(args, "local_path")));
        return new ToolExecutionResult("ssh_download_file", true, "下载完成。");
    }

    private ToolExecutionResult SshListDirectory(IReadOnlyDictionary<string, object?> args)
    {
        var settings = CreateSshSettings(args);
        var payload = _sshService.ListDirectory(settings, GetRequiredString(args, "path"), GetBool(args, "recursive"), GetInt(args, "max_entries", 200));
        return new ToolExecutionResult("ssh_list_directory", true, Json(payload));
    }

    private ToolExecutionResult SshReadFile(IReadOnlyDictionary<string, object?> args)
    {
        var settings = CreateSshSettings(args);
        var text = _sshService.ReadFile(settings, GetRequiredString(args, "path"), GetInt(args, "start_line", 1), GetInt(args, "end_line", 200));
        return new ToolExecutionResult("ssh_read_file", true, text);
    }

    private ToolExecutionResult SshWriteFile(IReadOnlyDictionary<string, object?> args)
    {
        var settings = CreateSshSettings(args);
        _sshService.WriteFile(settings, GetRequiredString(args, "path"), GetRequiredString(args, "content"), GetBool(args, "append"));
        return new ToolExecutionResult("ssh_write_file", true, "写入完成。");
    }

    private ToolExecutionResult SshMakeDirectory(IReadOnlyDictionary<string, object?> args)
    {
        var settings = CreateSshSettings(args);
        _sshService.MakeDirectory(settings, GetRequiredString(args, "path"));
        return new ToolExecutionResult("ssh_make_directory", true, "目录创建完成。");
    }

    private ToolExecutionResult SshRemovePath(IReadOnlyDictionary<string, object?> args)
    {
        var settings = CreateSshSettings(args);
        _sshService.RemovePath(settings, GetRequiredString(args, "path"));
        return new ToolExecutionResult("ssh_remove_path", true, "删除完成。");
    }

    private ToolExecutionResult SshPathExists(IReadOnlyDictionary<string, object?> args)
    {
        var settings = CreateSshSettings(args);
        var exists = _sshService.PathExists(settings, GetRequiredString(args, "path"));
        return new ToolExecutionResult("ssh_path_exists", true, Json(new Dictionary<string, object?> { ["exists"] = exists }));
    }

    private ToolExecutionResult ListKnowledgeDocuments(string? keyword, int limit)
    {
        if (!_config.KnowledgeBase.Enabled)
        {
            return new ToolExecutionResult("list_knowledge_documents", false, "资料库未启用。");
        }

        // Cache one merged local/remote index per executor so repeated planning rounds don't rescan the repository.
        EnsureKnowledgeIndexLoaded();
        var entries = _knowledgeDocumentIndex.Values
            .Where(item => string.IsNullOrWhiteSpace(keyword) || (item["relative_path"]?.ToString() ?? string.Empty).Contains(keyword, StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item["relative_path"]?.ToString(), StringComparer.OrdinalIgnoreCase)
            .Take(Math.Clamp(limit, 1, 200))
            .ToList();
        return new ToolExecutionResult("list_knowledge_documents", true, Json(entries));
    }

    private ToolExecutionResult ReadKnowledgeDocument(string path, int startLine, int endLine)
    {
        if (!_config.KnowledgeBase.Enabled)
        {
            return new ToolExecutionResult("read_knowledge_document", false, "资料库未启用。");
        }

        EnsureKnowledgeIndexLoaded();
        var normalizedPath = NormalizeKnowledgePath(path);
        var fullText = ReadKnowledgeDocumentContent(normalizedPath);
        return new ToolExecutionResult("read_knowledge_document", true, SliceNumberedLines(fullText, startLine, endLine));
    }

    private ToolExecutionResult WriteKnowledgeDocument(string path, string content, bool append)
    {
        if (!_config.KnowledgeBase.Enabled)
        {
            return new ToolExecutionResult("write_knowledge_document", false, "资料库未启用。");
        }

        var settings = CreateKnowledgeBaseSettings();
        var normalizedPath = NormalizeKnowledgePath(path);
        var fullPath = CombineRemote(_config.KnowledgeBase.RootDir, normalizedPath);
        _sshService.WriteFile(settings, fullPath, content, append);
        WriteKnowledgeCacheFile(normalizedPath, append && File.Exists(ResolveKnowledgeCachePath(normalizedPath))
            ? File.ReadAllText(ResolveKnowledgeCachePath(normalizedPath)) + content
            : content);
        _knowledgeDocumentCache[normalizedPath] = append && _knowledgeDocumentCache.TryGetValue(normalizedPath, out var existingContent)
            ? existingContent + content
            : content;
        UpsertKnowledgeEntry(normalizedPath, remoteAvailable: true, localAvailable: true);
        return new ToolExecutionResult("write_knowledge_document", true, "资料库文档写入完成。");
    }

    private void EnsureKnowledgeIndexLoaded()
    {
        if (_knowledgeIndexLoaded)
        {
            return;
        }

        _knowledgeDocumentIndex.Clear();
        LoadLocalKnowledgeEntries();
        LoadRemoteKnowledgeEntries();
        _knowledgeIndexLoaded = true;
    }

    private void LoadLocalKnowledgeEntries()
    {
        var cacheRoot = ResolveKnowledgeCacheRoot();
        Directory.CreateDirectory(cacheRoot);
        foreach (var file in EnumerateMatchingFiles(cacheRoot, "**/*", true))
        {
            var relativePath = Path.GetRelativePath(cacheRoot, file).Replace('\\', '/');
            if (!MatchKnowledgePattern(relativePath, _config.KnowledgeBase.Patterns))
            {
                continue;
            }

            UpsertKnowledgeEntry(relativePath, remoteAvailable: false, localAvailable: true);
        }
    }

    private void LoadRemoteKnowledgeEntries()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_config.KnowledgeBase.Host) || string.IsNullOrWhiteSpace(_config.KnowledgeBase.RootDir))
            {
                return;
            }

            var settings = CreateKnowledgeBaseSettings();
            var entries = _sshService.ListDirectory(settings, _config.KnowledgeBase.RootDir, true, 500)
                .Where(static item => item.TryGetValue("is_dir", out var isDir) && isDir is bool value && !value);

            foreach (var entry in entries)
            {
                var relativePath = NormalizeKnowledgePath(entry["relative_path"]?.ToString() ?? string.Empty);
                if (!MatchKnowledgePattern(relativePath, _config.KnowledgeBase.Patterns))
                {
                    continue;
                }

                UpsertKnowledgeEntry(relativePath, remoteAvailable: true, localAvailable: File.Exists(ResolveKnowledgeCachePath(relativePath)));
            }
        }
        catch
        {
            // Keep local cache available even when the remote repository is temporarily unreachable.
        }
    }

    private void UpsertKnowledgeEntry(string relativePath, bool remoteAvailable, bool localAvailable)
    {
        var normalizedPath = NormalizeKnowledgePath(relativePath);
        _knowledgeDocumentIndex[normalizedPath] = new Dictionary<string, object?>
        {
            ["path"] = normalizedPath,
            ["relative_path"] = normalizedPath,
            ["source"] = remoteAvailable && localAvailable ? "remote+local" : remoteAvailable ? "remote" : "local",
            ["cached_locally"] = localAvailable,
            ["remote_available"] = remoteAvailable,
        };
    }

    private string ReadKnowledgeDocumentContent(string normalizedPath)
    {
        if (_knowledgeDocumentCache.TryGetValue(normalizedPath, out var cachedText))
        {
            return cachedText;
        }

        var cachePath = ResolveKnowledgeCachePath(normalizedPath);
        if (File.Exists(cachePath))
        {
            var localText = File.ReadAllText(cachePath);
            _knowledgeDocumentCache[normalizedPath] = localText;
            return localText;
        }

        var settings = CreateKnowledgeBaseSettings();
        var remoteText = _sshService.ReadAllText(settings, CombineRemote(_config.KnowledgeBase.RootDir, normalizedPath));
        WriteKnowledgeCacheFile(normalizedPath, remoteText);
        _knowledgeDocumentCache[normalizedPath] = remoteText;
        UpsertKnowledgeEntry(normalizedPath, remoteAvailable: true, localAvailable: true);
        return remoteText;
    }

    private string ResolveKnowledgeCacheRoot() => ConfigService.ExpandPath(_config.KnowledgeBase.LocalCacheDir);

    private string ResolveKnowledgeCachePath(string relativePath)
    {
        var normalizedPath = NormalizeKnowledgePath(relativePath).Replace('/', Path.DirectorySeparatorChar);
        return Path.Combine(ResolveKnowledgeCacheRoot(), normalizedPath);
    }

    private void WriteKnowledgeCacheFile(string relativePath, string content)
    {
        var cachePath = ResolveKnowledgeCachePath(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
        File.WriteAllText(cachePath, content);
    }

    private static string NormalizeKnowledgePath(string path) => path.Replace('\\', '/').TrimStart('/').Trim();

    private static string SliceNumberedLines(string text, int startLine, int endLine)
    {
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        return string.Join(Environment.NewLine,
            normalized.Split('\n')
                .Skip(Math.Max(0, startLine - 1))
                .Take(Math.Max(1, endLine - startLine + 1))
                .Select((line, index) => $"{startLine + index}: {line}"));
    }

    private static Dictionary<string, object?> CreateTool(string name, string description, Dictionary<string, object?> properties, string[] required)
    {
        return new Dictionary<string, object?>
        {
            ["type"] = "function",
            ["function"] = new Dictionary<string, object?>
            {
                ["name"] = name,
                ["description"] = description,
                ["parameters"] = new Dictionary<string, object?>
                {
                    ["type"] = "object",
                    ["properties"] = properties,
                    ["required"] = required,
                },
            },
        };
    }

    private async Task<ToolExecutionResult> RunGitAsync(string arguments, string workingDirectory, string toolName, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = new Process { StartInfo = psi };
        process.Start();
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        var stderr = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        var ok = process.ExitCode == 0;
        return new ToolExecutionResult(toolName, ok, ok ? stdout.Trim() : stderr.Trim());
    }

    private (bool Ok, string Message) CheckCommandPolicy(string command)
    {
        if (_config.CommandPolicy.Mode.Equals("whitelist", StringComparison.OrdinalIgnoreCase))
        {
            var firstToken = command.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
            if (_config.CommandPolicy.Whitelist.Any(item => firstToken.Equals(item, StringComparison.OrdinalIgnoreCase)))
            {
                return (true, string.Empty);
            }

            return (false, $"命令不在白名单中: {firstToken}");
        }

        var blocked = _config.CommandPolicy.Blacklist.FirstOrDefault(item => command.Contains(item, StringComparison.OrdinalIgnoreCase));
        return blocked is null ? (true, string.Empty) : (false, $"命令命中黑名单规则: {blocked}");
    }

    private (bool Ok, string Message) CheckApproval(string toolName, string summary)
    {
        if (ApprovalPolicy.Equals("read-only", StringComparison.OrdinalIgnoreCase) && MutatingCommandPattern.IsMatch(summary))
        {
            return (false, "当前为只读模式，拒绝执行可能修改环境的操作。");
        }

        if (ApprovalPolicy.Equals("prompt", StringComparison.OrdinalIgnoreCase))
        {
            if (_confirmCallback is null || !_confirmCallback(toolName, summary))
            {
                return (false, "用户拒绝了本次操作。");
            }
        }

        return (true, string.Empty);
    }

    private string ResolvePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Cwd;
        }

        var expanded = ConfigService.ExpandPath(path);
        return Path.IsPathRooted(expanded) ? expanded : Path.GetFullPath(Path.Combine(Cwd, expanded));
    }

    private string ResolveExistingBase(string? path)
    {
        var resolved = ResolvePath(path);
        if (File.Exists(resolved))
        {
            return Path.GetDirectoryName(resolved)!;
        }

        return resolved;
    }

    private IEnumerable<string> EnumerateMatchingFiles(string root, string pattern, bool includeHidden)
    {
        if (!Directory.Exists(root))
        {
            return [];
        }

        var regex = GlobToRegex(pattern);
        return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(file => includeHidden || !Path.GetFileName(file).StartsWith(".", StringComparison.Ordinal))
            .Where(file => regex.IsMatch(Path.GetRelativePath(root, file).Replace('\\', '/')));
    }

    private static Regex GlobToRegex(string pattern)
    {
        var normalized = (string.IsNullOrWhiteSpace(pattern) ? "**/*" : pattern).Replace('\\', '/');
        var regex = Regex.Escape(normalized)
            .Replace(@"\*\*", "___DOUBLESTAR___")
            .Replace(@"\*", "[^/]*")
            .Replace(@"\?", ".")
            .Replace("___DOUBLESTAR___", ".*");
        return new Regex("^" + regex + "$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }

    private static void AppendTree(StringBuilder builder, string path, int depth, int maxDepth, ref int remaining)
    {
        if (remaining <= 0 || depth > maxDepth)
        {
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(path).OrderBy(static item => item))
        {
            if (remaining <= 0)
            {
                return;
            }

            builder.AppendLine($"{new string(' ', depth * 2)}- {Path.GetFileName(directory)}/");
            remaining--;
            AppendTree(builder, directory, depth + 1, maxDepth, ref remaining);
        }

        foreach (var file in Directory.EnumerateFiles(path).OrderBy(static item => item))
        {
            if (remaining <= 0)
            {
                return;
            }

            builder.AppendLine($"{new string(' ', depth * 2)}- {Path.GetFileName(file)}");
            remaining--;
        }
    }

    private static int CountOccurrences(string text, string searchText)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(searchText, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += searchText.Length;
        }

        return count;
    }

    private static string ReplaceOnce(string text, string searchText, string replaceText)
    {
        var index = text.IndexOf(searchText, StringComparison.Ordinal);
        return index < 0 ? text : text[..index] + replaceText + text[(index + searchText.Length)..];
    }

    private static string Json(object payload) => JsonSerializer.Serialize(payload, JsonOptions);

    private static bool MatchKnowledgePattern(string relativePath, IReadOnlyList<string> patterns)
    {
        if (patterns.Count == 0)
        {
            return relativePath.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || relativePath.EndsWith(".txt", StringComparison.OrdinalIgnoreCase);
        }

        return patterns.Any(pattern => GlobToRegex(pattern).IsMatch(relativePath.Replace('\\', '/')));
    }

    private static string CombineRemote(string root, string relativePath) => root.TrimEnd('/', '\\') + "/" + relativePath.TrimStart('/', '\\');

    private SshConnectionSettings CreateSshSettings(IReadOnlyDictionary<string, object?> args)
    {
        return new SshConnectionSettings
        {
            Host = GetRequiredString(args, "host"),
            Port = GetInt(args, "port", 22),
            Username = GetRequiredString(args, "username"),
            Password = GetOptionalString(args, "password") ?? string.Empty,
            Timeout = TimeSpan.FromSeconds(15),
        };
    }

    private SshConnectionSettings CreateKnowledgeBaseSettings()
    {
        return new SshConnectionSettings
        {
            Host = _config.KnowledgeBase.Host,
            Port = _config.KnowledgeBase.Port,
            Username = _config.KnowledgeBase.Username,
            Password = _config.KnowledgeBase.Password,
            Timeout = TimeSpan.FromSeconds(15),
        };
    }

    private static string GetRequiredString(IReadOnlyDictionary<string, object?> args, string key)
    {
        var value = GetOptionalString(args, key);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"缺少参数: {key}");
        }

        return value;
    }

    private static string? GetOptionalString(IReadOnlyDictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            JsonElement element when element.ValueKind == JsonValueKind.String => element.GetString(),
            JsonElement element => element.ToString(),
            _ => value.ToString(),
        };
    }

    private static int GetInt(IReadOnlyDictionary<string, object?> args, string key, int defaultValue = 0)
    {
        if (!args.TryGetValue(key, out var value) || value is null)
        {
            return defaultValue;
        }

        return value switch
        {
            int intValue => intValue,
            long longValue => (int)longValue,
            JsonElement element when element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var intValue) => intValue,
            JsonElement element when int.TryParse(element.ToString(), out var parsed) => parsed,
            _ when int.TryParse(value.ToString(), out var parsed) => parsed,
            _ => defaultValue,
        };
    }

    private static bool GetBool(IReadOnlyDictionary<string, object?> args, string key, bool defaultValue = false)
    {
        if (!args.TryGetValue(key, out var value) || value is null)
        {
            return defaultValue;
        }

        return value switch
        {
            bool boolValue => boolValue,
            JsonElement element when element.ValueKind == JsonValueKind.True => true,
            JsonElement element when element.ValueKind == JsonValueKind.False => false,
            JsonElement element when bool.TryParse(element.ToString(), out var parsed) => parsed,
            _ when bool.TryParse(value.ToString(), out var parsed) => parsed,
            _ => defaultValue,
        };
    }
}