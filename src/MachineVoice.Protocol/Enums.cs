using System.Text.Json.Serialization;

namespace MachineVoice.Protocol;

[JsonConverter(typeof(JsonStringEnumConverter<PlaybackMode>))]
public enum PlaybackMode
{
    [JsonStringEnumMemberName("auto")]
    Auto,

    [JsonStringEnumMemberName("confirm")]
    Confirm,

    [JsonStringEnumMemberName("silent")]
    Silent,
}

/// <summary>Which waiting response is read next. The one being read always finishes.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<QueueOrder>))]
public enum QueueOrder
{
    /// <summary>Newest first; in the confirm mode a new response takes the toast.</summary>
    [JsonStringEnumMemberName("lifo")]
    Lifo,

    /// <summary>In the order of arrival.</summary>
    [JsonStringEnumMemberName("fifo")]
    Fifo,
}

/// <summary>When the heading «Cursor, проект X.» is read before a response.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<HeadingMode>))]
public enum HeadingMode
{
    [JsonStringEnumMemberName("always")]
    Always,

    [JsonStringEnumMemberName("never")]
    Never,

    /// <summary>Only when the source or the project differs from the response that was read before.</summary>
    [JsonStringEnumMemberName("onChange")]
    OnChange,
}

[JsonConverter(typeof(JsonStringEnumConverter<PlayerState>))]
public enum PlayerState
{
    [JsonStringEnumMemberName("idle")]
    Idle,

    [JsonStringEnumMemberName("speaking")]
    Speaking,

    [JsonStringEnumMemberName("paused")]
    Paused,
}

[JsonConverter(typeof(JsonStringEnumConverter<SpeechOutcome>))]
public enum SpeechOutcome
{
    [JsonStringEnumMemberName("spoken")]
    Spoken,

    [JsonStringEnumMemberName("skipped")]
    Skipped,

    /// <summary>The TTS engine failed to speak it. Older history also has responses stopped by the user.</summary>
    [JsonStringEnumMemberName("stopped")]
    Stopped,
}

[JsonConverter(typeof(JsonStringEnumConverter<TtsEngineKind>))]
public enum TtsEngineKind
{
    /// <summary>AVSpeechSynthesizer with the system voices.</summary>
    [JsonStringEnumMemberName("system")]
    System,

    /// <summary>Qwen3-TTS served locally by mlx-audio.</summary>
    [JsonStringEnumMemberName("qwen")]
    Qwen,

    /// <summary>OmniVoice served locally by mlx-audio; the voice is cloned from a saved sample.</summary>
    [JsonStringEnumMemberName("omnivoice")]
    OmniVoice,
}

/// <summary>Which text is read when a response has both the original and a retelling.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ReadingPreference>))]
public enum ReadingPreference
{
    [JsonStringEnumMemberName("full")]
    Full,

    [JsonStringEnumMemberName("summary")]
    Summary,
}

/// <summary>What a probe of an OpenAI-compatible <c>/models</c> endpoint found.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SummaryProbeStatus>))]
public enum SummaryProbeStatus
{
    /// <summary>The endpoint lists this model.</summary>
    [JsonStringEnumMemberName("available")]
    Available,

    /// <summary>The endpoint answered, and no model name was given.</summary>
    [JsonStringEnumMemberName("reachable")]
    Reachable,

    /// <summary>The endpoint answered, and this model is not in its list.</summary>
    [JsonStringEnumMemberName("missing")]
    Missing,

    /// <summary>The endpoint rejected the API key.</summary>
    [JsonStringEnumMemberName("unauthorized")]
    Unauthorized,

    /// <summary>The endpoint did not answer with a model list.</summary>
    [JsonStringEnumMemberName("unreachable")]
    Unreachable,
}

[JsonConverter(typeof(JsonStringEnumConverter<SourceConnectionStatus>))]
public enum SourceConnectionStatus
{
    [JsonStringEnumMemberName("disconnected")]
    Disconnected,

    [JsonStringEnumMemberName("connected")]
    Connected,

    /// <summary>Hooks are present, but they no longer match this installation.</summary>
    [JsonStringEnumMemberName("stale")]
    Stale,
}
