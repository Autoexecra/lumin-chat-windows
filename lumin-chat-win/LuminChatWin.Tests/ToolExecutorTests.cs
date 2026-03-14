using System.Text.Json;
using LuminChatWin.Core.Models;
using LuminChatWin.Core.Services;

namespace LuminChatWin.Tests;

public sealed class ToolExecutorTests
{
    [Fact]
    public void ToolExecutor_WritesReplacesAndReadsFiles()
    {
        var root = CreateTempDirectory();
        var executor = new ToolExecutor(AppConfig.CreateDefault(), root);

        var write = executor.WriteFile("notes.txt", "alpha\nbeta\n");
        var replace = executor.ReplaceInFile("notes.txt", "beta", "gamma");
        var insert = executor.InsertInFile("notes.txt", "header", 1);
        var read = executor.ReadFile("notes.txt", 1, 10);

        Assert.True(write.Ok);
        Assert.True(replace.Ok);
        Assert.True(insert.Ok);
        Assert.Contains("1: header", read.Output);
        Assert.Contains("3: gamma", read.Output);
    }

    [Fact]
    public async Task ToolExecutor_ReturnsEnvironmentPayload()
    {
        var root = CreateTempDirectory();
        var executor = new ToolExecutor(AppConfig.CreateDefault(), root);

        var result = await executor.ExecuteAsync(new ToolCall("1", "get_environment", new Dictionary<string, object?>()));
        using var document = JsonDocument.Parse(result.Output);

        Assert.True(result.Ok);
        Assert.True(document.RootElement.TryGetProperty("cwd", out _));
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "LuminChatWinTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}