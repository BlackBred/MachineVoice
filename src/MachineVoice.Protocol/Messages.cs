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

public sealed class SkipCommand : ClientMessage
{
    public string Type { get; init; } = "skip";
}

public sealed class SetModeCommand : ClientMessage
{
    public string Type { get; init; } = "setMode";
    public PlaybackMode? Mode { get; init; }
}

/// <summary>Sets <see cref="TtsSettingsDto.PlaybackRate"/>; the response being read speeds up or slows down at once.</summary>
public sealed class SetPlaybackRateCommand : ClientMessage
{
    public string Type { get; init; } = "setPlaybackRate";
    public double? Rate { get; init; }
}

/// <summary>Moves the response being read to <see cref="Position"/> seconds of its audio; a pause stays a pause.</summary>
public sealed class SeekCommand : ClientMessage
{
    public string Type { get; init; } = "seek";
    public double? Position { get; init; }
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

    /// <summary>Reorders the waiting responses at once; the one being read finishes.</summary>
    public QueueOrder? Order { get; init; }

    /// <summary>Applies from the next response; the one being read keeps its heading.</summary>
    public HeadingMode? Heading { get; init; }

    public SummarySettingsDto? Summary { get; init; }
    public TtsSettingsDto? Tts { get; init; }
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

/// <summary>The saved OmniVoice voices, in <see cref="ResultMessage.Voices"/>.</summary>
public sealed class ListVoicesCommand : ClientMessage
{
    public string Type { get; init; } = "listVoices";
}

/// <summary>
/// Synthesizes a sample in a new random voice with the OmniVoice settings, starting the server and loading the
/// model if needed: the first time this takes minutes. The draft comes in <see cref="ResultMessage.Voice"/>.
/// </summary>
public sealed class CreateVoiceCommand : ClientMessage
{
    public string Type { get; init; } = "createVoice";
}

/// <summary>Keeps a draft as a voice; the result lists the voices.</summary>
public sealed class SaveVoiceCommand : ClientMessage
{
    public string Type { get; init; } = "saveVoice";
    public string DraftId { get; init; } = "";
    public string Name { get; init; } = "";
}

/// <summary>Deletes a saved voice; when OmniVoice used it, it falls back to a random voice.</summary>
public sealed class DeleteVoiceCommand : ClientMessage
{
    public string Type { get; init; } = "deleteVoice";
    public string VoiceId { get; init; } = "";
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
    public List<VoiceDto>? Voices { get; init; }
    public VoiceDto? Voice { get; init; }

    /// <summary>Why the command failed, for a person to read; set when the cause is outside MachineVoice.</summary>
    public string? Detail { get; init; }
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

/// <summary>
/// Where the audio of the response is, in seconds of audio (the playback rate does not change them). The duration
/// includes an estimate for the part that is not synthesized yet, so it may change while the response plays.
/// A duration of 0 means the engine cannot tell (the plain system voice); then the progress comes from the words.
/// </summary>
public sealed class PlayerPositionEvent : EventMessage
{
    public string Type { get; init; } = "player.position";
    public string ItemId { get; init; } = "";
    public double Position { get; init; }
    public double Duration { get; init; }
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
