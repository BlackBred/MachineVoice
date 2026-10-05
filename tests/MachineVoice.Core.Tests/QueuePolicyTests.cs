using MachineVoice.Core;
using MachineVoice.Protocol;

namespace MachineVoice.Core.Tests;

public class QueuePolicyTests
{
    [Fact]
    public void Fifo_Appends()
    {
        var queue = new List<SpeechItem>();
        var policy = new FifoQueuePolicy();
        policy.Enqueue(queue, Item("a"));
        policy.Enqueue(queue, Item("b"));
        policy.Enqueue(queue, Item("c"));

        Assert.Equal(["a", "b", "c"], queue.Select(item => item.GenerationId).ToArray());
    }

    [Fact]
    public void Lifo_PutsNewestFirst()
    {
        var queue = new List<SpeechItem>();
        var policy = new LifoQueuePolicy();
        policy.Enqueue(queue, Item("a"));
        policy.Enqueue(queue, Item("b"));
        policy.Enqueue(queue, Item("c"));

        Assert.Equal(["c", "b", "a"], queue.Select(item => item.GenerationId).ToArray());
    }

    [Fact(Timeout = 20000)]
    public async Task Lifo_IsDefault_AndDoesNotInterruptPlayback()
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
        Assert.Equal(QueueOrder.Lifo, snapshot.Snapshot!.Settings.Order);
        Assert.Equal(first.Id, snapshot.Snapshot.CurrentItemId);
        Assert.Equal(first.Id, tts.UtteranceId);
        Assert.Equal(["c", "b"], snapshot.Snapshot.Queue.Select(item => item.GenerationId).ToArray());

        tts.Complete();
        await log.TakeAsync<PlayerStateEvent>(state => state.State == PlayerState.Speaking && state.ItemId == third.Id);
        tts.Complete();
        await log.TakeAsync<PlayerStateEvent>(state => state.State == PlayerState.Speaking && state.ItemId == second.Id);
        tts.Complete();
        await log.TakeAsync<HistoryAppendedEvent>(entry => entry.Entry.Item.Id == second.Id);

        var done = await client.GetSnapshotAsync();
        Assert.Equal(["a", "c", "b"], done.Snapshot!.History.Select(entry => entry.Item.GenerationId).ToArray());
        await host.DisposeAsync();
    }

    [Fact(Timeout = 20000)]
    public async Task Lifo_NewResponseTakesTheToast()
    {
        await using var root = new TempRoot();
        var tts = new ManualTtsEngine();
        var host = await TestHost.StartAsync(root.Path, tts);
        await using var client = await host.ConnectInProcessAsync();
        var log = EventLog.Pump(client);
        Assert.True((await client.SetModeAsync(PlaybackMode.Confirm)).Ok);

        var first = await IngestClient.SubmitAsync(host.IngestSocketPath, Draft("a"));
        await log.TakeAsync<ConfirmationRequestedEvent>(confirmation => confirmation.Item.Id == first.Id);

        var second = await IngestClient.SubmitAsync(host.IngestSocketPath, Draft("b"));
        Assert.Equal(first.Id, (await log.TakeAsync<ConfirmationClearedEvent>()).ItemId);
        await log.TakeAsync<ConfirmationRequestedEvent>(confirmation => confirmation.Item.Id == second.Id);

        var snapshot = await client.GetSnapshotAsync();
        Assert.Equal(second.Id, snapshot.Snapshot!.Confirmation!.Id);
        Assert.Equal(["b", "a"], snapshot.Snapshot.Queue.Select(item => item.GenerationId).ToArray());
        Assert.Equal(ProtocolErrors.NotFound, (await client.ListenAsync(first.Id!)).Error);

        Assert.True((await client.DismissAsync(second.Id!)).Ok);
        await log.TakeAsync<ConfirmationRequestedEvent>(confirmation => confirmation.Item.Id == first.Id);
        Assert.Null(tts.UtteranceId);
        await host.DisposeAsync();
    }

    [Fact(Timeout = 20000)]
    public async Task Fifo_KeepsTheToast()
    {
        await using var root = new TempRoot();
        var tts = new ManualTtsEngine();
        var host = await TestHost.StartAsync(root.Path, tts);
        await using var client = await host.ConnectInProcessAsync();
        var log = EventLog.Pump(client);
        Assert.True((await client.UpdateSettingsAsync(PlaybackMode.Confirm, order: QueueOrder.Fifo)).Ok);

        var first = await IngestClient.SubmitAsync(host.IngestSocketPath, Draft("a"));
        await log.TakeAsync<ConfirmationRequestedEvent>(confirmation => confirmation.Item.Id == first.Id);
        await IngestClient.SubmitAsync(host.IngestSocketPath, Draft("b"));

        var snapshot = await client.GetSnapshotAsync();
        Assert.Equal(first.Id, snapshot.Snapshot!.Confirmation!.Id);
        Assert.Equal(["a", "b"], snapshot.Snapshot.Queue.Select(item => item.GenerationId).ToArray());
        await host.DisposeAsync();
    }

    [Fact(Timeout = 20000)]
    public async Task SwitchingOrder_ReordersWaitingResponses()
    {
        await using var root = new TempRoot();
        var tts = new ManualTtsEngine();
        var host = await TestHost.StartAsync(root.Path, tts);
        await using var client = await host.ConnectInProcessAsync();
        var log = EventLog.Pump(client);
        Assert.True((await client.SetModeAsync(PlaybackMode.Silent)).Ok);

        var first = await IngestClient.SubmitAsync(host.IngestSocketPath, Draft("a"));
        await IngestClient.SubmitAsync(host.IngestSocketPath, Draft("b"));
        var third = await IngestClient.SubmitAsync(host.IngestSocketPath, Draft("c"));
        Assert.Equal(["c", "b", "a"], await QueueAsync(client));

        Assert.True((await client.UpdateSettingsAsync(order: QueueOrder.Fifo)).Ok);
        Assert.Equal(["a", "b", "c"], await QueueAsync(client));

        Assert.True((await client.SetModeAsync(PlaybackMode.Confirm)).Ok);
        await log.TakeAsync<ConfirmationRequestedEvent>(confirmation => confirmation.Item.Id == first.Id);

        Assert.True((await client.UpdateSettingsAsync(order: QueueOrder.Lifo)).Ok);
        Assert.Equal(["c", "b", "a"], await QueueAsync(client));
        Assert.Equal(first.Id, (await log.TakeAsync<ConfirmationClearedEvent>()).ItemId);
        await log.TakeAsync<ConfirmationRequestedEvent>(confirmation => confirmation.Item.Id == third.Id);
        Assert.Null(tts.UtteranceId);
        await host.DisposeAsync();
    }

    static async Task<string[]> QueueAsync(IControlClient client) =>
        (await client.GetSnapshotAsync()).Snapshot!.Queue.Select(item => item.GenerationId).ToArray();

    static SpeechDraft Draft(string generationId) => new("cursor", generationId, generationId);

    static SpeechItem Item(string generationId) => new()
    {
        Id = generationId,
        Source = "cursor",
        GenerationId = generationId,
        Text = generationId,
        ReceivedAt = DateTimeOffset.UnixEpoch,
    };
}
