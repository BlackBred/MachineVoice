using System.Text.Json.Serialization;

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

    /// <summary>Text after the text pipeline, as it goes to TTS. Null until the item is prepared.</summary>
    public string? Speech { get; init; }

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
    public SummarySettingsDto Summary { get; init; } = new();
}

/// <summary>Optional LLM retelling through an OpenAI-compatible endpoint (Ollama works too).</summary>
public sealed class SummarySettingsDto
{
    public const string DefaultEndpoint = "http://localhost:11434/v1";
    public const int DefaultTimeoutSeconds = 20;
    public const int MaxTimeoutSeconds = 300;

    public bool Enabled { get; init; }

    /// <summary>Base URL; the request goes to {Endpoint}/chat/completions.</summary>
    public string Endpoint { get; init; } = DefaultEndpoint;

    public string Model { get; init; } = "";
    public string? ApiKey { get; init; }

    /// <summary>When the model does not answer in time, the rules-only text is spoken.</summary>
    public int TimeoutSeconds { get; init; } = DefaultTimeoutSeconds;
}

public sealed class SourceStatusDto
{
    public string Name { get; init; } = "";
    public SourceConnectionStatus Status { get; init; }

    /// <summary>An older speak.sh hook is still registered and would read the same responses.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool LegacySpeaker { get; init; }
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
