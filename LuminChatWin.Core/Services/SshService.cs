using Renci.SshNet;
using Renci.SshNet.Common;

namespace LuminChatWin.Core.Services;

public sealed class SshConnectionSettings
{
    public string Host { get; init; } = string.Empty;
    public int Port { get; init; } = 22;
    public string Username { get; init; } = string.Empty;
    public string Password { get; init; } = string.Empty;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(15);
}

public sealed class SshService
{
    public Dictionary<string, object?> RunCommand(SshConnectionSettings settings, string command, int timeoutSeconds = 60, string? cwd = null)
    {
        using var client = CreateClient(settings);
        client.Connect();
        var remoteCommand = string.IsNullOrWhiteSpace(cwd) ? command : $"cd {EscapeShellArg(cwd)} && {command}";
        using var cmd = client.CreateCommand(remoteCommand);
        cmd.CommandTimeout = TimeSpan.FromSeconds(timeoutSeconds);
        var result = cmd.Execute();
        return new Dictionary<string, object?>
        {
            ["command"] = command,
            ["cwd"] = cwd ?? string.Empty,
            ["exit_code"] = cmd.ExitStatus,
            ["stdout"] = result,
            ["stderr"] = cmd.Error,
            ["host"] = settings.Host,
            ["port"] = settings.Port,
            ["username"] = settings.Username,
        };
    }

    public void UploadFile(SshConnectionSettings settings, string localPath, string remotePath)
    {
        using var sftp = CreateSftp(settings);
        sftp.Connect();
        EnsureRemoteDirectory(sftp, Path.GetDirectoryName(remotePath)?.Replace('\\', '/') ?? "/");
        using var stream = File.OpenRead(localPath);
        sftp.UploadFile(stream, remotePath, true);
    }

    public void DownloadFile(SshConnectionSettings settings, string remotePath, string localPath)
    {
        using var sftp = CreateSftp(settings);
        sftp.Connect();
        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
        using var stream = File.Create(localPath);
        sftp.DownloadFile(remotePath, stream);
    }

    public IReadOnlyList<Dictionary<string, object?>> ListDirectory(SshConnectionSettings settings, string path, bool recursive = false, int maxEntries = 200)
    {
        using var sftp = CreateSftp(settings);
        sftp.Connect();
        var results = new List<Dictionary<string, object?>>();
        WalkDirectory(sftp, path, path, recursive, maxEntries, results);
        return results;
    }

    public string ReadFile(SshConnectionSettings settings, string path, int startLine = 1, int endLine = 200)
    {
        var content = ReadAllText(settings, path);
        var lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        return string.Join(Environment.NewLine, lines.Skip(Math.Max(0, startLine - 1)).Take(Math.Max(1, endLine - startLine + 1)).Select((line, index) => $"{startLine + index}: {line}"));
    }

    public string ReadAllText(SshConnectionSettings settings, string path)
    {
        using var sftp = CreateSftp(settings);
        sftp.Connect();
        using var stream = sftp.OpenRead(path);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public void WriteFile(SshConnectionSettings settings, string path, string content, bool append = false)
    {
        using var sftp = CreateSftp(settings);
        sftp.Connect();
        EnsureRemoteDirectory(sftp, Path.GetDirectoryName(path)?.Replace('\\', '/') ?? "/");
        using var stream = append && sftp.Exists(path) ? sftp.Open(path, FileMode.Append) : sftp.Open(path, FileMode.Create);
        using var writer = new StreamWriter(stream);
        writer.Write(content);
    }

    public void MakeDirectory(SshConnectionSettings settings, string path)
    {
        using var sftp = CreateSftp(settings);
        sftp.Connect();
        EnsureRemoteDirectory(sftp, path);
    }

    public void RemovePath(SshConnectionSettings settings, string path)
    {
        using var client = CreateClient(settings);
        client.Connect();
        using var cmd = client.CreateCommand($"rm -rf {EscapeShellArg(path)}");
        cmd.Execute();
    }

    public bool PathExists(SshConnectionSettings settings, string path)
    {
        using var sftp = CreateSftp(settings);
        sftp.Connect();
        return sftp.Exists(path);
    }

    private static SftpClient CreateSftp(SshConnectionSettings settings) => new(settings.Host, settings.Port, settings.Username, settings.Password) { OperationTimeout = settings.Timeout };

    private static SshClient CreateClient(SshConnectionSettings settings) => new(settings.Host, settings.Port, settings.Username, settings.Password) { ConnectionInfo = { Timeout = settings.Timeout } };

    private static void EnsureRemoteDirectory(SftpClient sftp, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path == "/")
        {
            return;
        }

        var current = string.Empty;
        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current += "/" + segment;
            if (!sftp.Exists(current))
            {
                sftp.CreateDirectory(current);
            }
        }
    }

    private static void WalkDirectory(SftpClient sftp, string currentPath, string rootPath, bool recursive, int maxEntries, List<Dictionary<string, object?>> results)
    {
        if (results.Count >= maxEntries)
        {
            return;
        }

        foreach (var entry in sftp.ListDirectory(currentPath))
        {
            if (entry.Name is "." or "..")
            {
                continue;
            }

            var relative = currentPath == rootPath ? entry.Name : Path.Combine(currentPath[(rootPath.Length)..].TrimStart('/', '\\'), entry.Name).Replace('\\', '/');
            results.Add(new Dictionary<string, object?>
            {
                ["path"] = entry.FullName,
                ["relative_path"] = relative,
                ["is_dir"] = entry.IsDirectory,
                ["size"] = entry.Attributes.Size,
                ["last_write_time"] = entry.Attributes.LastWriteTimeUtc.ToString("O"),
            });

            if (recursive && entry.IsDirectory)
            {
                WalkDirectory(sftp, entry.FullName, rootPath, true, maxEntries, results);
            }

            if (results.Count >= maxEntries)
            {
                return;
            }
        }
    }

    private static string EscapeShellArg(string value) => $"'{value.Replace("'", "'\\''", StringComparison.Ordinal)}'";
}