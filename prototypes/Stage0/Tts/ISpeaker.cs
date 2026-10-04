namespace MachineVoice.Stage0.Tts;

internal enum SpeakerState { Idle, Speaking, Paused }

internal interface ISpeaker
{
    string Name { get; }
    SpeakerState State { get; }
    event Action<SpeakerState>? StateChanged;

    void Speak(string text);
    void Pause();
    void Resume();
    void Stop();

    void TogglePause()
    {
        if (State == SpeakerState.Speaking) Pause();
        else if (State == SpeakerState.Paused) Resume();
    }
}
