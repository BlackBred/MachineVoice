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
    public HeadingMode Heading { get; init; } = HeadingMode.Always;
    public List<SourceSettingDto> Sources { get; init; } = [];
    public SummarySettingsDto Summary { get; init; } = new();
    public TtsSettingsDto Tts { get; init; } = new();
    public HotkeySettingsDto Hotkeys { get; init; } = new();
}

/// <summary>The clipboard hotkey and whether that reading cuts off whatever is playing.</summary>
public sealed class HotkeySettingsDto
{
    public bool Interrupt { get; init; }
    public HotkeyChordDto Clipboard { get; init; } = new();
}

/// <summary>A Carbon hotkey: macOS virtual key code and modifier bits.</summary>
public sealed class HotkeyChordDto
{
    /// <summary>kVK_ANSI_V.</summary>
    public const int DefaultKeyCode = 0x09;

    /// <summary>Control and Option.</summary>
    public const int DefaultModifiers = 0x1000 | 0x0800;

    public int KeyCode { get; init; } = DefaultKeyCode;
    public int Modifiers { get; init; } = DefaultModifiers;
}

public static class HotkeyRules
{
    public const int KnownModifiers = 0x0100 | 0x0200 | 0x0800 | 0x1000;

    public static HotkeySettingsDto Canonical(HotkeySettingsDto? settings)
    {
        var chord = Valid(settings?.Clipboard) ? settings!.Clipboard : new HotkeyChordDto();
        return new HotkeySettingsDto
        {
            Interrupt = settings?.Interrupt ?? false,
            Clipboard = new HotkeyChordDto { KeyCode = chord.KeyCode, Modifiers = chord.Modifiers },
        };
    }

    public static bool Valid(HotkeyChordDto? chord) =>
        chord is { KeyCode: >= 0 and <= 0x7F, Modifiers: > 0 }
        && (chord.Modifiers & ~KnownModifiers) == 0;
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
    public OmniVoiceTtsSettingsDto OmniVoice { get; init; } = new();
}

/// <summary>A model behind the OpenAI-compatible speech endpoint of mlx-audio (mlx_audio.server).</summary>
public interface IMlxModelSettings
{
    /// <summary>Base URL; the request goes to {Endpoint}/audio/speech.</summary>
    string Endpoint { get; }

    string Model { get; }

    /// <summary>
    /// A server that MachineVoice started is stopped after this long without speech, which frees the model's
    /// memory. 0 keeps it running.
    /// </summary>
    int UnloadAfterMinutes { get; }
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
public sealed class QwenTtsSettingsDto : IMlxModelSettings
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
    public int UnloadAfterMinutes { get; init; } = DefaultUnloadAfterMinutes;
}

/// <summary>
/// OmniVoice through mlx-audio. It has no built-in speakers: without a sample every request picks a new random
/// voice, so a voice is a saved sample (see <see cref="VoiceDto"/>) that each request clones.
/// </summary>
public sealed class OmniVoiceTtsSettingsDto : IMlxModelSettings
{
    public const string DefaultModel = "mlx-community/OmniVoice-bfloat16";
    public const string DefaultLanguage = "ru";

    public string Endpoint { get; init; } = QwenTtsSettingsDto.DefaultEndpoint;
    public string Model { get; init; } = DefaultModel;

    /// <summary>Id of a saved voice; empty picks a random voice for every sentence.</summary>
    public string Voice { get; init; } = "";

    /// <summary>Language tag of the text, such as ru or en.</summary>
    public string Language { get; init; } = DefaultLanguage;

    public int UnloadAfterMinutes { get; init; } = QwenTtsSettingsDto.DefaultUnloadAfterMinutes;
}

/// <summary>A voice sample: a short clip and its transcript, which OmniVoice clones.</summary>
public sealed class VoiceDto
{
    public string Id { get; init; } = "";

    /// <summary>Empty for a draft that is not saved yet.</summary>
    public string Name { get; init; } = "";

    /// <summary>The WAV file of the sample, on the machine that runs MachineVoice.</summary>
    public string AudioPath { get; init; } = "";

    public string Text { get; init; } = "";

    /// <summary>Ships with MachineVoice: it cannot be deleted.</summary>
    public bool BuiltIn { get; init; }
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

/// <summary>Whether OmniVoice's server is installed and its weights are already in the local cache.</summary>
public sealed class OmniVoiceStatusDto
{
    /// <summary>mlx_audio.server is on disk.</summary>
    public bool Installed { get; init; }

    /// <summary>The model weights are in the Hugging Face cache, so nothing has to be downloaded.</summary>
    public bool ModelDownloaded { get; init; }
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
