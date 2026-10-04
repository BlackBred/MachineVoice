using MachineVoice.Core;
using MachineVoice.Protocol;

namespace MachineVoice.Core.Tests;

public class InProcessClientTests
{
    [Fact(Timeout = 20000)]
    public async Task InProcessClient_ReceivesTheSamePlayerEvents()
    {
        await using var root = new TempRoot();
        var tts = new ManualTtsEngine();
        var host = await TestHost.StartAsync(root.Path, tts);
        await using var client = await host.ConnectInProcessAsync();
        var log = EventLog.Pump(client);

        var snapshot = await log.TakeAsync<SnapshotEvent>();
        Assert.Equal(PlayerState.Idle, snapshot.Snapshot.Player);

        var accepted = await IngestClient.SubmitAsync(host.IngestSocketPath, new SpeechDraft("cursor", "g", "hello"));
        var speaking = await log.TakeAsync<PlayerStateEvent>(state => state.State == PlayerState.Speaking);
        Assert.Equal(accepted.Id, speaking.ItemId);

        Assert.True((await client.PauseAsync()).Ok);
        await log.TakeAsync<PlayerStateEvent>(state => state.State == PlayerState.Paused);
        Assert.True(tts.IsPaused);

        Assert.True((await client.ResumeAsync()).Ok);
        await log.TakeAsync<PlayerStateEvent>(state => state.State == PlayerState.Speaking);
        Assert.False(tts.IsPaused);

        await host.DisposeAsync();
    }

    [Fact(Timeout = 20000)]
    public async Task DuplicateGeneration_IsNotQueuedTwice()
    {
        await using var root = new TempRoot();
        var tts = new ManualTtsEngine();
        var host = await TestHost.StartAsync(root.Path, tts);
        var first = await IngestClient.SubmitAsync(host.IngestSocketPath, new SpeechDraft("cursor", "same", "one"));
        var second = await IngestClient.SubmitAsync(host.IngestSocketPath, new SpeechDraft("cursor", "same", "two"));

        Assert.False(first.Duplicate);
        Assert.True(second.Duplicate);
        Assert.Equal(first.Id, second.Id);
        Assert.Equal("Cursor.\none.", tts.Text);

        await using var client = await host.ConnectInProcessAsync();
        var snapshot = await client.GetSnapshotAsync();
        Assert.Equal(first.Id, snapshot.Snapshot!.CurrentItemId);
        Assert.Empty(snapshot.Snapshot.Queue);
        Assert.Single(Directory.GetFiles(Path.Combine(root.Path, "inbox")));
        await host.DisposeAsync();
    }
}
