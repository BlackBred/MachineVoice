using MachineVoice.Core;
using MachineVoice.Protocol;

namespace MachineVoice.Core.Tests;

public class HeadingModeTests
{
    [Fact(Timeout = 20000)]
    public async Task Always_IsDefault()
    {
        await using var root = new TempRoot();
        var tts = new ManualTtsEngine();
        var host = await TestHost.StartAsync(root.Path, tts);
        await using var client = await host.ConnectInProcessAsync();
        var log = EventLog.Pump(client);
        Assert.Equal(HeadingMode.Always, (await client.GetSettingsAsync()).Settings!.Heading);

        await IngestClient.SubmitAsync(host.IngestSocketPath, Draft("a", "App"));
        Assert.Equal("Cursor, проект App.\na.", tts.Text);
        var second = await IngestClient.SubmitAsync(host.IngestSocketPath, Draft("b", "App"));
        tts.Complete();
        await log.TakeAsync<PlayerStateEvent>(state => state.State == PlayerState.Speaking && state.ItemId == second.Id);
        Assert.Equal("Cursor, проект App.\nb.", tts.Text);
        await host.DisposeAsync();
    }

    [Fact(Timeout = 20000)]
    public async Task OnChange_ReadsTheHeadingOnlyWhenTheSourceOrProjectChanges()
    {
        await using var root = new TempRoot();
        var tts = new ManualTtsEngine();
        var host = await TestHost.StartAsync(root.Path, tts);
        await using var client = await host.ConnectInProcessAsync();
        var log = EventLog.Pump(client);
        Assert.True((await client.UpdateSettingsAsync(order: QueueOrder.Fifo, heading: HeadingMode.OnChange)).Ok);

        await IngestClient.SubmitAsync(host.IngestSocketPath, Draft("a", "App"));
        Assert.Equal("Cursor, проект App.\na.", tts.Text);
        var second = await IngestClient.SubmitAsync(host.IngestSocketPath, Draft("b", "App"));
        var third = await IngestClient.SubmitAsync(host.IngestSocketPath, Draft("c", "Other"));
        var fourth = await IngestClient.SubmitAsync(host.IngestSocketPath, Draft("d", "Other", "claude-code"));

        tts.Complete();
        await log.TakeAsync<PlayerStateEvent>(state => state.State == PlayerState.Speaking && state.ItemId == second.Id);
        Assert.Equal("b.", tts.Text);
        tts.Complete();
        await log.TakeAsync<PlayerStateEvent>(state => state.State == PlayerState.Speaking && state.ItemId == third.Id);
        Assert.Equal("Cursor, проект Other.\nc.", tts.Text);
        tts.Complete();
        await log.TakeAsync<PlayerStateEvent>(state => state.State == PlayerState.Speaking && state.ItemId == fourth.Id);
        Assert.Equal("Claude code, проект Other.\nd.", tts.Text);

        var current = (await client.GetSnapshotAsync()).Snapshot!.Current!;
        Assert.Equal(tts.Text, current.Speech);
        tts.Complete();
        var spoken = await log.TakeAsync<HistoryAppendedEvent>(entry => entry.Entry.Item.Id == fourth.Id);
        Assert.Equal("Claude code, проект Other.\nd.", spoken.Entry.Item.Speech);
        await host.DisposeAsync();
    }

    [Fact(Timeout = 20000)]
    public async Task OnChange_ComparesWithTheResponseReadBefore_NotTheOneThatArrivedBefore()
    {
        await using var root = new TempRoot();
        var tts = new ManualTtsEngine();
        var host = await TestHost.StartAsync(root.Path, tts);
        await using var client = await host.ConnectInProcessAsync();
        var log = EventLog.Pump(client);
        Assert.True((await client.UpdateSettingsAsync(heading: HeadingMode.OnChange)).Ok);

        await IngestClient.SubmitAsync(host.IngestSocketPath, Draft("a", "App"));
        await IngestClient.SubmitAsync(host.IngestSocketPath, Draft("b", "Other"));
        var third = await IngestClient.SubmitAsync(host.IngestSocketPath, Draft("c", "App"));

        tts.Complete();
        await log.TakeAsync<PlayerStateEvent>(state => state.State == PlayerState.Speaking && state.ItemId == third.Id);
        Assert.Equal("c.", tts.Text);
        await host.DisposeAsync();
    }

    [Fact(Timeout = 20000)]
    public async Task Never_SkipsTheHeading_AndSurvivesRestart()
    {
        await using var root = new TempRoot();
        var host = await TestHost.StartAsync(root.Path, new ManualTtsEngine());
        await using (var client = await host.ConnectInProcessAsync())
            Assert.True((await client.UpdateSettingsAsync(heading: HeadingMode.Never)).Ok);
        await host.DisposeAsync();

        var tts = new ManualTtsEngine();
        var restarted = await TestHost.StartAsync(root.Path, tts);
        await using var again = await restarted.ConnectInProcessAsync();
        Assert.Equal(HeadingMode.Never, (await again.GetSettingsAsync()).Settings!.Heading);
        await IngestClient.SubmitAsync(restarted.IngestSocketPath, Draft("a", "App"));
        Assert.Equal("a.", tts.Text);
        await restarted.DisposeAsync();
    }

    [Fact(Timeout = 20000)]
    public async Task UnknownHeadingMode_IsRejected()
    {
        await using var root = new TempRoot();
        var host = await TestHost.StartAsync(root.Path, new ManualTtsEngine());
        await using var client = await host.ConnectInProcessAsync();
        Assert.Equal(ProtocolErrors.InvalidArgument, (await client.UpdateSettingsAsync(heading: (HeadingMode)42)).Error);
        await host.DisposeAsync();
    }

    static SpeechDraft Draft(string generationId, string project, string source = "cursor") =>
        new(source, generationId, generationId, Project: project);
}
