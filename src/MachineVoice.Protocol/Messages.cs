namespace MachineVoice.Protocol;

public abstract class ClientMessage
{
    public int Version { get; init; }
    public string Id { get; init; } = "";
}

public sealed class PauseCommand : ClientMessage
{
    public string Type { get; init; } = "pause";
}

public sealed class ResumeCommand : ClientMessage
{
    public string Type { get; init; } = "resume";
}

public sealed class StopCommand : ClientMessage
{
    public string Type { get; init; } = "stop";
}

public sealed class SkipCommand : ClientMessage
{
    public string Type { get; init; } = "skip";
}

public sealed class SetModeCommand : ClientMessage
{
    public string Type { get; init; } = "setMode";
    public PlaybackMode? Mode { get; init; }
}

public sealed class ListenCommand : ClientMessage
{
    public string Type { get; init; } = "listen";
    public string ItemId { get; init; } = "";
}

public sealed class DismissCommand : ClientMessage
{
    public string Type { get; init; } = "dismiss";
    public string ItemId { get; init; } = "";
}

public sealed class OpenChatCommand : ClientMessage
{
    public string Type { get; init; } = "openChat";
    public string ItemId { get; init; } = "";
}

public sealed class GetSettingsCommand : ClientMessage
{
    public string Type { get; init; } = "getSettings";
}

public sealed class UpdateSettingsCommand : ClientMessage
{
    public string Type { get; init; } = "updateSettings";
    public PlaybackMode? Mode { get; init; }
    public SummarySettingsDto? Summary { get; init; }
}

public sealed class ConnectSourceCommand : ClientMessage
{
    public string Type { get; init; } = "connectSource";
    public string Source { get; init; } = "";
}

public sealed class DisconnectSourceCommand : ClientMessage
{
    public string Type { get; init; } = "disconnectSource";
    public string Source { get; init; } = "";
}

public sealed class GetSourceStatusCommand : ClientMessage
{
    public string Type { get; init; } = "getSourceStatus";
    public string Source { get; init; } = "";
}

public sealed class GetSnapshotCommand : ClientMessage
{
    public string Type { get; init; } = "getSnapshot";
}

/// <summary>Registers the bundled MCP server in ~/.cursor/mcp.json. Not a source of responses.</summary>
public sealed class ConnectMcpCommand : ClientMessage
{
    public string Type { get; init; } = "connectMcp";
}

public sealed class DisconnectMcpCommand : ClientMessage
{
    public string Type { get; init; } = "disconnectMcp";
}

public sealed class GetMcpStatusCommand : ClientMessage
{
    public string Type { get; init; } = "getMcpStatus";
}

public abstract class ServerMessage
{
    public int Version { get; init; } = ProtocolVersion.Current;
}

public sealed class ResultMessage : ServerMessage
{
    public string Type { get; init; } = "result";
    public string Id { get; init; } = "";
    public bool Ok { get; init; }
    public string? Error { get; init; }
    public SettingsDto? Settings { get; init; }
    public SourceStatusDto? Source { get; init; }
    public SnapshotDto? Snapshot { get; init; }
    public McpStatusDto? Mcp { get; init; }
}

public abstract class EventMessage : ServerMessage;

public sealed class SnapshotEvent : EventMessage
{
    public string Type { get; init; } = "snapshot";
    public SnapshotDto Snapshot { get; init; } = new();
}

public sealed class PlayerStateEvent : EventMessage
{
    public string Type { get; init; } = "player.state";
    public PlayerState State { get; init; }
    public string? ItemId { get; init; }
}

public sealed class PlayerProgressEvent : EventMessage
{
    public string Type { get; init; } = "player.progress";
    public string ItemId { get; init; } = "";
    public int WordIndex { get; init; }
    public string Word { get; init; } = "";
}

public sealed class QueueChangedEvent : EventMessage
{
    public string Type { get; init; } = "queue.changed";
    public List<SpeechItemDto> Items { get; init; } = [];
}

public sealed class HistoryAppendedEvent : EventMessage
{
    public string Type { get; init; } = "history.appended";
    public HistoryEntryDto Entry { get; init; } = new();
}

public sealed class ConfirmationRequestedEvent : EventMessage
{
    public string Type { get; init; } = "confirmation.requested";
    public SpeechItemDto Item { get; init; } = new();
}

public sealed class ConfirmationClearedEvent : EventMessage
{
    public string Type { get; init; } = "confirmation.cleared";
    public string ItemId { get; init; } = "";
}

public sealed class SettingsChangedEvent : EventMessage
{
    public string Type { get; init; } = "settings.changed";
    public SettingsDto Settings { get; init; } = new();
}

public sealed class SourceChangedEvent : EventMessage
{
    public string Type { get; init; } = "source.changed";
    public string Source { get; init; } = "";
    public SourceConnectionStatus Status { get; init; }
}

public sealed class McpChangedEvent : EventMessage
{
    public string Type { get; init; } = "mcp.changed";
    public McpStatusDto Mcp { get; init; } = new();
}

public sealed class ChatRequestedEvent : EventMessage
{
    public string Type { get; init; } = "chat.requested";
    public string ItemId { get; init; } = "";
    public string Source { get; init; } = "";
    public string? ConversationId { get; init; }
    public string? Project { get; init; }
}
