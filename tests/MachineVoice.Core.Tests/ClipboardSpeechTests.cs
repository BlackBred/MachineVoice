using MachineVoice.Core;
using MachineVoice.Protocol;

namespace MachineVoice.Core.Tests;

public class ClipboardSpeechTests
{
    [Fact(Timeout = 20000)]
    public async Task SpeaksNow_InSilentMode_AndLeavesTheQueue()
    {
        await using var root = new TempRoot();
        var tts = new ManualTtsEngine();
        var http = new StubHttpHandler(async (_, _, cancellationToken) =>
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException();
        });
        var host = await TestHost.StartAsync(root.Path, tts, http);
        await using var client = await host.ConnectInProcessAsync();
        var log = EventLog.Pump(client);
        Assert.True((await client.SetModeAsync(PlaybackMode.Silent)).Ok);
        Assert.True((await client.UpdateSettingsAsync(summary: new SummarySettingsDto
        {
            Enabled = true,
            Model = "slow",
            TimeoutSeconds = 30,
        })).Ok);

        var waiting = await IngestClient.SubmitAsync(host.IngestSocketPath, new SpeechDraft("cursor", "g-wait", "ответ"));
        await log.TakeAsync<QueueChangedEvent>(queue => queue.Items.Any(item => item.Id == waiting.Id));
        Assert.Null(tts.UtteranceId);

        Assert.True((await client.SpeakClipboardAsync("смотри https://github.com/org/repo")).Ok);
        Assert.Equal("Буфер обмена.\nсмотри github.com.", tts.Text);

        var snapshot = (await client.GetSnapshotAsync()).Snapshot!;
        Assert.Equal(PlayerState.Speaking, snapshot.Player);
        Assert.Equal(SpeakClipboardCommand.Source, snapshot.Current!.Source);
        Assert.Equal(tts.Text, snapshot.Current.Speech);
        Assert.Equal(waiting.Id, Assert.Single(snapshot.Queue).Id);

        tts.Complete();
        var spoken = await log.TakeAsync<HistoryAppendedEvent>(entry => entry.Entry.Outcome == SpeechOutcome.Spoken);
        Assert.Equal(SpeakClipboardCommand.Source, spoken.Entry.Item.Source);
        await log.TakeAsync<PlayerStateEvent>(state => state.State == PlayerState.Idle);
        var after = (await client.GetSnapshotAsync()).Snapshot!;
        Assert.Equal(PlayerState.Idle, after.Player);
        Assert.Equal(waiting.Id, Assert.Single(after.Queue).Id);
        Assert.DoesNotContain(http.Requests, call => call.Body.Contains("github.com", StringComparison.Ordinal));
        await host.DisposeAsync();
    }

    [Fact(Timeout = 20000)]
    public async Task SkipsWhatIsPlaying()
    {
        await using var root = new TempRoot();
        var tts = new ManualTtsEngine();
        var host = await TestHost.StartAsync(root.Path, tts);
        await using var client = await host.ConnectInProcessAsync();
        var log = EventLog.Pump(client);

        var playing = await IngestClient.SubmitAsync(host.IngestSocketPath, new SpeechDraft("cursor", "g-play", "первый"));
        await log.TakeAsync<PlayerStateEvent>(state => state.State == PlayerState.Speaking && state.ItemId == playing.Id);

        Assert.True((await client.SpeakClipboardAsync("буфер")).Ok);
        Assert.Equal(playing.Id, (await log.TakeAsync<HistoryAppendedEvent>(entry => entry.Entry.Outcome == SpeechOutcome.Skipped)).Entry.Item.Id);
        Assert.Equal("Буфер обмена.\nбуфер.", tts.Text);
        Assert.Equal(SpeakClipboardCommand.Source, (await client.GetSnapshotAsync()).Snapshot!.Current!.Source);
        await host.DisposeAsync();
    }

    [Fact(Timeout = 20000)]
    public async Task ConfirmToast_StaysUntilTheClipboardIsFinished()
    {
        await using var root = new TempRoot();
        var tts = new ManualTtsEngine();
        var host = await TestHost.StartAsync(root.Path, tts);
        await using var client = await host.ConnectInProcessAsync();
        var log = EventLog.Pump(client);
        Assert.True((await client.SetModeAsync(PlaybackMode.Confirm)).Ok);

        var pending = await IngestClient.SubmitAsync(host.IngestSocketPath, new SpeechDraft("cursor", "g-toast", "жди"));
        Assert.Equal(pending.Id, (await log.TakeAsync<ConfirmationRequestedEvent>()).Item.Id);
        Assert.Null(tts.UtteranceId);

        Assert.True((await client.SpeakClipboardAsync("буфер")).Ok);
        Assert.Equal("Буфер обмена.\nбуфер.", tts.Text);
        var during = (await client.GetSnapshotAsync()).Snapshot!;
        Assert.Equal(PlayerState.Speaking, during.Player);
        Assert.Equal(pending.Id, during.Confirmation!.Id);

        tts.Complete();
        await log.TakeAsync<HistoryAppendedEvent>(entry => entry.Entry.Item.Source == SpeakClipboardCommand.Source);
        await log.TakeAsync<PlayerStateEvent>(state => state.State == PlayerState.Idle);
        Assert.Null(tts.UtteranceId);
        Assert.Equal(pending.Id, (await client.GetSnapshotAsync()).Snapshot!.Confirmation!.Id);
        await host.DisposeAsync();
    }

    [Fact(Timeout = 20000)]
    public async Task BlankOrHugeText_IsRejected_AndThereIsNoChatToOpen()
    {
        await using var root = new TempRoot();
        var tts = new ManualTtsEngine();
        var host = await TestHost.StartAsync(root.Path, tts);
        await using var client = await host.ConnectInProcessAsync();

        var blank = await client.SpeakClipboardAsync(" \n\t ");
        Assert.False(blank.Ok);
        Assert.Equal(ProtocolErrors.InvalidArgument, blank.Error);
        var huge = await client.SpeakClipboardAsync(new string('а', SpeakClipboardCommand.MaxText + 1));
        Assert.Equal(ProtocolErrors.InvalidArgument, huge.Error);
        Assert.Null(tts.UtteranceId);

        Assert.True((await client.SpeakClipboardAsync("текст")).Ok);
        var current = (await client.GetSnapshotAsync()).Snapshot!.Current!;
        Assert.Equal(SpeakClipboardCommand.Source, current.Source);
        var open = await client.OpenChatAsync(current.Id);
        Assert.False(open.Ok);
        Assert.Equal(ProtocolErrors.InvalidState, open.Error);
        await host.DisposeAsync();
    }

    [Fact(Timeout = 20000)]
    public async Task OnChange_ReadsTheClipboardHeadingOnce()
    {
        await using var root = new TempRoot();
        var tts = new ManualTtsEngine();
        var host = await TestHost.StartAsync(root.Path, tts);
        await using var client = await host.ConnectInProcessAsync();
        var log = EventLog.Pump(client);
        Assert.True((await client.UpdateSettingsAsync(heading: HeadingMode.OnChange)).Ok);

        Assert.True((await client.SpeakClipboardAsync("раз")).Ok);
        Assert.Equal("Буфер обмена.\nраз.", tts.Text);
        tts.Complete();
        await log.TakeAsync<PlayerStateEvent>(state => state.State == PlayerState.Idle);

        Assert.True((await client.SpeakClipboardAsync("два")).Ok);
        Assert.Equal("два.", tts.Text);
        await host.DisposeAsync();
    }
}
