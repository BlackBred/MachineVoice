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
    public QueueOrder Order { get; init; } = QueueOrder.Lifo;
    public List<SourceSettingDto> Sources { get; init; } = [];
    public SummarySettingsDto Summary { get; init; } = new();
    public TtsSettingsDto Tts { get; init; } = new();
}

public sealed class TtsSettingsDto
{
    public const double DefaultPlaybackRate = 1.0;
    public const double MinPlaybackRate = 0.5;
    public const double MaxPlaybackRate = 2.0;

    public TtsEngineKind Engine { get; init; } = TtsEngineKind.System;

    /// <summary>
    /// Playback speed of the synthesized audio, 1 plays it as is. Unlike the other settings it applies at once,
    /// to the response being read too. The pitch stays the same.
    /// </summary>
    public double PlaybackRate { get; init; } = DefaultPlaybackRate;

    public SystemVoiceSettingsDto SystemVoice { get; init; } = new();
    public QwenTtsSettingsDto Qwen { get; init; } = new();
}

/// <summary>The macOS voice (AVSpeechSynthesizer).</summary>
public sealed class SystemVoiceSettingsDto
{
    /// <summary>AVSpeechUtteranceDefaultSpeechRate.</summary>
    public const double DefaultRate = 0.5;

    /// <summary>
    /// Speech rate of the synthesizer, 0..1. It shortens pauses and sounds more natural than a faster playback,
    /// but applies from the next response.
    /// </summary>
    public double Rate { get; init; } = DefaultRate;
}

/// <summary>Qwen3-TTS through the OpenAI-compatible speech endpoint of mlx-audio (mlx_audio.server).</summary>
public sealed class QwenTtsSettingsDto
{
    public const string DefaultEndpoint = "http://127.0.0.1:8899/v1";
    public const string DefaultModel = "mlx-community/Qwen3-TTS-12Hz-1.7B-CustomVoice-bf16";
    public const string DefaultVoice = "Ryan";
    public const int DefaultUnloadAfterMinutes = 10;
    public const int MaxUnloadAfterMinutes = 24 * 60;

    /// <summary>Speakers of the CustomVoice models.</summary>
    public static IReadOnlyList<string> Voices { get; } =
        ["Ryan", "Aiden", "Dylan", "Eric", "Uncle_Fu", "Serena", "Vivian", "Ono_Anna", "Sohee"];

    /// <summary>Base URL; the request goes to {Endpoint}/audio/speech.</summary>
    public string Endpoint { get; init; } = DefaultEndpoint;

    public string Model { get; init; } = DefaultModel;
    public string Voice { get; init; } = DefaultVoice;

    /// <summary>
    /// A server that MachineVoice started is stopped after this long without speech, which frees the model's
    /// memory. 0 keeps it running.
    /// </summary>
    public int UnloadAfterMinutes { get; init; } = DefaultUnloadAfterMinutes;
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

/// <summary>The MachineVoice entry in ~/.cursor/mcp.json.</summary>
public sealed class McpStatusDto
{
    public SourceConnectionStatus Status { get; init; }

    /// <summary>
    /// The MCP server binary ships with this installation. Without it (a development build) connecting fails.
    /// </summary>
    public bool Available { get; init; }
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
