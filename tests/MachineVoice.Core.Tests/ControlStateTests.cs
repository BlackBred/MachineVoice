using MachineVoice.Core;
using MachineVoice.Protocol;

namespace MachineVoice.Core.Tests;

public class ControlStateTests
{
    [Fact(Timeout = 30000)]
    public async Task Mirror_FollowsTheDaemon_ThroughEvents()
    {
        await using var root = new TempRoot();
        var tts = new ManualTtsEngine();
        var host = await TestHost.StartAsync(root.Path, tts);
        await using var client = await SocketControlClient.ConnectAsync(host.ControlSocketPath);
        var mirror = new Mirror(client);

        await mirror.MatchesAsync(client);
        var first = await IngestClient.SubmitAsync(host.IngestSocketPath, new SpeechDraft("cursor", "g1", "раз два три четыре", Topic: "тема"));
        await mirror.MatchesAsync(client);
        Assert.Equal(first.Id, mirror.Read(state => state.Current?.Id));
        Assert.Equal(PlayerState.Speaking, mirror.Read(state => state.Player));

        tts.Emit(1, "два");
        await mirror.WaitAsync(state => state.WordIndex == 1);
        Assert.InRange(mirror.Read(state => state.Progress)!.Value, 0.01, 0.99);

        tts.EmitPosition(3, 12);
        await mirror.WaitAsync(state => state.Progress == 0.25);
        Assert.True((await client.SeekAsync(6)).Ok);
        Assert.Equal(new[] { 6.0 }, tts.Seeks);
        Assert.Equal(ProtocolErrors.InvalidArgument, (await client.SeekAsync(-1)).Error);

        Assert.True((await client.PauseAsync()).Ok);
        await mirror.MatchesAsync(client);
        var second = await IngestClient.SubmitAsync(host.IngestSocketPath, new SpeechDraft("cursor", "g2", "второй"));
        await IngestClient.SubmitAsync(host.IngestSocketPath, new SpeechDraft("cursor", "g2b", "сбой"));
        await mirror.MatchesAsync(client);

        tts.FailNextSpeak = true;
        Assert.True((await client.SkipAsync()).Ok);
        await mirror.MatchesAsync(client);
        Assert.True(mirror.Read(state => state.Holding));
        Assert.Equal(ProtocolErrors.InvalidState, (await client.SeekAsync(1)).Error);

        Assert.True((await client.ResumeAsync()).Ok);
        await mirror.MatchesAsync(client);
        Assert.Equal(second.Id, mirror.Read(state => state.Current?.Id));
        Assert.False(mirror.Read(state => state.Holding));

        Assert.True((await client.SkipAsync()).Ok);
        await mirror.MatchesAsync(client);

        Assert.True((await client.SetModeAsync(PlaybackMode.Confirm)).Ok);
        var third = await IngestClient.SubmitAsync(host.IngestSocketPath, new SpeechDraft("cursor", "g3", "третий"));
        await mirror.MatchesAsync(client);
        Assert.Equal(third.Id, mirror.Read(state => state.Confirmation?.Id));

        Assert.True((await client.ListenAsync(third.Id!)).Ok);
        await mirror.MatchesAsync(client);
        Assert.Equal(third.Id, mirror.Read(state => state.Current?.Id));
        tts.Complete();
        await mirror.MatchesAsync(client);

        Assert.True((await client.SetModeAsync(PlaybackMode.Silent)).Ok);
        await IngestClient.SubmitAsync(host.IngestSocketPath, new SpeechDraft("cursor", "g4", "тихо"));
        await mirror.MatchesAsync(client);
        Assert.Single(mirror.Read(state => state.Queue));

        Assert.True((await client.ConnectSourceAsync("cursor")).Ok);
        await mirror.WaitAsync(state => state.Sources.TryGetValue("cursor", out var source) && source.Status == SourceConnectionStatus.Connected);
        Assert.False(mirror.Read(state => state.NeedsSnapshot));
        await host.DisposeAsync();
    }

    [Fact]
    public void UnknownItem_AsksForASnapshot()
    {
        var state = new ControlState();
        state.Reset(new SnapshotDto());
        state.Apply(new PlayerStateEvent { State = PlayerState.Speaking, ItemId = "missing" });
        Assert.True(state.NeedsSnapshot);
        Assert.Null(state.Current);
    }

    [Fact]
    public void Progress_FollowsTheTime_WhenKnown_AndResetsWithTheNextItem()
    {
        var state = new ControlState();
        state.Reset(new SnapshotDto { Queue = [new SpeechItemDto { Id = "a", Text = "раз два три четыре" }, new SpeechItemDto { Id = "b", Text = "пять" }] });
        state.Apply(new PlayerStateEvent { State = PlayerState.Speaking, ItemId = "a" });
        state.Apply(new PlayerProgressEvent { ItemId = "a", WordIndex = 0 });
        Assert.Equal(0.25, state.Progress);

        state.Apply(new PlayerPositionEvent { ItemId = "a", Position = 6, Duration = 8 });
        Assert.Equal(0.75, state.Progress);
        Assert.False(state.Apply(new PlayerPositionEvent { ItemId = "b", Position = 1, Duration = 2 }));

        state.Apply(new PlayerStateEvent { State = PlayerState.Speaking, ItemId = "b" });
        Assert.Equal(0, state.Duration);
        Assert.Equal(0, state.Progress);
    }

    [Fact]
    public void Waiting_LeavesOutTheItemAwaitingConfirmation()
    {
        var shown = new SpeechItemDto { Id = "a" };
        var next = new SpeechItemDto { Id = "b" };
        var state = new ControlState();
        state.Reset(new SnapshotDto { Confirmation = shown, Queue = [shown, next] });
        Assert.Equal(["b"], state.Waiting.Select(item => item.Id));

        state.Apply(new ConfirmationClearedEvent { ItemId = "a" });
        state.Apply(new QueueChangedEvent { Items = [next] });
        Assert.Equal(["b"], state.Waiting.Select(item => item.Id));
    }

    [Fact]
    public void History_IsCapped()
    {
        var state = new ControlState();
        for (var i = 0; i < ControlState.HistoryLimit + 5; i++)
            state.Apply(new HistoryAppendedEvent { Entry = new HistoryEntryDto { Item = new SpeechItemDto { Id = i.ToString() } } });
        Assert.Equal(ControlState.HistoryLimit, state.History.Count);
        Assert.Equal("5", state.History[0].Item.Id);
    }

    sealed class Mirror
    {
        readonly ControlState _state = new();

        public Mirror(IControlClient client)
        {
            _ = Task.Run(async () =>
            {
                await foreach (var message in client.EventsAsync())
                {
                    lock (_state)
                        _state.Apply(message);
                }
            });
        }

        public T Read<T>(Func<ControlState, T> read)
        {
            lock (_state)
                return read(_state);
        }

        public async Task WaitAsync(Func<ControlState, bool> condition)
        {
            var deadline = Environment.TickCount64 + 5000;
            while (!Read(condition))
            {
                if (Environment.TickCount64 > deadline)
                    throw new TimeoutException("The mirror did not reach the expected state.");
                await Task.Delay(15);
            }
        }

        /// <summary>Waits until the mirror equals a fresh snapshot.</summary>
        public async Task MatchesAsync(IControlClient client)
        {
            var deadline = Environment.TickCount64 + 5000;
            while (true)
            {
                var snapshot = (await client.GetSnapshotAsync()).Snapshot!;
                var difference = Read(state => Difference(state, snapshot));
                if (difference is null)
                    return;
                if (Environment.TickCount64 > deadline)
                    throw new Xunit.Sdk.XunitException($"Mirror differs from the snapshot: {difference}");
                await Task.Delay(15);
            }
        }

        static string? Difference(ControlState state, SnapshotDto snapshot)
        {
            if (state.Player != snapshot.Player)
                return $"player {state.Player} vs {snapshot.Player}";
            if (state.Current?.Id != snapshot.Current?.Id)
                return $"current {state.Current?.Id} vs {snapshot.Current?.Id}";
            if (state.Mode != snapshot.Mode)
                return $"mode {state.Mode} vs {snapshot.Mode}";
            if (state.Holding != snapshot.Holding)
                return $"holding {state.Holding} vs {snapshot.Holding}";
            if (state.Confirmation?.Id != snapshot.Confirmation?.Id)
                return $"confirmation {state.Confirmation?.Id} vs {snapshot.Confirmation?.Id}";
            if (!state.Queue.Select(item => item.Id).SequenceEqual(snapshot.Queue.Select(item => item.Id)))
                return "queue";
            if (!state.History.Select(entry => (entry.Item.Id, entry.Outcome)).SequenceEqual(
                    snapshot.History.TakeLast(ControlState.HistoryLimit).Select(entry => (entry.Item.Id, entry.Outcome))))
                return "history";
            return null;
        }
    }
}
