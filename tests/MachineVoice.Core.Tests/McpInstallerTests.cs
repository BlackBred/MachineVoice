using System.Text.Json;
using MachineVoice.Protocol;

namespace MachineVoice.Core.Tests;

public class McpInstallerTests
{
    [Fact(Timeout = 20000)]
    public async Task Connect_CreatesTheFile_AndDisconnectRemovesIt()
    {
        await using var root = new TempRoot();
        var bundled = Bundled(root.Path, "v1");
        var host = await TestHost.StartAsync(root.Path, new ManualTtsEngine(), mcpBinary: bundled);
        await using var client = await host.ConnectInProcessAsync();
        var log = EventLog.Pump(client);

        var before = await client.GetMcpStatusAsync();
        Assert.True(before.Mcp!.Available);
        Assert.Equal(SourceConnectionStatus.Disconnected, before.Mcp.Status);

        var connected = await client.ConnectMcpAsync();
        Assert.True(connected.Ok);
        Assert.Equal(SourceConnectionStatus.Connected, connected.Mcp!.Status);
        Assert.Equal(SourceConnectionStatus.Connected, (await log.TakeAsync<McpChangedEvent>()).Mcp.Status);

        var installed = Path.Combine(root.Path, "MachineVoice.Mcp");
        Assert.Equal("v1", await File.ReadAllTextAsync(installed));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(installed) & (UnixFileMode)0x1FF);
        using (var doc = JsonDocument.Parse(await File.ReadAllTextAsync(McpPath(root.Path))))
        {
            var entry = doc.RootElement.GetProperty("mcpServers").GetProperty("machinevoice");
            Assert.Equal(installed, entry.GetProperty("command").GetString());
            Assert.Equal(["--socket", host.ControlSocketPath], entry.GetProperty("args").EnumerateArray().Select(arg => arg.GetString()));
        }

        var disconnected = await client.DisconnectMcpAsync();
        Assert.Equal(SourceConnectionStatus.Disconnected, disconnected.Mcp!.Status);
        Assert.False(File.Exists(McpPath(root.Path)));
        Assert.False(File.Exists(installed));
        await host.DisposeAsync();
    }

    [Fact(Timeout = 20000)]
    public async Task ForeignServers_KeepTheirBytes()
    {
        await using var root = new TempRoot();
        var host = await TestHost.StartAsync(root.Path, new ManualTtsEngine(), mcpBinary: Bundled(root.Path, "v1"));
        const string original = """
            {
                // personal servers
                "mcpServers": {
                    "github": { "command": "npx",   "args": ["-y", "@modelcontextprotocol/server-github"],
                                "env": { "GITHUB_TOKEN": "секрет" } },
                    "docs":   {"url": "https://example.com/mcp"}
                },
                "note": [1,2,3]
            }

            """;
        Directory.CreateDirectory(Path.GetDirectoryName(McpPath(root.Path))!);
        await File.WriteAllTextAsync(McpPath(root.Path), original);

        await using var client = await host.ConnectInProcessAsync();
        Assert.Equal(SourceConnectionStatus.Connected, (await client.ConnectMcpAsync()).Mcp!.Status);
        var body = await File.ReadAllTextAsync(McpPath(root.Path));
        Assert.Contains("""
                    "github": { "command": "npx",   "args": ["-y", "@modelcontextprotocol/server-github"],
                                "env": { "GITHUB_TOKEN": "секрет" } },
                    "docs":   {"url": "https://example.com/mcp"},
                    "machinevoice": {
                        "command":
            """, body);
        Assert.Contains("// personal servers", body);
        Assert.Equal(original, await File.ReadAllTextAsync(McpPath(root.Path) + ".bak"));

        Assert.Equal(SourceConnectionStatus.Disconnected, (await client.DisconnectMcpAsync()).Mcp!.Status);
        Assert.Equal(original, await File.ReadAllTextAsync(McpPath(root.Path)));
        await host.DisposeAsync();
    }

    [Theory(Timeout = 20000)]
    [InlineData("""{"mcpServers":{"other":{"command":"x"}}}""")]
    [InlineData("""{"other": true}""")]
    [InlineData("{\r\n  \"mcpServers\": {}\r\n}\r\n")]
    [InlineData("\uFEFF{\n\t\"mcpServers\": {\n\t\t\"other\": {\"command\": \"x\"},\n\t},\n}\n")]
    public async Task Connect_AddsAValidEntry_ToAnyLayout(string original)
    {
        await using var root = new TempRoot();
        var host = await TestHost.StartAsync(root.Path, new ManualTtsEngine(), mcpBinary: Bundled(root.Path, "v1"));
        Directory.CreateDirectory(Path.GetDirectoryName(McpPath(root.Path))!);
        await File.WriteAllTextAsync(McpPath(root.Path), original);

        await using var client = await host.ConnectInProcessAsync();
        Assert.Equal(SourceConnectionStatus.Connected, (await client.ConnectMcpAsync()).Mcp!.Status);
        var options = new JsonDocumentOptions { AllowTrailingCommas = true };
        var bytes = await File.ReadAllBytesAsync(McpPath(root.Path));
        Assert.Equal(original.StartsWith('\uFEFF'), bytes.AsSpan().StartsWith((byte[])[0xEF, 0xBB, 0xBF]));
        using (var doc = JsonDocument.Parse(await File.ReadAllTextAsync(McpPath(root.Path)), options))
            Assert.Equal(JsonValueKind.Object, doc.RootElement.GetProperty("mcpServers").GetProperty("machinevoice").ValueKind);
        if (original.Contains("\r\n", StringComparison.Ordinal))
            Assert.DoesNotContain("\n", (await File.ReadAllTextAsync(McpPath(root.Path))).Replace("\r\n", "", StringComparison.Ordinal));

        Assert.Equal(SourceConnectionStatus.Disconnected, (await client.DisconnectMcpAsync()).Mcp!.Status);
        if (File.Exists(McpPath(root.Path)))
        {
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(McpPath(root.Path)), options);
            Assert.False(doc.RootElement.TryGetProperty("mcpServers", out var servers) && servers.TryGetProperty("machinevoice", out _));
        }

        await host.DisposeAsync();
    }

    [Fact(Timeout = 20000)]
    public async Task OtherPath_OrUpdatedApp_IsStale()
    {
        await using var root = new TempRoot();
        var bundled = Bundled(root.Path, "v1");
        var host = await TestHost.StartAsync(root.Path, new ManualTtsEngine(), mcpBinary: bundled);
        Directory.CreateDirectory(Path.GetDirectoryName(McpPath(root.Path))!);
        await File.WriteAllTextAsync(McpPath(root.Path), """
            {
              "mcpServers": {
                "machinevoice": { "command": "dotnet", "args": ["/old/MachineVoice.Mcp.dll"] }
              }
            }
            """);

        await using var client = await host.ConnectInProcessAsync();
        Assert.Equal(SourceConnectionStatus.Stale, (await client.GetMcpStatusAsync()).Mcp!.Status);
        Assert.Equal(SourceConnectionStatus.Connected, (await client.ConnectMcpAsync()).Mcp!.Status);
        Assert.DoesNotContain("/old/MachineVoice.Mcp.dll", await File.ReadAllTextAsync(McpPath(root.Path)));

        await File.WriteAllTextAsync(bundled, "v2");
        Assert.Equal(SourceConnectionStatus.Stale, (await client.GetMcpStatusAsync()).Mcp!.Status);
        Assert.Equal(SourceConnectionStatus.Connected, (await client.ConnectMcpAsync()).Mcp!.Status);
        Assert.Equal("v2", await File.ReadAllTextAsync(Path.Combine(root.Path, "MachineVoice.Mcp")));
        await host.DisposeAsync();
    }

    [Fact(Timeout = 20000)]
    public async Task CorruptFile_IsLeftUntouched()
    {
        await using var root = new TempRoot();
        var host = await TestHost.StartAsync(root.Path, new ManualTtsEngine(), mcpBinary: Bundled(root.Path, "v1"));
        Directory.CreateDirectory(Path.GetDirectoryName(McpPath(root.Path))!);
        await File.WriteAllTextAsync(McpPath(root.Path), """{"mcpServers": {"machinevoice": {}, "machinevoice": {}}}""");

        await using var client = await host.ConnectInProcessAsync();
        Assert.Equal(SourceConnectionStatus.Stale, (await client.GetMcpStatusAsync()).Mcp!.Status);
        var connected = await client.ConnectMcpAsync();
        Assert.False(connected.Ok);
        Assert.Equal(ProtocolErrors.InvalidState, connected.Error);
        Assert.False(File.Exists(Path.Combine(root.Path, "MachineVoice.Mcp")));

        await File.WriteAllTextAsync(McpPath(root.Path), "{");
        Assert.False((await client.ConnectMcpAsync()).Ok);
        Assert.False((await client.DisconnectMcpAsync()).Ok);
        Assert.Equal("{", await File.ReadAllTextAsync(McpPath(root.Path)));
        await host.DisposeAsync();
    }

    [Fact(Timeout = 20000)]
    public async Task WithoutBundledBinary_ConnectFails()
    {
        await using var root = new TempRoot();
        var host = await TestHost.StartAsync(root.Path, new ManualTtsEngine());
        await using var client = await SocketControlClient.ConnectAsync(host.ControlSocketPath);

        var status = await client.GetMcpStatusAsync();
        Assert.False(status.Mcp!.Available);
        Assert.Equal(SourceConnectionStatus.Disconnected, status.Mcp.Status);
        var connected = await client.ConnectMcpAsync();
        Assert.False(connected.Ok);
        Assert.Equal(ProtocolErrors.InvalidState, connected.Error);
        Assert.False(File.Exists(McpPath(root.Path)));
        await host.DisposeAsync();
    }

    static string Bundled(string root, string content)
    {
        var path = Path.Combine(root, "bundle", "MachineVoice.Mcp");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    static string McpPath(string root) => Path.Combine(root, "cursor-config", "mcp.json");
}
