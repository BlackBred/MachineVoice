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

        Assert.True((await client.PauseAsync()).Ok);
        await mirror.MatchesAsync(client);
        var second = await IngestClient.SubmitAsync(host.IngestSocketPath, new SpeechDraft("cursor", "g2", "второй"));
        await mirror.MatchesAsync(client);

        Assert.True((await client.StopAsync()).Ok);
        await mirror.MatchesAsync(client);
        Assert.True(mirror.Read(state => state.Holding));

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
