namespace MachineVoice.Core;

/// <summary>Plays one clip at a time. Does not call back: the caller polls <see cref="State"/>.</summary>
public interface IAudioPlayer : IDisposable
{
    /// <summary>Starts a complete audio file (WAV), replacing the current clip.</summary>
    void Play(byte[] audio);

    void Pause();
    void Resume();
    void Stop();

    AudioPlayback State { get; }
}

/// <param name="Active">A clip is loaded and has not reached its end; a paused clip is active.</param>
/// <param name="Position">Seconds played.</param>
/// <param name="Duration">Seconds in the clip.</param>
public readonly record struct AudioPlayback(bool Active, double Position, double Duration);

/// <summary>A local server that keeps a speech model in memory.</summary>
public interface ISpeechServer : IDisposable
{
    /// <summary>Returns once the server at <paramref name="endpoint"/> accepts requests, starting it if needed.</summary>
    Task EnsureRunningAsync(Uri endpoint, CancellationToken cancellationToken);

    /// <summary>Speech is in progress or about to start: the model has to stay loaded.</summary>
    void Busy();

    /// <summary>
    /// Nothing to read. After <paramref name="unloadAfter"/> a server that this process started may stop and free
    /// the model's memory; null keeps it running.
    /// </summary>
    void Idle(TimeSpan? unloadAfter);
}
