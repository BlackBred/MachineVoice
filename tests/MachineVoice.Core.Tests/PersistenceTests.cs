using MachineVoice.Core;
using MachineVoice.Protocol;

namespace MachineVoice.Core.Tests;

public class PersistenceTests
{
    [Fact(Timeout = 20000)]
    public async Task Inbox_SurvivesRestart_AndHistoryDedups()
    {
        await using var root = new TempRoot();
        var firstTts = new ManualTtsEngine();
        var first = await TestHost.StartAsync(root.Path, firstTts);
        var accepted = await IngestClient.SubmitAsync(first.IngestSocketPath, new SpeechDraft(
            "cursor", "gen-1", "привет", Project: "MachineVoice", ConversationId: "chat", Topic: "тема"));
        Assert.Equal("accepted", accepted.Type);
        Assert.False(accepted.Duplicate);
        Assert.Equal("привет", firstTts.Text);
        Assert.Single(Directory.GetFiles(Inbox(root.Path)));
        await first.DisposeAsync();
        Assert.Single(Directory.GetFiles(Inbox(root.Path)));

        var secondTts = new ManualTtsEngine();
        var second = await TestHost.StartAsync(root.Path, secondTts);
        Assert.Equal(accepted.Id, secondTts.UtteranceId);
        Assert.Equal("привет", secondTts.Text);
        await using var client = await second.ConnectInProcessAsync();
        var log = EventLog.Pump(client);
        var snapshot = await log.TakeAsync<SnapshotEvent>();
        Assert.Equal(accepted.Id, snapshot.Snapshot.CurrentItemId);
        Assert.Equal("gen-1", snapshot.Snapshot.Current!.GenerationId);

        secondTts.Complete();
        var spoken = await log.TakeAsync<HistoryAppendedEvent>();
        Assert.Equal(SpeechOutcome.Spoken, spoken.Entry.Outcome);
        Assert.Equal("тема", spoken.Entry.Item.Topic);
        Assert.Empty(Directory.GetFiles(Inbox(root.Path)));
        await client.DisposeAsync();
        await second.DisposeAsync();

        var thirdTts = new ManualTtsEngine();
        var third = await TestHost.StartAsync(root.Path, thirdTts);
        var duplicate = await IngestClient.SubmitAsync(third.IngestSocketPath, new SpeechDraft("cursor", "gen-1", "привет"));
        Assert.True(duplicate.Duplicate);
        Assert.Equal(accepted.Id, duplicate.Id);
        Assert.Null(thirdTts.UtteranceId);
        await using var after = await third.ConnectInProcessAsync();
        var afterLog = EventLog.Pump(after);
        var restored = await afterLog.TakeAsync<SnapshotEvent>();
        Assert.Equal(PlayerState.Idle, restored.Snapshot.Player);
        Assert.Single(restored.Snapshot.History);
        Assert.Empty(restored.Snapshot.Queue);
        await third.DisposeAsync();
    }

    [Fact(Timeout = 20000)]
    public async Task Settings_SurviveRestart()
    {
        await using var root = new TempRoot();
        var tts = new ManualTtsEngine();
        var host = await TestHost.StartAsync(root.Path, tts);
        await using var client = await host.ConnectInProcessAsync();
        var updated = await client.UpdateSettingsAsync(PlaybackMode.Silent);
        Assert.True(updated.Ok);
        var connected = await client.ConnectSourceAsync("cursor");
        Assert.Equal(SourceConnectionStatus.Connected, connected.Source!.Status);
        await client.DisposeAsync();
        await host.DisposeAsync();

        var restarted = await TestHost.StartAsync(root.Path, new ManualTtsEngine());
        await using var again = await restarted.ConnectInProcessAsync();
        var settings = await again.GetSettingsAsync();
        Assert.Equal(PlaybackMode.Silent, settings.Settings!.Mode);
        Assert.Contains(settings.Settings.Sources, source => source.Name == "cursor" && source.Enabled);
        var status = await again.GetSourceStatusAsync("cursor");
        Assert.Equal(SourceConnectionStatus.Connected, status.Source!.Status);
        await restarted.DisposeAsync();
    }

    [Fact(Timeout = 20000)]
    public async Task CorruptInboxFile_IsQuarantined()
    {
        await using var root = new TempRoot();
        var inbox = Inbox(root.Path);
        Directory.CreateDirectory(inbox);
        await File.WriteAllTextAsync(Path.Combine(inbox, "000-bad.json"), "{");

        var host = await TestHost.StartAsync(root.Path, new ManualTtsEngine());
        Assert.Empty(Directory.GetFiles(inbox));
        Assert.Single(Directory.GetFiles(Path.Combine(root.Path, "rejected")));
        await host.DisposeAsync();
    }

    [Fact(Timeout = 20000)]
    public async Task Fifo_PlaysInArrivalOrder()
    {
        await using var root = new TempRoot();
        var tts = new ManualTtsEngine();
        var host = await TestHost.StartAsync(root.Path, tts);
        await using var client = await host.ConnectInProcessAsync();
        var log = EventLog.Pump(client);

        var first = await IngestClient.SubmitAsync(host.IngestSocketPath, Draft("a"));
        var second = await IngestClient.SubmitAsync(host.IngestSocketPath, Draft("b"));
        var third = await IngestClient.SubmitAsync(host.IngestSocketPath, Draft("c"));

        var snapshot = await client.GetSnapshotAsync();
        Assert.Equal(first.Id, snapshot.Snapshot!.CurrentItemId);
        Assert.Equal(["b", "c"], snapshot.Snapshot.Queue.Select(item => item.GenerationId).ToArray());

        tts.Complete();
        await log.TakeAsync<PlayerStateEvent>(state => state.State == PlayerState.Speaking && state.ItemId == second.Id);
        tts.Complete();
        await log.TakeAsync<PlayerStateEvent>(state => state.State == PlayerState.Speaking && state.ItemId == third.Id);
        tts.Complete();
        await log.TakeAsync<PlayerStateEvent>(state => state.State == PlayerState.Idle);

        var done = await client.GetSnapshotAsync();
        Assert.Equal(["a", "b", "c"], done.Snapshot!.History.Select(entry => entry.Item.GenerationId).ToArray());
        Assert.All(done.Snapshot.History, entry => Assert.Equal(SpeechOutcome.Spoken, entry.Outcome));
        await host.DisposeAsync();
    }

    static SpeechDraft Draft(string generationId) => new("cursor", generationId, generationId);

    static string Inbox(string root) => Path.Combine(root, "inbox");
}
