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

    [JsonStringEnumMemberName("stopped")]
    Stopped,
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
