using System.Text.Json;
using System.Text.Json.Nodes;
using MachineVoice.Protocol;

namespace MachineVoice.Mcp;

sealed record McpTool(
    string Name,
    string Title,
    string Description,
    Func<JsonObject> InputSchema,
    Func<JsonObject> Annotations,
    Func<IControlClient, ToolArguments, CancellationToken, Task<ToolResult>> Run);

sealed record ToolResult(string Text, bool IsError)
{
    public static ToolResult Ok(string text) => new(text, false);
    public static ToolResult Fail(string text) => new(text, true);
}

/// <summary>
/// The tools cover playback, its speed, the mode and the retelling switch. Connecting sources (it edits ~/.cursor/hooks.json),
/// the LLM endpoint and the API key stay in the settings UI: an agent should not redirect where responses are sent.
/// </summary>
static class McpTools
{
    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    static readonly McpTool[] All =
    [
        new(
            "machinevoice_status",
            "MachineVoice status",
            "Shows what MachineVoice is doing: player state, playback mode, the response being read, " +
            "the response awaiting confirmation, the queue, recent history, sources and the LLM retelling settings. " +
            "Items carry ids, sources, projects and topics, not the response text.",
            () => Schema(new JsonObject()),
            () => new JsonObject { ["readOnlyHint"] = true, ["openWorldHint"] = false },
            StatusAsync),
        new(
            "machinevoice_playback",
            "Control playback",
            "pause: pause the current response. resume: continue a paused response, or start the queue again after " +
            "a speech failure. skip: drop the current response, the one awaiting confirmation or the queue head, and move on.",
            () => Schema(new JsonObject { ["action"] = Enum("What to do.", "pause", "resume", "skip") }, "action"),
            () => new JsonObject { ["readOnlyHint"] = false, ["destructiveHint"] = false, ["openWorldHint"] = false },
            PlaybackAsync),
        new(
            "machinevoice_set_mode",
            "Set playback mode",
            "auto: read every response as it arrives. confirm: ask before reading each response. " +
            "silent: do not read responses, only keep them in history.",
            () => Schema(new JsonObject { ["mode"] = Enum("The playback mode.", "auto", "confirm", "silent") }, "mode"),
            () => new JsonObject { ["readOnlyHint"] = false, ["destructiveHint"] = false, ["idempotentHint"] = true, ["openWorldHint"] = false },
            SetModeAsync),
        new(
            "machinevoice_set_speed",
            "Set reading speed",
            "Speeds up or slows down reading at once, the response being read too; the pitch stays the same. " +
            "1 is the normal speed. For \"faster\" or \"slower\" take playbackRate from machinevoice_status and add or subtract 0.25.",
            () => Schema(new JsonObject
            {
                ["rate"] = new JsonObject
                {
                    ["type"] = "number",
                    ["minimum"] = TtsSettingsDto.MinPlaybackRate,
                    ["maximum"] = TtsSettingsDto.MaxPlaybackRate,
                    ["description"] = "Playback speed: 0.5 is half as fast, 2 twice as fast.",
                },
            }, "rate"),
            () => new JsonObject { ["readOnlyHint"] = false, ["destructiveHint"] = false, ["idempotentHint"] = true, ["openWorldHint"] = false },
            SetSpeedAsync),
        new(
            "machinevoice_confirmation",
            "Answer the confirmation",
            "In the confirm mode a response waits for the user's decision. listen: read it now. dismiss: skip it. " +
            "Without item_id the tool answers the response that awaits confirmation right now.",
            () => Schema(new JsonObject
            {
                ["action"] = Enum("The decision.", "listen", "dismiss"),
                ["item_id"] = new JsonObject { ["type"] = "string", ["description"] = "Item id from machinevoice_status." },
            }, "action"),
            () => new JsonObject { ["readOnlyHint"] = false, ["destructiveHint"] = false, ["openWorldHint"] = false },
            ConfirmationAsync),
        new(
            "machinevoice_set_summary",
            "Configure the LLM retelling",
            "Turns the LLM retelling of responses on or off and sets the model and the timeout. " +
            "Omitted fields keep their values. The endpoint and the API key are changed only in the MachineVoice settings.",
            () => Schema(new JsonObject
            {
                ["enabled"] = new JsonObject { ["type"] = "boolean", ["description"] = "Retell responses through the LLM before reading them." },
                ["model"] = new JsonObject { ["type"] = "string", ["description"] = "Model name as the endpoint knows it, e.g. qwen3:8b. Required to enable the retelling." },
                ["timeout_seconds"] = new JsonObject
                {
                    ["type"] = "integer",
                    ["minimum"] = 1,
                    ["maximum"] = SummarySettingsDto.MaxTimeoutSeconds,
                    ["description"] = "How long to wait for the retelling before reading the response without it.",
                },
            }),
            () => new JsonObject { ["readOnlyHint"] = false, ["destructiveHint"] = false, ["idempotentHint"] = true, ["openWorldHint"] = false },
            SetSummaryAsync),
    ];

    public static JsonArray Definitions() => new(All.Select(tool => (JsonNode)new JsonObject
    {
        ["name"] = tool.Name,
        ["title"] = tool.Title,
        ["description"] = tool.Description,
        ["inputSchema"] = tool.InputSchema(),
        ["annotations"] = tool.Annotations(),
    }).ToArray());

    public static McpTool? Find(string? name) => All.FirstOrDefault(tool => tool.Name == name);

    static async Task<ToolResult> StatusAsync(IControlClient client, ToolArguments arguments, CancellationToken cancellationToken)
    {
        var result = await client.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (!result.Ok || result.Snapshot is null)
            return Rejected(result);

        var snapshot = result.Snapshot;
        var summary = snapshot.Settings.Summary;
        var report = new JsonObject
        {
            ["player"] = Name(snapshot.Player, ProtocolJsonContext.Default.PlayerState),
            ["mode"] = Name(snapshot.Mode, ProtocolJsonContext.Default.PlaybackMode),
            ["order"] = Name(snapshot.Settings.Order, ProtocolJsonContext.Default.QueueOrder),
            ["heading"] = Name(snapshot.Settings.Heading, ProtocolJsonContext.Default.HeadingMode),
            ["heldAfterFailure"] = snapshot.Holding,
            ["current"] = Item(snapshot.Current),
            ["awaitingConfirmation"] = Item(snapshot.Confirmation),
            ["queue"] = new JsonArray(snapshot.Queue.Select(item => Item(item)).ToArray()),
            ["recent"] = new JsonArray(snapshot.History.TakeLast(5).Reverse().Select(entry => (JsonNode)new JsonObject
            {
                ["item"] = Item(entry.Item),
                ["outcome"] = Name(entry.Outcome, ProtocolJsonContext.Default.SpeechOutcome),
                ["finishedAt"] = entry.FinishedAt,
            }).ToArray()),
            ["sources"] = new JsonArray(snapshot.Sources.Select(source => (JsonNode)new JsonObject
            {
                ["name"] = source.Name,
                ["status"] = Name(source.Status, ProtocolJsonContext.Default.SourceConnectionStatus),
            }).ToArray()),
            ["summary"] = new JsonObject
            {
                ["enabled"] = summary.Enabled,
                ["endpoint"] = summary.Endpoint,
                ["model"] = summary.Model,
                ["timeoutSeconds"] = summary.TimeoutSeconds,
                ["apiKeySet"] = !string.IsNullOrEmpty(summary.ApiKey),
            },
            ["tts"] = new JsonObject
            {
                ["engine"] = Name(snapshot.Settings.Tts.Engine, ProtocolJsonContext.Default.TtsEngineKind),
                ["voice"] = snapshot.Settings.Tts.Engine switch
                {
                    TtsEngineKind.Qwen => snapshot.Settings.Tts.Qwen.Voice,
                    TtsEngineKind.OmniVoice => snapshot.Settings.Tts.OmniVoice.Voice is { Length: > 0 } voice ? voice : "random",
                    _ => null,
                },
                ["playbackRate"] = snapshot.Settings.Tts.PlaybackRate,
            },
        };
        return ToolResult.Ok(report.ToJsonString(Indented));
    }

    static async Task<ToolResult> PlaybackAsync(IControlClient client, ToolArguments arguments, CancellationToken cancellationToken)
    {
        var action = arguments.String("action");
        if (arguments.Error is { } error)
            return ToolResult.Fail(error);

        Task<ResultMessage>? command = action switch
        {
            "pause" => client.PauseAsync(cancellationToken),
            "resume" => client.ResumeAsync(cancellationToken),
            "skip" => client.SkipAsync(cancellationToken),
            _ => null,
        };
        if (command is null)
            return ToolResult.Fail("action must be one of: pause, resume, skip.");

        var result = await command.ConfigureAwait(false);
        return result.Ok ? ToolResult.Ok($"Done: {action}.") : Rejected(result);
    }

    static async Task<ToolResult> SetModeAsync(IControlClient client, ToolArguments arguments, CancellationToken cancellationToken)
    {
        var mode = arguments.Enum("mode", ProtocolJsonContext.Default.PlaybackMode, "auto, confirm, silent");
        if (arguments.Error is { } error)
            return ToolResult.Fail(error);
        if (mode is null)
            return ToolResult.Fail("mode is required.");

        var result = await client.SetModeAsync(mode.Value, cancellationToken).ConfigureAwait(false);
        return result.Ok ? ToolResult.Ok($"Mode: {Name(mode.Value, ProtocolJsonContext.Default.PlaybackMode)}.") : Rejected(result);
    }

    static async Task<ToolResult> SetSpeedAsync(IControlClient client, ToolArguments arguments, CancellationToken cancellationToken)
    {
        var rate = arguments.Double("rate");
        if (arguments.Error is { } error)
            return ToolResult.Fail(error);
        if (rate is null)
            return ToolResult.Fail("rate is required.");

        var result = await client.SetPlaybackRateAsync(rate.Value, cancellationToken).ConfigureAwait(false);
        if (result.Error == ProtocolErrors.InvalidArgument)
            return ToolResult.Fail($"rate must be from {TtsSettingsDto.MinPlaybackRate} to {TtsSettingsDto.MaxPlaybackRate}.");
        return result.Ok ? ToolResult.Ok($"Speed: {Math.Round(rate.Value, 2)}x.") : Rejected(result);
    }

    static async Task<ToolResult> ConfirmationAsync(IControlClient client, ToolArguments arguments, CancellationToken cancellationToken)
    {
        var action = arguments.String("action");
        var itemId = arguments.String("item_id");
        if (arguments.Error is { } error)
            return ToolResult.Fail(error);
        if (action is not ("listen" or "dismiss"))
            return ToolResult.Fail("action must be one of: listen, dismiss.");

        if (string.IsNullOrWhiteSpace(itemId))
        {
            var snapshot = await client.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
            if (!snapshot.Ok || snapshot.Snapshot is null)
                return Rejected(snapshot);
            itemId = snapshot.Snapshot.Confirmation?.Id;
            if (itemId is null)
                return ToolResult.Fail("No response awaits confirmation.");
        }

        var result = action == "listen"
            ? await client.ListenAsync(itemId, cancellationToken).ConfigureAwait(false)
            : await client.DismissAsync(itemId, cancellationToken).ConfigureAwait(false);
        return result.Ok ? ToolResult.Ok($"Done: {action} {itemId}.") : Rejected(result);
    }

    static async Task<ToolResult> SetSummaryAsync(IControlClient client, ToolArguments arguments, CancellationToken cancellationToken)
    {
        var enabled = arguments.Bool("enabled");
        var model = arguments.String("model");
        var timeout = arguments.Int("timeout_seconds");
        if (arguments.Error is { } error)
            return ToolResult.Fail(error);
        if (enabled is null && model is null && timeout is null)
            return ToolResult.Fail("Pass at least one of: enabled, model, timeout_seconds.");

        var current = await client.GetSettingsAsync(cancellationToken).ConfigureAwait(false);
        if (!current.Ok || current.Settings is null)
            return Rejected(current);

        var summary = current.Settings.Summary;
        var updated = new SummarySettingsDto
        {
            Enabled = enabled ?? summary.Enabled,
            Endpoint = summary.Endpoint,
            Model = model ?? summary.Model,
            ApiKey = summary.ApiKey,
            TimeoutSeconds = timeout ?? summary.TimeoutSeconds,
        };
        var result = await client.UpdateSettingsAsync(summary: updated, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (result.Error == ProtocolErrors.InvalidArgument)
            return ToolResult.Fail(
                $"Invalid retelling settings: a model is required to enable it, the timeout is 1 to {SummarySettingsDto.MaxTimeoutSeconds} seconds.");
        if (!result.Ok)
            return Rejected(result);

        var state = updated.Enabled ? "on" : "off";
        var shownModel = string.IsNullOrWhiteSpace(updated.Model) ? "not set" : updated.Model.Trim();
        return ToolResult.Ok($"Retelling: {state}, model: {shownModel}, timeout: {updated.TimeoutSeconds} s.");
    }

    static ToolResult Rejected(ResultMessage result) => ToolResult.Fail(result.Error switch
    {
        ProtocolErrors.InvalidState => "Not possible in the current player state. Call machinevoice_status to see it.",
        ProtocolErrors.NotFound => "The item is not found or no longer awaits confirmation.",
        ProtocolErrors.UnsupportedVersion => "This MCP server and the running MachineVoice use different protocol versions.",
        var error => $"MachineVoice rejected the command: {error ?? "unknown error"}.",
    });

    static JsonNode? Item(SpeechItemDto? item) => item is null
        ? null
        : new JsonObject
        {
            ["id"] = item.Id,
            ["source"] = item.Source,
            ["project"] = item.Project,
            ["topic"] = item.Topic,
            ["receivedAt"] = item.ReceivedAt,
        };

    static JsonNode? Name<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type) =>
        JsonSerializer.SerializeToNode(value, type);

    static JsonObject Schema(JsonObject properties, params string[] required)
    {
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["additionalProperties"] = false,
        };
        if (required.Length > 0)
            schema["required"] = new JsonArray(required.Select(name => (JsonNode)name).ToArray());
        return schema;
    }

    static JsonObject Enum(string description, params string[] values) => new()
    {
        ["type"] = "string",
        ["enum"] = new JsonArray(values.Select(value => (JsonNode)value).ToArray()),
        ["description"] = description,
    };
}

/// <summary>Reads typed tool arguments and remembers the first one with a wrong type.</summary>
sealed class ToolArguments(JsonObject json)
{
    public string? Error { get; private set; }

    public string? String(string name) => Get<string>(name, "a string");
    public bool? Bool(string name) => GetValue<bool>(name, "a boolean");
    public int? Int(string name) => GetValue<int>(name, "an integer");
    public double? Double(string name) => GetValue<double>(name, "a number");

    public T? Enum<T>(string name, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type, string allowed) where T : struct
    {
        var text = String(name);
        if (text is null)
            return null;
        var quoted = JsonValue.Create(text).ToJsonString();
        try
        {
            // The enum converter also accepts numbers and other casing; only the exact names are valid here.
            var value = JsonSerializer.Deserialize(quoted, type);
            if (JsonSerializer.Serialize(value, type) == quoted)
                return value;
        }
        catch (JsonException)
        {
        }

        Error ??= $"{name} must be one of: {allowed}.";
        return null;
    }

    T? Get<T>(string name, string expected) where T : class
    {
        if (!json.TryGetPropertyValue(name, out var node) || node is null)
            return null;
        if (node is JsonValue value && value.TryGetValue<T>(out var result))
            return result;
        Error ??= $"{name} must be {expected}.";
        return null;
    }

    T? GetValue<T>(string name, string expected) where T : struct
    {
        if (!json.TryGetPropertyValue(name, out var node) || node is null)
            return null;
        if (node is JsonValue value && value.TryGetValue<T>(out var result))
            return result;
        Error ??= $"{name} must be {expected}.";
        return null;
    }
}
