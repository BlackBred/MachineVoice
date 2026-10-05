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
