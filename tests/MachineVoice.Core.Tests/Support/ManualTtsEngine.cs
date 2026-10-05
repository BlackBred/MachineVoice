using MachineVoice.Core;

namespace MachineVoice.Core.Tests;

sealed class ManualTtsEngine : ISeekableTtsEngine
{
    readonly object _gate = new();

    public string? UtteranceId { get; private set; }
    public string? Text { get; private set; }
    public bool IsPaused { get; private set; }

    /// <summary>The next <see cref="Speak"/> throws, as an engine that cannot start would.</summary>
    public bool FailNextSpeak { get; set; }

    public event EventHandler<TtsProgressEventArgs>? Progress;
    public event EventHandler<TtsCompletedEventArgs>? Completed;

    public void Speak(string utteranceId, string text)
    {
        lock (_gate)
        {
            if (FailNextSpeak)
            {
                FailNextSpeak = false;
                throw new InvalidOperationException("Speech failed.");
            }

            UtteranceId = utteranceId;
            Text = text;
            IsPaused = false;
        }
    }

    public void Pause()
    {
        lock (_gate)
            IsPaused = true;
    }

    public void Resume()
    {
        lock (_gate)
            IsPaused = false;
    }

    public void Stop()
    {
        lock (_gate)
        {
            UtteranceId = null;
            IsPaused = false;
        }
    }

    public void Emit(int wordIndex, string word)
    {
        string id;
        lock (_gate)
            id = UtteranceId ?? throw new InvalidOperationException("Not speaking.");
        Progress?.Invoke(this, new TtsProgressEventArgs(id, wordIndex, word));
    }

    public void Complete()
    {
        string id;
        lock (_gate)
        {
            id = UtteranceId ?? throw new InvalidOperationException("Not speaking.");
            UtteranceId = null;
            IsPaused = false;
        }

        Completed?.Invoke(this, new TtsCompletedEventArgs(id));
    }

    public List<double> Seeks { get; } = [];

    public event EventHandler<TtsPositionEventArgs>? PositionChanged;

    public void Seek(double position)
    {
        lock (_gate)
            Seeks.Add(position);
    }

    public void EmitPosition(double position, double duration)
    {
        string id;
        lock (_gate)
            id = UtteranceId ?? throw new InvalidOperationException("Not speaking.");
        PositionChanged?.Invoke(this, new TtsPositionEventArgs(id, position, duration));
    }
}
