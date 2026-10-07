using MachineVoice.Core;
using MachineVoice.Protocol;

namespace MachineVoice.Core.Tests;

public class PrefetchTests
{
    [Fact(Timeout = 20000)]
    public async Task Confirmation_IsSynthesizedBeforeListen()
    {
        await using var root = new TempRoot();
        var tts = new PreparableTts();
        var host = await TestHost.StartAsync(root.Path, tts);
        await using var client = await host.ConnectInProcessAsync();
        var log = EventLog.Pump(client);
        Assert.True((await client.SetModeAsync(PlaybackMode.Confirm)).Ok);

        var waiting = await IngestClient.SubmitAsync(host.IngestSocketPath, new SpeechDraft("cursor", "g", "текст ответа"));
        await log.TakeAsync<ConfirmationRequestedEvent>(confirmation => confirmation.Item.Id == waiting.Id);
        var ready = Environment.TickCount64 + 5000;
        while (tts.Prepared.All(item => item.Id != waiting.Id))
        {
            if (Environment.TickCount64 > ready)
                throw new TimeoutException("The confirmation was not prepared.");
            await Task.Delay(10);
        }

        Assert.Null(tts.UtteranceId);
        Assert.Equal(waiting.Id, tts.Prepared[^1].Id);
        Assert.Equal("Cursor.\nтекст ответа.", tts.Prepared[^1].Text);

        Assert.True((await client.ListenAsync(waiting.Id!)).Ok);
        await log.TakeAsync<PlayerStateEvent>(state => state.State == PlayerState.Speaking && state.ItemId == waiting.Id);
        Assert.Equal("Cursor.\nтекст ответа.", tts.Text);
        await host.DisposeAsync();
    }

    [Fact(Timeout = 20000)]
    public async Task NextQueuedResponse_StartsWhenTheCurrentOneIsNearItsEnd()
    {
        await using var root = new TempRoot();
        var tts = new PreparableTts();
        var host = await TestHost.StartAsync(root.Path, tts);
        await using var client = await host.ConnectInProcessAsync();

        var first = await IngestClient.SubmitAsync(host.IngestSocketPath, new SpeechDraft("cursor", "g1", "первый"));
        var second = await IngestClient.SubmitAsync(host.IngestSocketPath, new SpeechDraft("cursor", "g2", "второй"));
        var deadline = Environment.TickCount64 + 5000;
        while (tts.UtteranceId != first.Id)
        {
            if (Environment.TickCount64 > deadline)
                throw new TimeoutException("The first response did not start.");
            await Task.Delay(10);
        }

        tts.EmitPosition(0, 100);
        await Task.Delay(80);
        Assert.Empty(tts.Prepared);

        tts.EmitPosition(90, 100);
        deadline = Environment.TickCount64 + 5000;
        while (tts.Prepared.Count == 0)
        {
            if (Environment.TickCount64 > deadline)
                throw new TimeoutException("The next response was not prepared.");
            await Task.Delay(10);
        }

        Assert.Equal(second.Id, tts.Prepared[^1].Id);
        Assert.Equal("Cursor.\nвторой.", tts.Prepared[^1].Text);

        tts.EmitPosition(10, 100);
        deadline = Environment.TickCount64 + 5000;
        while (tts.Cancels == 0)
        {
            if (Environment.TickCount64 > deadline)
                throw new TimeoutException("Preparation was not cancelled.");
            await Task.Delay(10);
        }

        await host.DisposeAsync();
    }

    sealed class PreparableTts : ManualTtsEngine, IPreparableTts
    {
        public List<(string Id, string Text)> Prepared { get; } = [];
        public int Cancels { get; private set; }

        public void Prepare(string utteranceId, string text) => Prepared.Add((utteranceId, text));

        public void CancelPrepare() => Cancels++;
    }
}
