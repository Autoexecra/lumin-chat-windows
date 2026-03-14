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
}