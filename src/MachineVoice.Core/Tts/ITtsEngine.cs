namespace MachineVoice.Core;

/// <summary>
/// Speech backend. Callbacks may arrive on any thread; the host hops back onto its own loop.
/// <see cref="Completed"/> fires only for a natural end, not after <see cref="Stop"/>.
/// </summary>
public interface ITtsEngine
{
    void Speak(string utteranceId, string text);
    void Pause();
    void Resume();
    void Stop();

    event EventHandler<TtsProgressEventArgs>? Progress;
    event EventHandler<TtsCompletedEventArgs>? Completed;
}

public sealed class TtsProgressEventArgs(string utteranceId, int wordIndex, string word) : EventArgs
{
    public string UtteranceId { get; } = utteranceId;
    public int WordIndex { get; } = wordIndex;
    public string Word { get; } = word;
}

public sealed class TtsCompletedEventArgs(string utteranceId) : EventArgs
{
    public string UtteranceId { get; } = utteranceId;
}
