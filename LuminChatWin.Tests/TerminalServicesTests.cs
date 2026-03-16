using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using LuminChatWin.Core.Models;
using LuminChatWin.Core.Services;

namespace LuminChatWin.Tests;

public sealed class TerminalServicesTests
{
    [Fact]
    public void TerminalSessionManager_RenderTerminalPreview_StripsAnsiAndHandlesCarriageReturn()
    {
        var rendered = TerminalSessionManager.RenderTerminalPreview(
            "\u001b[32mok3568 ~\u001b[m # ",
            "ifconfig",
            "\r\n",
            "done\r",
            "ready\r\n");

        Assert.DoesNotContain("\u001b", rendered, StringComparison.Ordinal);
        Assert.Contains("ok3568 ~ # ifconfig", rendered, StringComparison.Ordinal);
        Assert.Contains("ready", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("done", rendered, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("22", "COM1 @ 115200", "session-a", 2201)]
    [InlineData("22", "COM12 @ 115200", "session-b", 2212)]
    [InlineData("22", "ttyUSB105 @ 115200", "session-c", 2205)]
    public void TerminalSessionManager_BuildDefaultBridgePort_UsesTwoDigitSerialSuffix(string prefix, string descriptor, string sessionId, int expectedPort)
    {
        var port = TerminalSessionManager.BuildDefaultBridgePort(prefix, descriptor, sessionId);
        Assert.Equal(expectedPort, port);
    }

    [Theory]
    [InlineData("COM12 @ 115200", "COM12")]
    [InlineData("ttyUSB7 @ 921600", "ttyUSB7")]
    [InlineData(" COM3   @ 9600 ", "COM3")]
    public void TerminalSessionManager_ResolveBridgeOverrideKey_UsesStableSerialKey(string descriptor, string expectedKey)
    {
        Assert.Equal(expectedKey, TerminalSessionManager.ResolveBridgeOverrideKey(descriptor));
    }

    [Fact]
    public async Task TerminalSessionManager_ExecutesPowerShellCommand()
    {
        var config = AppConfig.CreateDefault();
        await using var manager = new TerminalSessionManager(() => config.Terminal);
        var tempDir = CreateTempDirectory();

        var session = await manager.CreatePowerShellSessionAsync(new TerminalPowerShellOptions
        {
            Title = "Test PowerShell",
            Program = config.Terminal.DefaultPowershellProgram,
            Arguments = config.Terminal.DefaultPowershellArgs,
            WorkingDirectory = tempDir,
        });

        try
        {
            var result = await manager.ExecuteCommandAsync(session.SessionId, "Write-Output 'terminal-ok'", TimeSpan.FromSeconds(8));
            Assert.True(result.Success);
            Assert.Contains("terminal-ok", result.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await manager.StopSessionAsync(session.SessionId);
        }
    }

    [Fact]
    public async Task TerminalApiServer_ReturnsSessionsAndCommandOutput()
    {
        var config = AppConfig.CreateDefault();
        config.Terminal.ExecApi.Enabled = true;
        config.Terminal.ExecApi.BindHost = "127.0.0.1";
        config.Terminal.ExecApi.Port = GetFreePort();
        config.Terminal.ExecApi.DefaultTimeoutSeconds = 8;

        await using var manager = new TerminalSessionManager(() => config.Terminal);
        await using var apiServer = new TerminalApiServer(manager, () => config.Terminal);
        var session = await manager.CreatePowerShellSessionAsync(new TerminalPowerShellOptions
        {
            Title = "API PowerShell",
            Program = config.Terminal.DefaultPowershellProgram,
            Arguments = config.Terminal.DefaultPowershellArgs,
            WorkingDirectory = CreateTempDirectory(),
        });

        try
        {
            await apiServer.StartAsync();
            using var client = new HttpClient { BaseAddress = new Uri(apiServer.BaseUrl) };

            var sessionsResponse = await client.GetAsync("api/sessions");
            sessionsResponse.EnsureSuccessStatusCode();
            var sessionsJson = await sessionsResponse.Content.ReadAsStringAsync();
            Assert.Contains(session.SessionId, sessionsJson, StringComparison.OrdinalIgnoreCase);

            var execResponse = await client.PostAsJsonAsync("api/exec_cmd", new
            {
                sessionId = session.SessionId,
                command = "Write-Output 'api-ok'",
                timeoutSeconds = 8,
            });
            var execJson = await execResponse.Content.ReadAsStringAsync();
            Assert.True(execResponse.IsSuccessStatusCode, execJson);
            Assert.Contains("api-ok", execJson, StringComparison.OrdinalIgnoreCase);

            var currentResponse = await client.GetAsync($"api/sessions/{session.SessionId}/current-output");
            currentResponse.EnsureSuccessStatusCode();
            var currentPayload = JsonDocument.Parse(await currentResponse.Content.ReadAsStringAsync());
            Assert.True(currentPayload.RootElement.TryGetProperty("isRunning", out _));
        }
        finally
        {
            await apiServer.StopAsync();
            await manager.StopSessionAsync(session.SessionId);
        }
    }

    [Fact]
    public async Task TerminalAgentService_AutoModeExecutesUntilComplete()
    {
        var config = AppConfig.CreateDefault();
        config.App.MaxToolRounds = 4;

        await using var manager = new TerminalSessionManager(() => config.Terminal);
        var workspaceRoot = CreateTempDirectory();
        var session = await manager.CreatePowerShellSessionAsync(new TerminalPowerShellOptions
        {
            Title = "Agent PowerShell",
            Program = config.Terminal.DefaultPowershellProgram,
            Arguments = config.Terminal.DefaultPowershellArgs,
            WorkingDirectory = workspaceRoot,
        });

        try
        {
            var client = new FakeTerminalAgentChatCompletionClient(
                new LlmResponse
                {
                    Success = true,
                    Content = """
                    {"analysis":"先打印标记。","command":"Write-Output 'agent-loop-ok'","complete":false,"need_input":false,"final_message":""}
                    """,
                },
                new LlmResponse
                {
                    Success = true,
                    Content = """
                    {"analysis":"输出已出现。","command":"","complete":true,"need_input":false,"final_message":"任务完成"}
                    """,
                });
            var service = new TerminalAgentService(client, () => config, manager, () => workspaceRoot);
            var dialogue = new List<TerminalAgentDialogueItem>();

            var result = await service.RunLoopAsync(session.SessionId, "打印一个标记并确认完成", dialogue, "auto", TerminalAgentMode.Auto);

            Assert.True(result.Success);
            Assert.True(result.Completed);
            Assert.Equal("任务完成", result.FinalMessage);
            var output = manager.GetRecentOutput(session.SessionId, 4000);
            Assert.Contains("agent-loop-ok", output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await manager.StopSessionAsync(session.SessionId);
        }
    }

    [Fact]
    public async Task TerminalAgentService_PromptModeReturnsSuggestionWithoutExecuting()
    {
        var config = AppConfig.CreateDefault();
        await using var manager = new TerminalSessionManager(() => config.Terminal);
        var workspaceRoot = CreateTempDirectory();
        var session = await manager.CreatePowerShellSessionAsync(new TerminalPowerShellOptions
        {
            Title = "Prompt Agent PowerShell",
            Program = config.Terminal.DefaultPowershellProgram,
            Arguments = config.Terminal.DefaultPowershellArgs,
            WorkingDirectory = workspaceRoot,
        });

        try
        {
            var client = new FakeTerminalAgentChatCompletionClient(
                new LlmResponse
                {
                    Success = true,
                    Content = """
                    {"analysis":"建议先读取版本。","command":"Write-Output 'prompt-only'","complete":false,"need_input":false,"final_message":""}
                    """,
                });
            var service = new TerminalAgentService(client, () => config, manager, () => workspaceRoot);
            var dialogue = new List<TerminalAgentDialogueItem>();

            var result = await service.RunLoopAsync(session.SessionId, "只给出下一步建议", dialogue, "auto", TerminalAgentMode.Prompt);

            Assert.True(result.Success);
            Assert.False(result.Completed);
            Assert.Equal("Write-Output 'prompt-only'", result.SuggestedCommand);
            var output = manager.GetRecentOutput(session.SessionId, 4000);
            Assert.DoesNotContain("prompt-only", output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await manager.StopSessionAsync(session.SessionId);
        }
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "LuminChatWinTerminalTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class FakeTerminalAgentChatCompletionClient(params LlmResponse[] responses) : IChatCompletionClient
    {
        private readonly Queue<LlmResponse> _responses = new(responses);

        public Task<LlmResponse> CompleteAsync(AppConfig config, int modelLevel, IReadOnlyList<PersistedChatMessage> messages, IReadOnlyList<Dictionary<string, object?>>? tools, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_responses.Count > 0 ? _responses.Dequeue() : new LlmResponse { Success = true, Content = "{\"analysis\":\"\",\"command\":\"\",\"complete\":true,\"need_input\":false,\"final_message\":\"\"}" });
        }
    }
}