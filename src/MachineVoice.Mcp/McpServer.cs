using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using MachineVoice.Protocol;

namespace MachineVoice.Mcp;

/// <summary>
/// MCP server over stdio (newline-delimited JSON-RPC). Every tool call opens its own control connection,
/// so the server keeps working when MachineVoice restarts.
/// </summary>
public sealed class McpServer(Func<CancellationToken, Task<IControlClient>> connect, Action<string>? log = null)
{
    public const string LatestProtocolVersion = "2025-06-18";
    static readonly string[] SupportedProtocolVersions = ["2025-11-25", LatestProtocolVersion, "2025-03-26", "2024-11-05"];

    const int ParseError = -32700;
    const int InvalidRequest = -32600;
    const int MethodNotFound = -32601;
    const int InvalidParams = -32602;

    const string Instructions =
        "MachineVoice is a background app that reads AI assistant responses aloud. " +
        "Use these tools when the user asks to pause, resume, stop or skip the speech, change the playback mode " +
        "or the LLM retelling settings. Call machinevoice_status first when the request depends on the current state.";

    public static string DefaultSocketPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library",
            "Application Support",
            "MachineVoice",
            "control.sock");

    public async Task RunAsync(TextReader input, TextWriter output, CancellationToken cancellationToken = default)
    {
        while (await input.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var response = await HandleAsync(line, cancellationToken).ConfigureAwait(false);
            if (response is null)
                continue;

            await output.WriteLineAsync(response.ToJsonString().AsMemory(), cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    async Task<JsonObject?> HandleAsync(string line, CancellationToken cancellationToken)
    {
        JsonObject? request;
        try
        {
            request = JsonNode.Parse(line) as JsonObject;
        }
        catch (JsonException)
        {
            return Error(null, ParseError, "Parse error.");
        }

        if (request is null)
            return Error(null, InvalidRequest, "Expected a JSON-RPC object.");
        if (!request.TryGetPropertyValue("id", out var idNode))
            return null;

        var id = idNode?.DeepClone();
        if (request.ContainsKey("result") || request.ContainsKey("error"))
            return null;
        if (request["method"] is not JsonValue methodNode || !methodNode.TryGetValue<string>(out var method))
            return Error(id, InvalidRequest, "Missing method.");

        var parameters = request["params"] as JsonObject;
        return method switch
        {
            "initialize" => Result(id, Initialize(parameters)),
            "ping" => Result(id, new JsonObject()),
            "tools/list" => Result(id, new JsonObject { ["tools"] = McpTools.Definitions() }),
            "tools/call" => await CallAsync(id, parameters, cancellationToken).ConfigureAwait(false),
            _ => Error(id, MethodNotFound, $"Method not found: {method}."),
        };
    }

    static JsonObject Initialize(JsonObject? parameters)
    {
        var requested = parameters?["protocolVersion"] is JsonValue value && value.TryGetValue<string>(out var version)
            ? version
            : null;
        return new JsonObject
        {
            ["protocolVersion"] = requested is not null && SupportedProtocolVersions.Contains(requested)
                ? requested
                : LatestProtocolVersion,
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
            ["serverInfo"] = new JsonObject { ["name"] = "machinevoice", ["version"] = "0.1.0" },
            ["instructions"] = Instructions,
        };
    }

    async Task<JsonObject> CallAsync(JsonNode? id, JsonObject? parameters, CancellationToken cancellationToken)
    {
        var name = parameters?["name"] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
        var tool = McpTools.Find(name);
        if (tool is null)
            return Error(id, InvalidParams, $"Unknown tool: {name}.");

        var arguments = new ToolArguments(parameters?["arguments"] as JsonObject ?? new JsonObject());
        ToolResult result;
        try
        {
            await using var client = await connect(cancellationToken).ConfigureAwait(false);
            result = await tool.Run(client, arguments, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException or IOException)
        {
            log?.Invoke($"Control connection failed: {ex.Message}");
            result = ToolResult.Fail("MachineVoice is not running: its control socket is unavailable.");
        }

        return Result(id, new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = result.Text }),
            ["isError"] = result.IsError,
        });
    }

    static JsonObject Result(JsonNode? id, JsonObject result) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["result"] = result,
    };

    static JsonObject Error(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    };
}
