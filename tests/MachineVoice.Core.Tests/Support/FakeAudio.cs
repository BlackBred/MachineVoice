using System.Collections.Concurrent;
using MachineVoice.Core;

namespace MachineVoice.Core.Tests;

/// <summary>A player whose clips last until the test ends them.</summary>
sealed class FakeAudioPlayer : IAudioPlayer
{
    readonly object _gate = new();
    bool _active;
    bool _paused;
    double _position;
    double _rate = 1;

    public List<byte[]> Played { get; } = [];
    public bool IsPaused { get { lock (_gate) return _paused; } }
    public bool IsActive { get { lock (_gate) return _active; } }

    public double Rate
    {
        get { lock (_gate) return _rate; }
        set { lock (_gate) _rate = value; }
    }

    /// <summary>Seconds into the clip, out of 1.</summary>
    public double Position
    {
        get { lock (_gate) return _position; }
        set { lock (_gate) _position = value; }
    }

    public void Play(byte[] audio)
    {
        lock (_gate)
        {
            Played.Add(audio);
            _active = true;
            _paused = false;
            _position = 0;
        }
    }

    public void Pause()
    {
        lock (_gate)
            _paused = _active;
    }

    public void Resume()
    {
        lock (_gate)
            _paused = false;
    }

    public void Stop()
    {
        lock (_gate)
        {
            _active = false;
            _paused = false;
        }
    }

    public List<double> Seeks { get; } = [];

    public void Seek(double position)
    {
        lock (_gate)
        {
            Seeks.Add(position);
            _position = position;
        }
    }

    public AudioPlayback State
    {
        get
        {
            lock (_gate)
                return new AudioPlayback(_active, _position, 1);
        }
    }

    public int PlayedCount
    {
        get
        {
            lock (_gate)
                return Played.Count;
        }
    }

    /// <summary>The clip reaches its end.</summary>
    public void End()
    {
        lock (_gate)
            _active = false;
    }

    public async Task WaitForPlayAsync(int count, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (PlayedCount < count)
        {
            if (Environment.TickCount64 > deadline)
                throw new TimeoutException($"Clip {count} did not start; {PlayedCount} played.");
            await Task.Delay(10);
        }
    }

    public void Dispose()
    {
    }
}

sealed class FakeSpeechServer : ISpeechServer
{
    public ConcurrentQueue<string> Calls { get; } = new();

    public Task EnsureRunningAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        Calls.Enqueue("ensure " + endpoint);
        return Task.CompletedTask;
    }

    public void Busy() => Calls.Enqueue("busy");

    public void Idle(TimeSpan? unloadAfter) => Calls.Enqueue("idle " + (unloadAfter?.TotalMinutes.ToString() ?? "never"));

    public void Dispose() => Calls.Enqueue("dispose");
}

/// <summary>Records every engine event of an <see cref="ITtsEngine"/>.</summary>
sealed class TtsRecorder
{
    public TtsRecorder(ITtsEngine tts)
    {
        tts.Progress += (_, args) => Words.Enqueue(args);
        tts.Completed += (_, args) => Completed.TrySetResult(args.UtteranceId);
        if (tts is IFallibleTtsEngine fallible)
            fallible.Failed += (_, args) => Failed.TrySetResult(args);
    }

    public ConcurrentQueue<TtsProgressEventArgs> Words { get; } = new();
    public TaskCompletionSource<string> Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<TtsFailedEventArgs> Failed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}
