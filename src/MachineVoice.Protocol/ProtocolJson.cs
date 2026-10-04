using System.Text.Json.Serialization;

namespace MachineVoice.Protocol;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(PauseCommand))]
[JsonSerializable(typeof(ResumeCommand))]
[JsonSerializable(typeof(StopCommand))]
[JsonSerializable(typeof(SkipCommand))]
[JsonSerializable(typeof(SetModeCommand))]
[JsonSerializable(typeof(ListenCommand))]
[JsonSerializable(typeof(DismissCommand))]
[JsonSerializable(typeof(OpenChatCommand))]
[JsonSerializable(typeof(GetSettingsCommand))]
[JsonSerializable(typeof(UpdateSettingsCommand))]
[JsonSerializable(typeof(ConnectSourceCommand))]
[JsonSerializable(typeof(DisconnectSourceCommand))]
[JsonSerializable(typeof(GetSourceStatusCommand))]
[JsonSerializable(typeof(GetSnapshotCommand))]
[JsonSerializable(typeof(ResultMessage))]
[JsonSerializable(typeof(SnapshotEvent))]
[JsonSerializable(typeof(PlayerStateEvent))]
[JsonSerializable(typeof(PlayerProgressEvent))]
[JsonSerializable(typeof(QueueChangedEvent))]
[JsonSerializable(typeof(HistoryAppendedEvent))]
[JsonSerializable(typeof(ConfirmationRequestedEvent))]
[JsonSerializable(typeof(ConfirmationClearedEvent))]
[JsonSerializable(typeof(SettingsChangedEvent))]
[JsonSerializable(typeof(SourceChangedEvent))]
[JsonSerializable(typeof(ChatRequestedEvent))]
[JsonSerializable(typeof(SnapshotDto))]
[JsonSerializable(typeof(SettingsDto))]
[JsonSerializable(typeof(List<HistoryEntryDto>))]
[JsonSerializable(typeof(SpeechItemDto))]
[JsonSerializable(typeof(HistoryEntryDto))]
[JsonSerializable(typeof(SourceSettingDto))]
[JsonSerializable(typeof(SourceStatusDto))]
public partial class ProtocolJsonContext : JsonSerializerContext;
