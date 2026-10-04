namespace MachineVoice.Protocol;

public sealed class SpeechItemDto
{
    public string Id { get; init; } = "";
    public string Source { get; init; } = "";
    public string? Project { get; init; }
    public string? ConversationId { get; init; }
    public string GenerationId { get; init; } = "";
    public string? Topic { get; init; }
    public string Text { get; init; } = "";
    public DateTimeOffset ReceivedAt { get; init; }
}

public sealed class HistoryEntryDto
{
    public SpeechItemDto Item { get; init; } = new();
    public SpeechOutcome Outcome { get; init; }
    public DateTimeOffset FinishedAt { get; init; }
}

public sealed class SourceSettingDto
{
    public string Name { get; init; } = "";
    public bool Enabled { get; init; }
}

public sealed class SettingsDto
{
    public PlaybackMode Mode { get; init; } = PlaybackMode.Auto;
    public List<SourceSettingDto> Sources { get; init; } = [];
}

public sealed class SourceStatusDto
{
    public string Name { get; init; } = "";
    public SourceConnectionStatus Status { get; init; }
}

public sealed class SnapshotDto
{
    public PlayerState Player { get; init; }
    public string? CurrentItemId { get; init; }
    public SpeechItemDto? Current { get; init; }
    public PlaybackMode Mode { get; init; }
    public bool Holding { get; init; }
    public SpeechItemDto? Confirmation { get; init; }
    public List<SpeechItemDto> Queue { get; init; } = [];
    public List<HistoryEntryDto> History { get; init; } = [];
    public SettingsDto Settings { get; init; } = new();
    public List<SourceStatusDto> Sources { get; init; } = [];
}
