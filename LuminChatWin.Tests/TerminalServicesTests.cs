using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using LuminChatWin.Core.Models;
using LuminChatWin.Core.Services;
using Renci.SshNet;

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
            var events = new List<AgentEvent>();
            var client = new FakeTerminalAgentChatCompletionClient(
                new LlmResponse
                {
                    Success = true,
                    ReasoningContent = "先确认终端状态。",
                    Content = """
                    {"analysis":"先打印标记。","complete":false,"final_message":""}
                    """,
                    ToolCalls =
                    [
                        new ToolCall("call-1", "run_shell_command", new Dictionary<string, object?>
                        {
                            ["command"] = "Write-Output 'agent-loop-ok'",
                        }),
                    ],
                },
                new LlmResponse
                {
                    Success = true,
                    Content = """
                    {"analysis":"输出已出现。","complete":true,"final_message":"任务完成"}
                    """,
                });
            var service = new TerminalAgentService(client, () => config, manager, () => workspaceRoot);
            var dialogue = new List<TerminalAgentDialogueItem>();

            var result = await service.RunLoopAsync(
                session.SessionId,
                "打印一个标记并确认完成",
                dialogue,
                "auto",
                TerminalAgentMode.Auto,
                new Progress<AgentEvent>(evt => events.Add(evt)));

            Assert.True(result.Success);
            Assert.True(result.Completed);
            Assert.Equal("任务完成", result.FinalMessage);
            Assert.Contains(events, static evt => evt.Type == AgentEventType.Reasoning && evt.Message.Contains("thinking:", StringComparison.Ordinal));
            Assert.Contains(events, static evt => evt.Type == AgentEventType.Content && evt.Message.Contains("content:", StringComparison.Ordinal));
            Assert.Contains(events, static evt => evt.Type == AgentEventType.ToolCall && string.Equals(evt.ToolName, "run_shell_command", StringComparison.Ordinal));
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
                    {"analysis":"建议先读取版本。","complete":false,"final_message":""}
                    """,
                    ToolCalls =
                    [
                        new ToolCall("call-1", "run_shell_command", new Dictionary<string, object?>
                        {
                            ["command"] = "Write-Output 'prompt-only'",
                        }),
                    ],
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

    [Fact]
    public async Task TerminalAgentService_OnlyExposesRequestedTools()
    {
        var config = AppConfig.CreateDefault();
        await using var manager = new TerminalSessionManager(() => config.Terminal);
        var workspaceRoot = CreateTempDirectory();
        var session = await manager.CreatePowerShellSessionAsync(new TerminalPowerShellOptions
        {
            Title = "Tool Filter PowerShell",
            Program = config.Terminal.DefaultPowershellProgram,
            Arguments = config.Terminal.DefaultPowershellArgs,
            WorkingDirectory = workspaceRoot,
        });

        try
        {
            var client = new CapturingTerminalAgentChatCompletionClient(new LlmResponse
            {
                Success = true,
                Content = """
                {"analysis":"工具列表已检查。","command":"","complete":true,"need_input":false,"final_message":"完成"}
                """,
            });
            var service = new TerminalAgentService(client, () => config, manager, () => workspaceRoot);

            var result = await service.RunLoopAsync(session.SessionId, "检查工具白名单", [], "auto", TerminalAgentMode.Prompt);

            Assert.True(result.Success);
            Assert.Equal(
                [
                    "fetch_web_page",
                    "list_knowledge_documents",
                    "read_knowledge_document",
                    "run_shell_command",
                    "search_web",
                    "ssh_execute_command",
                    "ssh_list_directory",
                    "ssh_read_file",
                    "ssh_write_file",
                    "write_knowledge_document",
                ],
                client.LastToolNames.OrderBy(static item => item, StringComparer.Ordinal).ToArray());
        }
        finally
        {
            await manager.StopSessionAsync(session.SessionId);
        }
    }

    [Fact]
    public async Task TerminalAgentService_UserPrompt_UsesTranscriptInsteadOfDialogueHistory()
    {
        var config = AppConfig.CreateDefault();
        await using var manager = new TerminalSessionManager(() => config.Terminal);
        var workspaceRoot = CreateTempDirectory();
        var session = await manager.CreatePowerShellSessionAsync(new TerminalPowerShellOptions
        {
            Title = "Transcript PowerShell",
            Program = config.Terminal.DefaultPowershellProgram,
            Arguments = config.Terminal.DefaultPowershellArgs,
            WorkingDirectory = workspaceRoot,
        });

        try
        {
            await manager.ExecuteCommandAsync(session.SessionId, "Write-Output 'transcript-only'", TimeSpan.FromSeconds(8));
            var client = new CapturingTerminalAgentChatCompletionClient(new LlmResponse
            {
                Success = true,
                Content = """
                {"analysis":"已读取终端输出。","command":"","complete":true,"need_input":false,"final_message":"完成"}
                """,
            });
            var service = new TerminalAgentService(client, () => config, manager, () => workspaceRoot);
            var dialogue = new List<TerminalAgentDialogueItem>
            {
                new() { Role = "assistant", Content = "this-should-not-appear" },
            };

            var result = await service.RunLoopAsync(session.SessionId, "只使用终端输出", dialogue, "auto", TerminalAgentMode.Prompt);

            Assert.True(result.Success);
            Assert.Contains("transcript-only", client.LastUserPrompt, StringComparison.Ordinal);
            Assert.DoesNotContain("this-should-not-appear", client.LastUserPrompt, StringComparison.Ordinal);
        }
        finally
        {
            await manager.StopSessionAsync(session.SessionId);
        }
    }

    [Fact]
    public void SystemPromptBuilder_IncludesRepositoryAndSecondaryServerContext()
    {
        var config = AppConfig.CreateDefault();
        config.SecondaryServer.Enabled = true;
        config.SecondaryServer.Host = "192.168.0.20";
        config.SecondaryServer.Port = 2222;
        config.SecondaryServer.User = "root";
        config.KnowledgeBase.Enabled = true;
        config.KnowledgeBase.Host = "10.0.0.8";
        config.KnowledgeBase.Port = 22;
        config.KnowledgeBase.RootDir = "/root/docs";
        config.KnowledgeBase.LocalCacheDir = "~/.cache/repository";

        var prompt = SystemPromptBuilder.Build(config, 1, 5);

        Assert.Contains("辅助服务器", prompt, StringComparison.Ordinal);
        Assert.Contains("192.168.0.20:2222", prompt, StringComparison.Ordinal);
        Assert.Contains("资料库", prompt, StringComparison.Ordinal);
        Assert.Contains("/root/docs", prompt, StringComparison.Ordinal);
        Assert.Contains(ConfigService.ExpandPath(config.KnowledgeBase.LocalCacheDir), prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SerialSshBridgeServer_ExecCommand_ReturnsCommandOutput()
    {
        var port = GetFreePort();
        var config = new TerminalSerialBridgeConfig
        {
            Username = "root",
            Password = "root",
            HostKeyPath = Path.Combine(CreateTempDirectory(), "bridge-hostkey.pem"),
        };
        string? receivedCommand = null;
        await using var bridge = new SerialSshBridgeServer(
            "session-exec",
            "session-exec",
            IPAddress.Loopback,
            port,
            config,
            (_, _) => Task.CompletedTask,
            () => string.Empty,
            (commandText, _, _) =>
            {
                receivedCommand = commandText;
                return Task.FromResult(new TerminalCommandResult
                {
                    Success = true,
                    Command = commandText,
                    Output = "bridge-exec-ok\n",
                });
            });

        bridge.Start();

        using var client = new SshClient("127.0.0.1", port, config.Username, config.Password);
        client.Connect();
        var command = client.CreateCommand("echo bridge");
        var output = command.Execute();

        Assert.Equal("echo bridge", receivedCommand);
        Assert.Contains("bridge-exec-ok", output, StringComparison.Ordinal);
        Assert.Equal(0, command.ExitStatus);

        client.Disconnect();
    }

    [Fact]
    public async Task SerialSshBridgeServer_ShellSession_ForwardsInputAndOutput()
    {
        var port = GetFreePort();
        var config = new TerminalSerialBridgeConfig
        {
            Username = "root",
            Password = "root",
            HostKeyPath = Path.Combine(CreateTempDirectory(), "bridge-shell-hostkey.pem"),
        };
        var receivedInput = new List<string>();
        using var receivedInputSignal = new AutoResetEvent(false);
        await using var bridge = new SerialSshBridgeServer(
            "session-shell",
            "Demo Serial",
            IPAddress.Loopback,
            port,
            config,
            (text, _) =>
            {
                lock (receivedInput)
                {
                    receivedInput.Add(text);
                }
                receivedInputSignal.Set();
                return Task.CompletedTask;
            },
            () => "cached-output\r\n",
            (_, _, _) => Task.FromResult(new TerminalCommandResult { Success = true }));

        bridge.Start();

        using var client = new SshClient("127.0.0.1", port, config.Username, config.Password);
        client.Connect();
        using var stream = client.CreateShellStream("xterm", 80, 24, 800, 600, 1024);

        var initialOutput = ReadUntilContains(stream, "cached-output", TimeSpan.FromSeconds(5));
        Assert.Contains("Serial SSH bridge attached", initialOutput, StringComparison.Ordinal);
        Assert.Contains("cached-output", initialOutput, StringComparison.Ordinal);

        stream.Write("status\n");
        stream.Flush();
        var forwardedInput = WaitForCombinedInput(receivedInput, receivedInputSignal, TimeSpan.FromSeconds(5));
        Assert.Contains("status", forwardedInput, StringComparison.Ordinal);

        bridge.HandleTerminalOutput(new TerminalOutputEventArgs
        {
            SessionId = "session-shell",
            Kind = TerminalHistoryEntryKind.Output,
            Text = "shell-output\r\n",
        });

        var shellOutput = ReadUntilContains(stream, "shell-output", TimeSpan.FromSeconds(5));
        Assert.Contains("shell-output", shellOutput, StringComparison.Ordinal);

        client.Disconnect();
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

    private static string ReadUntilContains(ShellStream stream, string expectedText, TimeSpan timeout)
    {
        var buffer = new StringBuilder();
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (stream.DataAvailable)
            {
                buffer.Append(stream.Read());
                if (buffer.ToString().Contains(expectedText, StringComparison.Ordinal))
                {
                    return buffer.ToString();
                }
            }
            else
            {
                Thread.Sleep(50);
            }
        }

        return buffer.ToString();
    }

    private static string WaitForCombinedInput(List<string> receivedInput, AutoResetEvent signal, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            lock (receivedInput)
            {
                var combined = string.Concat(receivedInput);
                if (combined.Contains("status", StringComparison.Ordinal))
                {
                    return combined;
                }
            }

            signal.WaitOne(50);
        }

        lock (receivedInput)
        {
            return string.Concat(receivedInput);
        }
    }

    private sealed class FakeTerminalAgentChatCompletionClient(params LlmResponse[] responses) : IChatCompletionClient
    {
        private readonly Queue<LlmResponse> _responses = new(responses);

        public Task<LlmResponse> CompleteAsync(AppConfig config, int modelLevel, IReadOnlyList<PersistedChatMessage> messages, IReadOnlyList<Dictionary<string, object?>>? tools, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_responses.Count > 0 ? _responses.Dequeue() : new LlmResponse { Success = true, Content = "{\"analysis\":\"\",\"command\":\"\",\"complete\":true,\"need_input\":false,\"final_message\":\"\"}" });
        }

        public Task<LlmResponse> CompleteStreamingAsync(AppConfig config, int modelLevel, IReadOnlyList<PersistedChatMessage> messages, IReadOnlyList<Dictionary<string, object?>>? tools, Action<string>? onReasoningChunk = null, Action<string>? onContentChunk = null, CancellationToken cancellationToken = default)
        {
            var response = _responses.Count > 0 ? _responses.Dequeue() : new LlmResponse { Success = true, Content = "{\"analysis\":\"\",\"complete\":true,\"final_message\":\"\"}" };
            if (!string.IsNullOrWhiteSpace(response.ReasoningContent))
            {
                onReasoningChunk?.Invoke(response.ReasoningContent);
            }

            if (!string.IsNullOrWhiteSpace(response.Content))
            {
                onContentChunk?.Invoke(response.Content);
            }

            return Task.FromResult(response);
        }
    }

    private sealed class CapturingTerminalAgentChatCompletionClient(params LlmResponse[] responses) : IChatCompletionClient
    {
        private readonly Queue<LlmResponse> _responses = new(responses);

        public IReadOnlyList<string> LastToolNames { get; private set; } = [];

        public string LastUserPrompt { get; private set; } = string.Empty;

        public Task<LlmResponse> CompleteAsync(AppConfig config, int modelLevel, IReadOnlyList<PersistedChatMessage> messages, IReadOnlyList<Dictionary<string, object?>>? tools, CancellationToken cancellationToken = default)
        {
            LastToolNames = tools?
                .Select(static tool => tool.TryGetValue("function", out var functionObject) && functionObject is Dictionary<string, object?> function && function.TryGetValue("name", out var nameObject) ? nameObject?.ToString() ?? string.Empty : string.Empty)
                .Where(static name => !string.IsNullOrWhiteSpace(name))
                .ToArray() ?? [];
            LastUserPrompt = messages.LastOrDefault(static item => item.Role == "user")?.Content ?? string.Empty;
            return Task.FromResult(_responses.Count > 0 ? _responses.Dequeue() : new LlmResponse { Success = true, Content = "{\"analysis\":\"\",\"command\":\"\",\"complete\":true,\"need_input\":false,\"final_message\":\"\"}" });
        }

        public Task<LlmResponse> CompleteStreamingAsync(AppConfig config, int modelLevel, IReadOnlyList<PersistedChatMessage> messages, IReadOnlyList<Dictionary<string, object?>>? tools, Action<string>? onReasoningChunk = null, Action<string>? onContentChunk = null, CancellationToken cancellationToken = default)
        {
            LastToolNames = tools?
                .Select(static tool => tool.TryGetValue("function", out var functionObject) && functionObject is Dictionary<string, object?> function && function.TryGetValue("name", out var nameObject) ? nameObject?.ToString() ?? string.Empty : string.Empty)
                .Where(static name => !string.IsNullOrWhiteSpace(name))
                .ToArray() ?? [];
            LastUserPrompt = messages.LastOrDefault(static item => item.Role == "user")?.Content ?? string.Empty;
            var response = _responses.Count > 0 ? _responses.Dequeue() : new LlmResponse { Success = true, Content = "{\"analysis\":\"\",\"complete\":true,\"final_message\":\"\"}" };
            if (!string.IsNullOrWhiteSpace(response.ReasoningContent))
            {
                onReasoningChunk?.Invoke(response.ReasoningContent);
            }

            if (!string.IsNullOrWhiteSpace(response.Content))
            {
                onContentChunk?.Invoke(response.Content);
            }

            return Task.FromResult(response);
        }
    }
}