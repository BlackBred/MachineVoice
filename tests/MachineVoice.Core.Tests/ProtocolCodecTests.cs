using MachineVoice.Protocol;

namespace MachineVoice.Core.Tests;

public class ProtocolCodecTests
{
    [Fact]
    public void Enums_UseStableWireNames()
    {
        var json = ProtocolCodec.Write(new PlayerStateEvent
        {
            State = PlayerState.Speaking,
            ItemId = "item",
        });

        Assert.Contains("\"type\":\"player.state\"", json);
        Assert.Contains("\"state\":\"speaking\"", json);
        Assert.DoesNotContain("Speaking", json);

        var mode = ProtocolCodec.Write(new SetModeCommand
        {
            Version = ProtocolVersion.Current,
            Id = "1",
            Mode = PlaybackMode.Confirm,
        });
        Assert.Contains("\"mode\":\"confirm\"", mode);

        var rate = ProtocolCodec.Write(new SetPlaybackRateCommand { Version = ProtocolVersion.Current, Id = "1", Rate = 1.5 });
        Assert.Contains("\"type\":\"setPlaybackRate\"", rate);
        Assert.Equal(1.5, Assert.IsType<SetPlaybackRateCommand>(ProtocolCodec.ReadClient(rate).Message).Rate);

        var seek = ProtocolCodec.Write(new SeekCommand { Version = ProtocolVersion.Current, Id = "1", Position = 12.5 });
        Assert.Contains("\"type\":\"seek\"", seek);
        Assert.Equal(12.5, Assert.IsType<SeekCommand>(ProtocolCodec.ReadClient(seek).Message).Position);

        var clipboard = ProtocolCodec.Write(new SpeakClipboardCommand { Version = ProtocolVersion.Current, Id = "1", Text = "из буфера" });
        Assert.Contains("\"type\":\"speakClipboard\"", clipboard);
        Assert.Equal("из буфера", Assert.IsType<SpeakClipboardCommand>(ProtocolCodec.ReadClient(clipboard).Message).Text);
        var position = ProtocolCodec.Write(new PlayerPositionEvent { ItemId = "i", Position = 1.5, Duration = 9 });
        Assert.Contains("\"type\":\"player.position\"", position);
        Assert.Equal(9, Assert.IsType<PlayerPositionEvent>(ProtocolCodec.ReadServer(position)).Duration);

        var legacy = ProtocolCodec.Write(new ResultMessage
        {
            Id = "1",
            Ok = true,
            Source = new SourceStatusDto
            {
                Name = "cursor",
                Status = SourceConnectionStatus.Connected,
                LegacySpeaker = true,
            },
        });
        Assert.Contains("\"legacySpeaker\":true", legacy);

        var omni = ProtocolCodec.Write(new ResultMessage
        {
            Id = "1",
            Ok = true,
            OmniVoice = new OmniVoiceStatusDto { Installed = true, ModelDownloaded = true },
        });
        Assert.Contains("\"installed\":true", omni);
        Assert.Contains("\"modelDownloaded\":true", omni);
        var omniStatus = Assert.IsType<ResultMessage>(ProtocolCodec.ReadServer(omni)).OmniVoice!;
        Assert.True(omniStatus.Installed);
        Assert.True(omniStatus.ModelDownloaded);
    }

    [Fact]
    public void UpdateSettings_CarriesSummarySettings()
    {
        var json = ProtocolCodec.Write(new UpdateSettingsCommand
        {
            Version = ProtocolVersion.Current,
            Id = "u",
            Summary = new SummarySettingsDto { Enabled = true, Model = "qwen3", TimeoutSeconds = 7 },
        });
        Assert.DoesNotContain("\"mode\"", json);

        var decoded = Assert.IsType<UpdateSettingsCommand>(ProtocolCodec.ReadClient(json).Message);
        Assert.Null(decoded.Mode);
        Assert.True(decoded.Summary!.Enabled);
        Assert.Equal("qwen3", decoded.Summary.Model);
        Assert.Equal(SummarySettingsDto.DefaultEndpoint, decoded.Summary.Endpoint);
        Assert.Equal(7, decoded.Summary.TimeoutSeconds);
        Assert.Equal(SummarySettingsDto.DefaultLengthLimit, decoded.Summary.LengthLimit);
        Assert.Equal(ReadingPreference.Summary, decoded.Summary.DefaultReading);
        Assert.Equal(SummarySettingsDto.DefaultSystemPrompt, decoded.Summary.SystemPrompt);

        var chosen = ProtocolCodec.Write(new UpdateSettingsCommand
        {
            Version = ProtocolVersion.Current,
            Id = "u2",
            Summary = new SummarySettingsDto { LengthLimit = 800, DefaultReading = ReadingPreference.Full },
        });
        var chosenSummary = Assert.IsType<UpdateSettingsCommand>(ProtocolCodec.ReadClient(chosen).Message).Summary!;
        Assert.Equal(800, chosenSummary.LengthLimit);
        Assert.Equal(ReadingPreference.Full, chosenSummary.DefaultReading);
    }

    [Fact]
    public void ProbeSummary_RoundTripsTheModelCheck()
    {
        var json = ProtocolCodec.Write(new ProbeSummaryCommand
        {
            Version = 1,
            Id = "ps",
            Endpoint = "http://localhost:11434/v1",
            Model = "qwen3:8b",
            ApiKey = "secret",
        });
        var command = Assert.IsType<ProbeSummaryCommand>(ProtocolCodec.ReadClient(json).Message);
        Assert.Equal("qwen3:8b", command.Model);
        Assert.Equal("secret", command.ApiKey);

        var result = ProtocolCodec.Write(new ResultMessage
        {
            Id = "ps",
            Ok = true,
            SummaryProbe = new SummaryProbeDto { Status = SummaryProbeStatus.Missing },
        });
        var decoded = Assert.IsType<ResultMessage>(ProtocolCodec.ReadServer(result));
        Assert.Equal(SummaryProbeStatus.Missing, decoded.SummaryProbe!.Status);
    }

    [Fact]
    public void RoundTrips_EveryCommandAndEvent()
    {
        ClientMessage[] commands =
        [
            new PauseCommand { Version = 1, Id = "p" },
            new ResumeCommand { Version = 1, Id = "r" },
            new SkipCommand { Version = 1, Id = "k" },
            new SetModeCommand { Version = 1, Id = "m", Mode = PlaybackMode.Silent },
            new SetPlaybackRateCommand { Version = 1, Id = "v", Rate = 1.25 },
            new SeekCommand { Version = 1, Id = "e", Position = 12.5 },
            new ListenCommand { Version = 1, Id = "l", ItemId = "item" },
            new SelectReadingCommand { Version = 1, Id = "s", ItemId = "item", ReadSummary = true },
            new SpeakClipboardCommand { Version = 1, Id = "b", Text = "из буфера" },
            new DismissCommand { Version = 1, Id = "d", ItemId = "item" },
            new OpenChatCommand { Version = 1, Id = "o", ItemId = "item" },
            new GetSettingsCommand { Version = 1, Id = "g" },
            new UpdateSettingsCommand { Version = 1, Id = "u", Mode = PlaybackMode.Auto },
            new ConnectSourceCommand { Version = 1, Id = "c", Source = "cursor" },
            new DisconnectSourceCommand { Version = 1, Id = "x", Source = "cursor" },
            new GetSourceStatusCommand { Version = 1, Id = "t", Source = "cursor" },
            new GetSnapshotCommand { Version = 1, Id = "n" },
            new GetOmniVoiceStatusCommand { Version = 1, Id = "os" },
            new InstallOmniVoiceCommand { Version = 1, Id = "oi" },
            new ProbeSummaryCommand { Version = 1, Id = "ps", Endpoint = "http://localhost:11434/v1", Model = "qwen3:8b" },
        ];

        foreach (var command in commands)
        {
            var decoded = ProtocolCodec.ReadClient(ProtocolCodec.Write(command));
            Assert.Null(decoded.Error);
            Assert.IsType(command.GetType(), decoded.Message);
            Assert.Equal(command.Id, decoded.Message!.Id);
        }

        var selected = Assert.IsType<SelectReadingCommand>(ProtocolCodec.ReadClient(ProtocolCodec.Write(
            new SelectReadingCommand { Version = 1, Id = "s2", ItemId = "item", ReadSummary = true })).Message);
        Assert.True(selected.ReadSummary);
        Assert.Equal("item", selected.ItemId);

        ServerMessage[] events =
        [
            new SnapshotEvent { Snapshot = new SnapshotDto { Player = PlayerState.Idle, Mode = PlaybackMode.Auto } },
            new PlayerStateEvent { State = PlayerState.Paused, ItemId = "item" },
            new PlayerProgressEvent { ItemId = "item", WordIndex = 2, Word = "word" },
            new PlayerPositionEvent { ItemId = "item", Position = 1.5, Duration = 9 },
            new QueueChangedEvent { Items = [new SpeechItemDto { Id = "item", GenerationId = "g", Source = "cursor", Text = "t" }] },
            new HistoryAppendedEvent
            {
                Entry = new HistoryEntryDto
                {
                    Item = new SpeechItemDto { Id = "item", GenerationId = "g", Source = "cursor", Text = "t" },
                    Outcome = SpeechOutcome.Spoken,
                },
            },
            new ConfirmationRequestedEvent { Item = new SpeechItemDto { Id = "item", GenerationId = "g", Source = "cursor", Text = "t" } },
            new ConfirmationClearedEvent { ItemId = "item" },
            new SettingsChangedEvent { Settings = new SettingsDto { Mode = PlaybackMode.Silent } },
            new SourceChangedEvent { Source = "cursor", Status = SourceConnectionStatus.Stale },
            new ChatRequestedEvent { ItemId = "item", Source = "cursor", ConversationId = "chat", Project = "MachineVoice" },
            new ResultMessage { Id = "1", Ok = false, Error = ProtocolErrors.InvalidState },
        ];

        foreach (var message in events)
        {
            var decoded = ProtocolCodec.ReadServer(ProtocolCodec.Write(message));
            Assert.IsType(message.GetType(), decoded);
        }
    }

    [Fact]
    public void UnknownCommand_PreservesId()
    {
        var decoded = ProtocolCodec.ReadClient("{\"version\":1,\"type\":\"submit\",\"id\":\"abc\"}");
        Assert.Null(decoded.Message);
        Assert.Equal("abc", decoded.Id);
        Assert.Equal(ProtocolErrors.UnknownCommand, decoded.Error);
    }
}
