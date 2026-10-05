using MachineVoice.Core;
using MachineVoice.Protocol;

namespace MachineVoice.Core.Tests;

public class ControlSocketTests
{
    [Fact(Timeout = 20000)]
    public async Task AllCommandsAndEvents_WorkOverUnixSocket()
    {
        await using var root = new TempRoot();
        var tts = new ManualTtsEngine();
        var host = await TestHost.StartAsync(root.Path, tts);
        await using var client = await SocketControlClient.ConnectAsync(host.ControlSocketPath);
        var log = EventLog.Pump(client);

        var snapshot = await log.TakeAsync<SnapshotEvent>();
        Assert.Equal(PlayerState.Idle, snapshot.Snapshot.Player);
        Assert.Equal(PlaybackMode.Auto, snapshot.Snapshot.Mode);
        Assert.Empty(snapshot.Snapshot.Queue);

        var idlePause = await client.PauseAsync();
        Assert.False(idlePause.Ok);
        Assert.Equal(ProtocolErrors.InvalidState, idlePause.Error);
        var missing = await client.ListenAsync("missing");
        Assert.Equal(ProtocolErrors.NotFound, missing.Error);

        var first = await IngestClient.SubmitAsync(host.IngestSocketPath, new SpeechDraft(
            "cursor", "g1", "alpha beta", Project: "MachineVoice", ConversationId: "chat-1", Topic: "topic"));
        Assert.False(first.Duplicate);
        await log.TakeAsync<QueueChangedEvent>();
        var speaking = await log.TakeAsync<PlayerStateEvent>(state => state.State == PlayerState.Speaking);
        Assert.Equal(first.Id, speaking.ItemId);

        tts.Emit(0, "alpha");
        var progress = await log.TakeAsync<PlayerProgressEvent>();
        Assert.Equal(first.Id, progress.ItemId);
        Assert.Equal(0, progress.WordIndex);
        Assert.Equal("alpha", progress.Word);

        Assert.True((await client.PauseAsync()).Ok);
        await log.TakeAsync<PlayerStateEvent>(state => state.State == PlayerState.Paused && state.ItemId == first.Id);
        Assert.True((await client.ResumeAsync()).Ok);
        await log.TakeAsync<PlayerStateEvent>(state => state.State == PlayerState.Speaking && state.ItemId == first.Id);

        tts.Complete();
        Assert.Equal(first.Id, (await log.TakeAsync<HistoryAppendedEvent>(entry => entry.Entry.Outcome == SpeechOutcome.Spoken)).Entry.Item.Id);
        await log.TakeAsync<PlayerStateEvent>(state => state.State == PlayerState.Idle);

        var chat = await client.OpenChatAsync(first.Id!);
        Assert.True(chat.Ok);
        var requested = await log.TakeAsync<ChatRequestedEvent>();
        Assert.Equal(first.Id, requested.ItemId);
        Assert.Equal("cursor", requested.Source);
        Assert.Equal("chat-1", requested.ConversationId);
        Assert.Equal("MachineVoice", requested.Project);

        var second = await IngestClient.SubmitAsync(host.IngestSocketPath, new SpeechDraft("cursor", "g2", "second"));
        await log.TakeAsync<PlayerStateEvent>(state => state.State == PlayerState.Speaking && state.ItemId == second.Id);
        var third = await IngestClient.SubmitAsync(host.IngestSocketPath, new SpeechDraft("cursor", "g3", "third"));
        await log.TakeAsync<QueueChangedEvent>(queue => queue.Items.Any(item => item.Id == third.Id));
        var failing = await IngestClient.SubmitAsync(host.IngestSocketPath, new SpeechDraft("cursor", "g3b", "fails"));
        await log.TakeAsync<QueueChangedEvent>(queue => queue.Items.Any(item => item.Id == failing.Id));

        tts.FailNextSpeak = true;
        tts.Complete();
        Assert.Equal(failing.Id, (await log.TakeAsync<HistoryAppendedEvent>(entry => entry.Entry.Outcome == SpeechOutcome.Stopped)).Entry.Item.Id);
        await log.TakeAsync<PlayerStateEvent>(state => state.State == PlayerState.Idle);
        var held = await client.GetSnapshotAsync();
        Assert.True(held.Snapshot!.Holding);
        Assert.Equal(third.Id, Assert.Single(held.Snapshot.Queue).Id);

        Assert.True((await client.ResumeAsync()).Ok);
        await log.TakeAsync<PlayerStateEvent>(state => state.State == PlayerState.Speaking && state.ItemId == third.Id);
        Assert.True((await client.SkipAsync()).Ok);
        Assert.Equal(third.Id, (await log.TakeAsync<HistoryAppendedEvent>(entry => entry.Entry.Outcome == SpeechOutcome.Skipped)).Entry.Item.Id);
        await log.TakeAsync<PlayerStateEvent>(state => state.State == PlayerState.Idle);

        Assert.True((await client.SetModeAsync(PlaybackMode.Confirm)).Ok);
        Assert.Equal(PlaybackMode.Confirm, (await log.TakeAsync<SettingsChangedEvent>(settings => settings.Settings.Mode == PlaybackMode.Confirm)).Settings.Mode);

        var fourth = await IngestClient.SubmitAsync(host.IngestSocketPath, new SpeechDraft("cursor", "g4", "confirm me", ConversationId: "chat-4"));
        var confirmation = await log.TakeAsync<ConfirmationRequestedEvent>();
        Assert.Equal(fourth.Id, confirmation.Item.Id);
        Assert.Null(tts.UtteranceId);

        Assert.True((await client.ListenAsync(fourth.Id!)).Ok);
        Assert.Equal(fourth.Id, (await log.TakeAsync<ConfirmationClearedEvent>()).ItemId);
        await log.TakeAsync<PlayerStateEvent>(state => state.State == PlayerState.Speaking && state.ItemId == fourth.Id);
        tts.Complete();
        await log.TakeAsync<HistoryAppendedEvent>(entry => entry.Entry.Item.Id == fourth.Id && entry.Entry.Outcome == SpeechOutcome.Spoken);
        await log.TakeAsync<PlayerStateEvent>(state => state.State == PlayerState.Idle);

        var fifth = await IngestClient.SubmitAsync(host.IngestSocketPath, new SpeechDraft("cursor", "g5", "dismiss me"));
        await log.TakeAsync<ConfirmationRequestedEvent>(item => item.Item.Id == fifth.Id);
        Assert.True((await client.DismissAsync(fifth.Id!)).Ok);
        Assert.Equal(fifth.Id, (await log.TakeAsync<ConfirmationClearedEvent>()).ItemId);
        Assert.Equal(fifth.Id, (await log.TakeAsync<HistoryAppendedEvent>(entry => entry.Entry.Outcome == SpeechOutcome.Skipped && entry.Entry.Item.Id == fifth.Id)).Entry.Item.Id);

        Assert.True((await client.SetModeAsync(PlaybackMode.Silent)).Ok);
        await log.TakeAsync<SettingsChangedEvent>(settings => settings.Settings.Mode == PlaybackMode.Silent);
        var sixth = await IngestClient.SubmitAsync(host.IngestSocketPath, new SpeechDraft("cursor", "g6", "quiet"));
        await log.TakeAsync<QueueChangedEvent>(queue => queue.Items.Any(item => item.Id == sixth.Id));
        Assert.Null(tts.UtteranceId);
        var quiet = await client.GetSnapshotAsync();
        Assert.Equal(PlayerState.Idle, quiet.Snapshot!.Player);
        Assert.Contains(quiet.Snapshot.Queue, item => item.Id == sixth.Id);

        var connected = await client.ConnectSourceAsync("cursor");
        Assert.Equal(SourceConnectionStatus.Connected, connected.Source!.Status);
        await log.TakeAsync<SourceChangedEvent>(source => source.Source == "cursor" && source.Status == SourceConnectionStatus.Connected);
        var status = await client.GetSourceStatusAsync("cursor");
        Assert.Equal(SourceConnectionStatus.Connected, status.Source!.Status);

        var disconnected = await client.DisconnectSourceAsync("cursor");
        Assert.Equal(SourceConnectionStatus.Disconnected, disconnected.Source!.Status);
        await log.TakeAsync<SourceChangedEvent>(source => source.Source == "cursor" && source.Status == SourceConnectionStatus.Disconnected);

        Assert.True((await client.UpdateSettingsAsync(PlaybackMode.Auto)).Ok);
        await log.TakeAsync<SettingsChangedEvent>(settings => settings.Settings.Mode == PlaybackMode.Auto);
        await log.TakeAsync<PlayerStateEvent>(state => state.State == PlayerState.Speaking && state.ItemId == sixth.Id);

        var settings = await client.GetSettingsAsync();
        Assert.True(settings.Ok);
        Assert.Equal(PlaybackMode.Auto, settings.Settings!.Mode);
        Assert.Contains(settings.Settings.Sources, source => source.Name == "cursor" && !source.Enabled);

        await host.DisposeAsync();
    }
}
