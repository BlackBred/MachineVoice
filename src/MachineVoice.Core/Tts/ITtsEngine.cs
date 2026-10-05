using MachineVoice.Protocol;

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

/// <summary>An engine that follows the TTS settings. The host applies them at start and after every change.</summary>
public interface IConfigurableTts
{
    void Apply(TtsSettingsDto settings);
}

/// <summary>
/// An engine that can fail in the middle of an utterance (a server that went away). Instead of
/// <see cref="ITtsEngine.Completed"/> it reports the text it did not read.
/// </summary>
public interface IFallibleTtsEngine : ITtsEngine
{
    event EventHandler<TtsFailedEventArgs>? Failed;
}

/// <summary>An engine that knows the time of its audio and can move in it.</summary>
public interface ISeekableTtsEngine : ITtsEngine
{
    /// <summary>
    /// Moves the current utterance to <paramref name="position"/> seconds of its audio, exactly: the audio may
    /// resume mid-word. Snapping to the nearest word start or pause is possible with the word times, not done yet.
    /// </summary>
    void Seek(double position);

    event EventHandler<TtsPositionEventArgs>? PositionChanged;
}

public sealed class TtsProgressEventArgs(string utteranceId, int wordIndex, string word) : EventArgs
{
    public string UtteranceId { get; } = utteranceId;
    public int WordIndex { get; } = wordIndex;
    public string Word { get; } = word;
}

/// <param name="position">Seconds of audio from the start of the utterance.</param>
/// <param name="duration">Seconds of audio in the utterance, partly estimated; 0 when unknown.</param>
public sealed class TtsPositionEventArgs(string utteranceId, double position, double duration) : EventArgs
{
    public string UtteranceId { get; } = utteranceId;
    public double Position { get; } = position;
    public double Duration { get; } = duration;
}

public sealed class TtsCompletedEventArgs(string utteranceId) : EventArgs
{
    public string UtteranceId { get; } = utteranceId;
}

public sealed class TtsFailedEventArgs(string utteranceId, string remainingText, int wordOffset, Exception error) : EventArgs
{
    public string UtteranceId { get; } = utteranceId;

    /// <summary>The part of the text that was not read.</summary>
    public string RemainingText { get; } = remainingText;

    /// <summary>Index of the first word of <see cref="RemainingText"/> in the whole utterance.</summary>
    public int WordOffset { get; } = wordOffset;

    public Exception Error { get; } = error;
}
