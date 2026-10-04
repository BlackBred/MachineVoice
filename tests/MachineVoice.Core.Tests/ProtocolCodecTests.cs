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
    }

    [Fact]
    public void RoundTrips_EveryCommandAndEvent()
    {
        ClientMessage[] commands =
        [
            new PauseCommand { Version = 1, Id = "p" },
            new ResumeCommand { Version = 1, Id = "r" },
            new StopCommand { Version = 1, Id = "s" },
            new SkipCommand { Version = 1, Id = "k" },
            new SetModeCommand { Version = 1, Id = "m", Mode = PlaybackMode.Silent },
            new ListenCommand { Version = 1, Id = "l", ItemId = "item" },
            new DismissCommand { Version = 1, Id = "d", ItemId = "item" },
            new OpenChatCommand { Version = 1, Id = "o", ItemId = "item" },
            new GetSettingsCommand { Version = 1, Id = "g" },
            new UpdateSettingsCommand { Version = 1, Id = "u", Mode = PlaybackMode.Auto },
            new ConnectSourceCommand { Version = 1, Id = "c", Source = "cursor" },
            new DisconnectSourceCommand { Version = 1, Id = "x", Source = "cursor" },
            new GetSourceStatusCommand { Version = 1, Id = "t", Source = "cursor" },
            new GetSnapshotCommand { Version = 1, Id = "n" },
        ];

        foreach (var command in commands)
        {
            var decoded = ProtocolCodec.ReadClient(ProtocolCodec.Write(command));
            Assert.Null(decoded.Error);
            Assert.IsType(command.GetType(), decoded.Message);
            Assert.Equal(command.Id, decoded.Message!.Id);
        }

        ServerMessage[] events =
        [
            new SnapshotEvent { Snapshot = new SnapshotDto { Player = PlayerState.Idle, Mode = PlaybackMode.Auto } },
            new PlayerStateEvent { State = PlayerState.Paused, ItemId = "item" },
            new PlayerProgressEvent { ItemId = "item", WordIndex = 2, Word = "word" },
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
