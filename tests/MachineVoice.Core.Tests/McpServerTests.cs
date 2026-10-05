using System.Text.Json.Nodes;
using MachineVoice.Core;
using MachineVoice.Mcp;
using MachineVoice.Protocol;

namespace MachineVoice.Core.Tests;

public class McpServerTests
{
    [Fact(Timeout = 20000)]
    public async Task Handshake_ListsTheTools_AndAnswersProtocolErrors()
    {
        var server = new McpServer(_ => throw new InvalidOperationException("Not expected to connect."));

        var responses = await ExchangeAsync(server,
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26","capabilities":{},"clientInfo":{"name":"test","version":"1"}}}""",
            """{"jsonrpc":"2.0","method":"notifications/initialized"}""",
            """{"jsonrpc":"2.0","id":"two","method":"tools/list"}""",
            """{"jsonrpc":"2.0","id":3,"method":"ping"}""",
            """{"jsonrpc":"2.0","id":4,"method":"resources/list"}""",
            """{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"missing"}}""",
            "not json");

        Assert.Equal(6, responses.Count);
        var initialize = responses[0]["result"]!;
        Assert.Equal("2025-03-26", (string?)initialize["protocolVersion"]);
        Assert.NotNull(initialize["capabilities"]!["tools"]);
        Assert.Equal("machinevoice", (string?)initialize["serverInfo"]!["name"]);

        Assert.Equal("two", (string?)responses[1]["id"]);
        var tools = responses[1]["result"]!["tools"]!.AsArray().Select(tool => (string?)tool!["name"]).ToList();
        Assert.Equal(
            ["machinevoice_status", "machinevoice_playback", "machinevoice_set_mode", "machinevoice_set_speed", "machinevoice_confirmation", "machinevoice_set_summary"],
            tools);

        Assert.Empty(responses[2]["result"]!.AsObject());
        Assert.Equal(-32601, (int?)responses[3]["error"]!["code"]);
        Assert.Equal(-32602, (int?)responses[4]["error"]!["code"]);
        Assert.Equal(-32700, (int?)responses[5]["error"]!["code"]);
        Assert.Null(responses[5]["id"]);
    }

    [Fact(Timeout = 20000)]
    public async Task UnknownProtocolVersion_GetsTheLatestOne()
    {
        var server = new McpServer(_ => throw new InvalidOperationException("Not expected to connect."));

        var response = Assert.Single(await ExchangeAsync(server,
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"1999-01-01"}}"""));

        Assert.Equal(McpServer.LatestProtocolVersion, (string?)response["result"]!["protocolVersion"]);
    }

    [Fact(Timeout = 20000)]
    public async Task Tools_DriveMachineVoiceOverTheControlSocket()
    {
        await using var root = new TempRoot();
        var tts = new ManualTtsEngine();
        var host = await TestHost.StartAsync(root.Path, tts);
        var server = new McpServer(async cancellationToken =>
            await SocketControlClient.ConnectAsync(host.ControlSocketPath, cancellationToken));
        await using var client = await host.ConnectInProcessAsync();
        Assert.True((await client.UpdateSettingsAsync(summary: new SummarySettingsDto
        {
            Endpoint = "http://llm.test/v1",
            ApiKey = "secret",
        })).Ok);

        var mode = await CallAsync(server, "machinevoice_set_mode", new JsonObject { ["mode"] = "confirm" });
        Assert.False(IsError(mode), Text(mode));
        Assert.Equal(PlaybackMode.Confirm, (await client.GetSettingsAsync()).Settings!.Mode);

        var accepted = await IngestClient.SubmitAsync(host.IngestSocketPath, new SpeechDraft(
            "cursor", "g1", "секретный текст ответа", Project: "MachineVoice", Topic: "тема"));
        var status = await CallAsync(server, "machinevoice_status", new JsonObject());
        Assert.False(IsError(status), Text(status));
        var report = JsonNode.Parse(Text(status))!;
        Assert.Equal("confirm", (string?)report["mode"]);
        Assert.Equal(accepted.Id, (string?)report["awaitingConfirmation"]!["id"]);
        Assert.Equal("тема", (string?)report["awaitingConfirmation"]!["topic"]);
        Assert.True((bool?)report["summary"]!["apiKeySet"]);
        Assert.DoesNotContain("secret", Text(status));
        Assert.DoesNotContain("секретный", Text(status));

        var listen = await CallAsync(server, "machinevoice_confirmation", new JsonObject { ["action"] = "listen" });
        Assert.False(IsError(listen), Text(listen));
        var speaking = (await client.GetSnapshotAsync()).Snapshot!;
        Assert.Equal(PlayerState.Speaking, speaking.Player);
        Assert.Equal(accepted.Id, speaking.Current!.Id);

        var pause = await CallAsync(server, "machinevoice_playback", new JsonObject { ["action"] = "pause" });
        Assert.False(IsError(pause), Text(pause));
        Assert.Equal(PlayerState.Paused, (await client.GetSnapshotAsync()).Snapshot!.Player);

        var speed = await CallAsync(server, "machinevoice_set_speed", new JsonObject { ["rate"] = 1.5 });
        Assert.False(IsError(speed), Text(speed));
        Assert.Equal(1.5, (await client.GetSettingsAsync()).Settings!.Tts.PlaybackRate);
        var speedStatus = JsonNode.Parse(Text(await CallAsync(server, "machinevoice_status", new JsonObject())))!;
        Assert.Equal(1.5, (double?)speedStatus["tts"]!["playbackRate"]);

        var summary = await CallAsync(server, "machinevoice_set_summary", new JsonObject
        {
            ["enabled"] = true,
            ["model"] = "qwen3:8b",
            ["timeout_seconds"] = 30,
        });
        Assert.False(IsError(summary), Text(summary));
        var settings = (await client.GetSettingsAsync()).Settings!.Summary;
        Assert.True(settings.Enabled);
        Assert.Equal("qwen3:8b", settings.Model);
        Assert.Equal(30, settings.TimeoutSeconds);
        Assert.Equal("http://llm.test/v1", settings.Endpoint);
        Assert.Equal("secret", settings.ApiKey);

        await host.DisposeAsync();
    }

    [Fact(Timeout = 20000)]
    public async Task BadArguments_AndRejectedCommands_AreToolErrors()
    {
        await using var root = new TempRoot();
        var host = await TestHost.StartAsync(root.Path, new ManualTtsEngine());
        var server = new McpServer(async cancellationToken =>
            await SocketControlClient.ConnectAsync(host.ControlSocketPath, cancellationToken));

        Assert.True(IsError(await CallAsync(server, "machinevoice_set_mode", new JsonObject { ["mode"] = "Loud" })));
        Assert.True(IsError(await CallAsync(server, "machinevoice_set_mode", new JsonObject { ["mode"] = "1" })));
        Assert.True(IsError(await CallAsync(server, "machinevoice_playback", new JsonObject { ["action"] = 5 })));
        Assert.True(IsError(await CallAsync(server, "machinevoice_set_summary", new JsonObject())));
        Assert.True(IsError(await CallAsync(server, "machinevoice_set_summary", new JsonObject { ["enabled"] = true })));
        Assert.True(IsError(await CallAsync(server, "machinevoice_confirmation", new JsonObject { ["action"] = "listen" })));
        Assert.True(IsError(await CallAsync(server, "machinevoice_set_speed", new JsonObject())));
        Assert.True(IsError(await CallAsync(server, "machinevoice_set_speed", new JsonObject { ["rate"] = "fast" })));
        Assert.True(IsError(await CallAsync(server, "machinevoice_set_speed", new JsonObject { ["rate"] = 3 })));

        var pause = await CallAsync(server, "machinevoice_playback", new JsonObject { ["action"] = "pause" });
        Assert.True(IsError(pause));
        Assert.Contains("machinevoice_status", Text(pause));

        await host.DisposeAsync();
    }

    [Fact(Timeout = 20000)]
    public async Task StoppedMachineVoice_IsAToolError()
    {
        await using var root = new TempRoot();
        var socket = Path.Combine(root.Path, "control.sock");
        var server = new McpServer(async cancellationToken =>
            await SocketControlClient.ConnectAsync(socket, cancellationToken));

        var status = await CallAsync(server, "machinevoice_status", new JsonObject());

        Assert.True(IsError(status));
        Assert.Contains("not running", Text(status));
    }

    static async Task<JsonNode> CallAsync(McpServer server, string tool, JsonObject arguments)
    {
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = arguments },
        };
        var response = Assert.Single(await ExchangeAsync(server, request.ToJsonString()));
        return response["result"] ?? throw new InvalidOperationException(response.ToJsonString());
    }

    static bool IsError(JsonNode result) => (bool)result["isError"]!;

    static string Text(JsonNode result) => (string)result["content"]![0]!["text"]!;

    static async Task<List<JsonNode>> ExchangeAsync(McpServer server, params string[] lines)
    {
        using var input = new StringReader(string.Join('\n', lines) + "\n");
        await using var output = new StringWriter();
        await server.RunAsync(input, output);
        return output.ToString()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonNode.Parse(line)!)
            .ToList();
    }
}
