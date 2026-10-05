using System.Text.Json;

namespace MachineVoice.Protocol;

public readonly record struct ClientDecode(ClientMessage? Message, string? Id, string? Error);

public static class ProtocolCodec
{
    public static string Write(ClientMessage message) => message switch
    {
        PauseCommand m => JsonSerializer.Serialize(m, ProtocolJsonContext.Default.PauseCommand),
        ResumeCommand m => JsonSerializer.Serialize(m, ProtocolJsonContext.Default.ResumeCommand),
        SkipCommand m => JsonSerializer.Serialize(m, ProtocolJsonContext.Default.SkipCommand),
        SetModeCommand m => JsonSerializer.Serialize(m, ProtocolJsonContext.Default.SetModeCommand),
        SetPlaybackRateCommand m => JsonSerializer.Serialize(m, ProtocolJsonContext.Default.SetPlaybackRateCommand),
        SeekCommand m => JsonSerializer.Serialize(m, ProtocolJsonContext.Default.SeekCommand),
        ListenCommand m => JsonSerializer.Serialize(m, ProtocolJsonContext.Default.ListenCommand),
        DismissCommand m => JsonSerializer.Serialize(m, ProtocolJsonContext.Default.DismissCommand),
        OpenChatCommand m => JsonSerializer.Serialize(m, ProtocolJsonContext.Default.OpenChatCommand),
        GetSettingsCommand m => JsonSerializer.Serialize(m, ProtocolJsonContext.Default.GetSettingsCommand),
        UpdateSettingsCommand m => JsonSerializer.Serialize(m, ProtocolJsonContext.Default.UpdateSettingsCommand),
        ConnectSourceCommand m => JsonSerializer.Serialize(m, ProtocolJsonContext.Default.ConnectSourceCommand),
        DisconnectSourceCommand m => JsonSerializer.Serialize(m, ProtocolJsonContext.Default.DisconnectSourceCommand),
        GetSourceStatusCommand m => JsonSerializer.Serialize(m, ProtocolJsonContext.Default.GetSourceStatusCommand),
        GetSnapshotCommand m => JsonSerializer.Serialize(m, ProtocolJsonContext.Default.GetSnapshotCommand),
        ConnectMcpCommand m => JsonSerializer.Serialize(m, ProtocolJsonContext.Default.ConnectMcpCommand),
        DisconnectMcpCommand m => JsonSerializer.Serialize(m, ProtocolJsonContext.Default.DisconnectMcpCommand),
        GetMcpStatusCommand m => JsonSerializer.Serialize(m, ProtocolJsonContext.Default.GetMcpStatusCommand),
        _ => throw new ArgumentOutOfRangeException(nameof(message), message.GetType().Name, "Unknown command."),
    };

    public static string Write(ServerMessage message) => message switch
    {
        ResultMessage m => JsonSerializer.Serialize(m, ProtocolJsonContext.Default.ResultMessage),
        SnapshotEvent m => JsonSerializer.Serialize(m, ProtocolJsonContext.Default.SnapshotEvent),
        PlayerStateEvent m => JsonSerializer.Serialize(m, ProtocolJsonContext.Default.PlayerStateEvent),
        PlayerProgressEvent m => JsonSerializer.Serialize(m, ProtocolJsonContext.Default.PlayerProgressEvent),
        PlayerPositionEvent m => JsonSerializer.Serialize(m, ProtocolJsonContext.Default.PlayerPositionEvent),
        QueueChangedEvent m => JsonSerializer.Serialize(m, ProtocolJsonContext.Default.QueueChangedEvent),
        HistoryAppendedEvent m => JsonSerializer.Serialize(m, ProtocolJsonContext.Default.HistoryAppendedEvent),
        ConfirmationRequestedEvent m => JsonSerializer.Serialize(m, ProtocolJsonContext.Default.ConfirmationRequestedEvent),
        ConfirmationClearedEvent m => JsonSerializer.Serialize(m, ProtocolJsonContext.Default.ConfirmationClearedEvent),
        SettingsChangedEvent m => JsonSerializer.Serialize(m, ProtocolJsonContext.Default.SettingsChangedEvent),
        SourceChangedEvent m => JsonSerializer.Serialize(m, ProtocolJsonContext.Default.SourceChangedEvent),
        McpChangedEvent m => JsonSerializer.Serialize(m, ProtocolJsonContext.Default.McpChangedEvent),
        ChatRequestedEvent m => JsonSerializer.Serialize(m, ProtocolJsonContext.Default.ChatRequestedEvent),
        _ => throw new ArgumentOutOfRangeException(nameof(message), message.GetType().Name, "Unknown server message."),
    };

    public static ClientDecode ReadClient(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return new ClientDecode(null, null, ProtocolErrors.InvalidMessage);

            var id = ReadString(root, "id");
            var type = ReadString(root, "type");
            if (type is null)
                return new ClientDecode(null, id, ProtocolErrors.InvalidMessage);

            ClientMessage? message = type switch
            {
                "pause" => root.Deserialize(ProtocolJsonContext.Default.PauseCommand),
                "resume" => root.Deserialize(ProtocolJsonContext.Default.ResumeCommand),
                "skip" => root.Deserialize(ProtocolJsonContext.Default.SkipCommand),
                "setMode" => root.Deserialize(ProtocolJsonContext.Default.SetModeCommand),
                "setPlaybackRate" => root.Deserialize(ProtocolJsonContext.Default.SetPlaybackRateCommand),
                "seek" => root.Deserialize(ProtocolJsonContext.Default.SeekCommand),
                "listen" => root.Deserialize(ProtocolJsonContext.Default.ListenCommand),
                "dismiss" => root.Deserialize(ProtocolJsonContext.Default.DismissCommand),
                "openChat" => root.Deserialize(ProtocolJsonContext.Default.OpenChatCommand),
                "getSettings" => root.Deserialize(ProtocolJsonContext.Default.GetSettingsCommand),
                "updateSettings" => root.Deserialize(ProtocolJsonContext.Default.UpdateSettingsCommand),
                "connectSource" => root.Deserialize(ProtocolJsonContext.Default.ConnectSourceCommand),
                "disconnectSource" => root.Deserialize(ProtocolJsonContext.Default.DisconnectSourceCommand),
                "getSourceStatus" => root.Deserialize(ProtocolJsonContext.Default.GetSourceStatusCommand),
                "getSnapshot" => root.Deserialize(ProtocolJsonContext.Default.GetSnapshotCommand),
                "connectMcp" => root.Deserialize(ProtocolJsonContext.Default.ConnectMcpCommand),
                "disconnectMcp" => root.Deserialize(ProtocolJsonContext.Default.DisconnectMcpCommand),
                "getMcpStatus" => root.Deserialize(ProtocolJsonContext.Default.GetMcpStatusCommand),
                _ => null,
            };

            return message is null
                ? new ClientDecode(null, id, ProtocolErrors.UnknownCommand)
                : new ClientDecode(message, id, null);
        }
        catch (JsonException)
        {
            return new ClientDecode(null, null, ProtocolErrors.InvalidMessage);
        }
    }

    public static ServerMessage? ReadServer(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            return null;

        return ReadString(root, "type") switch
        {
            "result" => root.Deserialize(ProtocolJsonContext.Default.ResultMessage),
            "snapshot" => root.Deserialize(ProtocolJsonContext.Default.SnapshotEvent),
            "player.state" => root.Deserialize(ProtocolJsonContext.Default.PlayerStateEvent),
            "player.progress" => root.Deserialize(ProtocolJsonContext.Default.PlayerProgressEvent),
            "player.position" => root.Deserialize(ProtocolJsonContext.Default.PlayerPositionEvent),
            "queue.changed" => root.Deserialize(ProtocolJsonContext.Default.QueueChangedEvent),
            "history.appended" => root.Deserialize(ProtocolJsonContext.Default.HistoryAppendedEvent),
            "confirmation.requested" => root.Deserialize(ProtocolJsonContext.Default.ConfirmationRequestedEvent),
            "confirmation.cleared" => root.Deserialize(ProtocolJsonContext.Default.ConfirmationClearedEvent),
            "settings.changed" => root.Deserialize(ProtocolJsonContext.Default.SettingsChangedEvent),
            "source.changed" => root.Deserialize(ProtocolJsonContext.Default.SourceChangedEvent),
            "mcp.changed" => root.Deserialize(ProtocolJsonContext.Default.McpChangedEvent),
            "chat.requested" => root.Deserialize(ProtocolJsonContext.Default.ChatRequestedEvent),
            _ => null,
        };
    }

    static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
