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
    public async Task Default_QueuesBehindWhatIsPlaying()
    {
        await using var root = new TempRoot();
        var tts = new ManualTtsEngine();
        var host = await TestHost.StartAsync(root.Path, tts);
        await using var client = await host.ConnectInProcessAsync();
        var log = EventLog.Pump(client);

        var settings = (await client.GetSettingsAsync()).Settings!;
        Assert.False(settings.Hotkeys.Interrupt);
        Assert.Equal(HotkeyChordDto.DefaultKeyCode, settings.Hotkeys.Clipboard.KeyCode);
        Assert.Equal(HotkeyChordDto.DefaultModifiers, settings.Hotkeys.Clipboard.Modifiers);

        var playing = await IngestClient.SubmitAsync(host.IngestSocketPath, new SpeechDraft("cursor", "g-play", "первый"));
        await log.TakeAsync<PlayerStateEvent>(state => state.State == PlayerState.Speaking && state.ItemId == playing.Id);
        var speech = tts.Text;

        Assert.True((await client.SpeakClipboardAsync("буфер")).Ok);
        var snapshot = (await client.GetSnapshotAsync()).Snapshot!;
        Assert.Equal(playing.Id, snapshot.Current!.Id);
        Assert.Equal(speech, tts.Text);
        Assert.Equal(SpeakClipboardCommand.Source, Assert.Single(snapshot.Queue).Source);
        Assert.DoesNotContain(snapshot.History, entry => entry.Item.Id == playing.Id);
        await host.DisposeAsync();
    }

    [Fact(Timeout = 20000)]
    public async Task Interrupt_ResumesAtTheSameSecond_AfterTheClipboardFinishesOrIsSkipped()
    {
        await using var root = new TempRoot();
        var tts = new ManualTtsEngine();
        var host = await TestHost.StartAsync(root.Path, tts);
        await using var client = await host.ConnectInProcessAsync();
        var log = EventLog.Pump(client);
        Assert.True((await client.UpdateSettingsAsync(hotkeys: new HotkeySettingsDto
        {
            Interrupt = true,
            Clipboard = new HotkeyChordDto(),
        })).Ok);

        var playing = await IngestClient.SubmitAsync(host.IngestSocketPath, new SpeechDraft("cursor", "g-play", "первый"));
        await log.TakeAsync<PlayerStateEvent>(state => state.State == PlayerState.Speaking && state.ItemId == playing.Id);
        var speech = tts.Text;
        tts.EmitPosition(12.5, 40);
        await log.TakeAsync<PlayerPositionEvent>(position => position.ItemId == playing.Id && position.Position == 12.5);

        Assert.True((await client.SpeakClipboardAsync("буфер")).Ok);
        Assert.Equal("Буфер обмена.\nбуфер.", tts.Text);
        var during = (await client.GetSnapshotAsync()).Snapshot!;
        Assert.Equal(SpeakClipboardCommand.Source, during.Current!.Source);
        Assert.DoesNotContain(during.History, entry => entry.Item.Id == playing.Id);
        Assert.Empty(during.Queue);

        tts.Complete();
        await log.TakeAsync<PlayerStateEvent>(state => state.State == PlayerState.Speaking && state.ItemId == playing.Id);
        Assert.Equal(speech, tts.Text);
        Assert.Equal(12.5, Assert.Single(tts.Seeks));

        tts.EmitPosition(12.5, 40);
        await log.TakeAsync<PlayerPositionEvent>(position => position.ItemId == playing.Id && position.Position == 12.5);
        Assert.True((await client.SpeakClipboardAsync("ещё")).Ok);
        Assert.True((await client.SkipAsync()).Ok);
        await log.TakeAsync<PlayerStateEvent>(state => state.State == PlayerState.Speaking && state.ItemId == playing.Id);
        Assert.Equal(speech, tts.Text);
        Assert.Equal([12.5, 12.5], tts.Seeks);
        Assert.DoesNotContain((await client.GetSnapshotAsync()).Snapshot!.History, entry => entry.Item.Id == playing.Id && entry.Outcome == SpeechOutcome.Skipped);
        await host.DisposeAsync();
    }

    [Fact(Timeout = 20000)]
    public async Task Fifo_AndPaused_StillKeepTheCurrentReading()
    {
        await using var root = new TempRoot();
        var tts = new ManualTtsEngine();
        var host = await TestHost.StartAsync(root.Path, tts);
        await using var client = await host.ConnectInProcessAsync();
        var log = EventLog.Pump(client);
        Assert.True((await client.UpdateSettingsAsync(order: QueueOrder.Fifo)).Ok);

        var playing = await IngestClient.SubmitAsync(host.IngestSocketPath, new SpeechDraft("cursor", "g-play", "первый"));
        await log.TakeAsync<PlayerStateEvent>(state => state.State == PlayerState.Speaking && state.ItemId == playing.Id);
        Assert.True((await client.PauseAsync()).Ok);
        await log.TakeAsync<PlayerStateEvent>(state => state.State == PlayerState.Paused);

        Assert.True((await client.SpeakClipboardAsync("буфер")).Ok);
        var snapshot = (await client.GetSnapshotAsync()).Snapshot!;
        Assert.Equal(PlayerState.Paused, snapshot.Player);
        Assert.Equal(playing.Id, snapshot.Current!.Id);
        Assert.Equal(SpeakClipboardCommand.Source, Assert.Single(snapshot.Queue).Source);
        await host.DisposeAsync();
    }

    [Fact(Timeout = 20000)]
    public async Task QueuedClipboard_AsksForConfirmation_WhenItsTurnComes()
    {
        await using var root = new TempRoot();
        var tts = new ManualTtsEngine();
        var host = await TestHost.StartAsync(root.Path, tts);
        await using var client = await host.ConnectInProcessAsync();
        var log = EventLog.Pump(client);

        var playing = await IngestClient.SubmitAsync(host.IngestSocketPath, new SpeechDraft("cursor", "g-play", "первый"));
        await log.TakeAsync<PlayerStateEvent>(state => state.State == PlayerState.Speaking && state.ItemId == playing.Id);
        Assert.True((await client.SpeakClipboardAsync("буфер")).Ok);
        Assert.True((await client.SetModeAsync(PlaybackMode.Confirm)).Ok);

        tts.Complete();
        var toast = await log.TakeAsync<ConfirmationRequestedEvent>();
        Assert.Equal(SpeakClipboardCommand.Source, toast.Item.Source);
        Assert.Equal(PlayerState.Idle, (await client.GetSnapshotAsync()).Snapshot!.Player);
        await host.DisposeAsync();
    }

    [Fact(Timeout = 20000)]
    public async Task Topic_IsTheFirstSentence_AtMostTwelveWords()
    {
        await using var root = new TempRoot();
        var tts = new ManualTtsEngine();
        var host = await TestHost.StartAsync(root.Path, tts);
        await using var client = await host.ConnectInProcessAsync();

        Assert.True((await client.SpeakClipboardAsync(
            "Раз два три четыре пять шесть семь восемь девять десять одиннадцать двенадцать тринадцать. Хвост.")).Ok);
        Assert.Equal(
            "Раз два три четыре пять шесть семь восемь девять десять одиннадцать двенадцать…",
            (await client.GetSnapshotAsync()).Snapshot!.Current!.Topic);
        await host.DisposeAsync();
    }

    [Fact(Timeout = 20000)]
    public async Task Hotkeys_RoundTrip_AndRejectAnEmptyChord()
    {
        await using var root = new TempRoot();
        var tts = new ManualTtsEngine();
        var host = await TestHost.StartAsync(root.Path, tts);
        await using var client = await host.ConnectInProcessAsync();

        var saved = await client.UpdateSettingsAsync(hotkeys: new HotkeySettingsDto
        {
            Interrupt = true,
            Clipboard = new HotkeyChordDto { KeyCode = 0x00, Modifiers = 0x0100 },
        });
        Assert.True(saved.Ok);
        var settings = (await client.GetSettingsAsync()).Settings!.Hotkeys;
        Assert.True(settings.Interrupt);
        Assert.Equal(0x00, settings.Clipboard.KeyCode);
        Assert.Equal(0x0100, settings.Clipboard.Modifiers);

        var rejected = await client.UpdateSettingsAsync(hotkeys: new HotkeySettingsDto
        {
            Interrupt = false,
            Clipboard = new HotkeyChordDto { KeyCode = 0x09, Modifiers = 0 },
        });
        Assert.Equal(ProtocolErrors.InvalidArgument, rejected.Error);
        Assert.True((await client.GetSettingsAsync()).Settings!.Hotkeys.Interrupt);
        await host.DisposeAsync();
    }

    [Fact(Timeout = 20000)]
    public async Task UnseekableEngine_RestartsTheInterruptedText()
    {
        await using var root = new TempRoot();
        var tts = new PlainTtsEngine();
        var host = await TestHost.StartAsync(root.Path, tts);
        await using var client = await host.ConnectInProcessAsync();
        var log = EventLog.Pump(client);
        Assert.True((await client.UpdateSettingsAsync(hotkeys: new HotkeySettingsDto { Interrupt = true })).Ok);

        var playing = await IngestClient.SubmitAsync(host.IngestSocketPath, new SpeechDraft("cursor", "g-play", "первый"));
        await log.TakeAsync<PlayerStateEvent>(state => state.State == PlayerState.Speaking && state.ItemId == playing.Id);
        var speech = tts.Text;

        Assert.True((await client.SpeakClipboardAsync("буфер")).Ok);
        tts.Complete();
        await log.TakeAsync<PlayerStateEvent>(state => state.State == PlayerState.Speaking && state.ItemId == playing.Id);
        Assert.Equal(speech, tts.Text);
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

/// <summary>A voice that cannot seek: interruption restarts the same text from the beginning.</summary>
sealed class PlainTtsEngine : ITtsEngine
{
    public string? Text { get; private set; }

    public event EventHandler<TtsProgressEventArgs>? Progress { add { } remove { } }
    public event EventHandler<TtsCompletedEventArgs>? Completed;

    public void Speak(string utteranceId, string text)
    {
        UtteranceId = utteranceId;
        Text = text;
    }

    public void Pause()
    {
    }

    public void Resume()
    {
    }

    public void Stop() => UtteranceId = null;

    public void Complete()
    {
        var id = UtteranceId ?? throw new InvalidOperationException("Not speaking.");
        UtteranceId = null;
        Completed?.Invoke(this, new TtsCompletedEventArgs(id));
    }

    string? UtteranceId { get; set; }
}
