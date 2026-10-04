using System.Runtime.InteropServices;
using Avalonia.Threading;
using static MachineVoice.Stage0.Interop.ObjC;

namespace MachineVoice.Stage0.Tts;

/// <summary>
/// AVSpeechSynthesizer through the Objective-C runtime. Pauses at a word boundary, so resuming
/// continues cleanly. State is polled; word progress via a delegate comes later. Main thread only.
/// </summary>
internal sealed class AvSpeaker : ISpeaker
{
    private const nint BoundaryImmediate = 0;
    private const nint BoundaryWord = 1;

    private readonly IntPtr _synth;
    private readonly IntPtr _voice;
    private readonly DispatcherTimer _poll;

    public AvSpeaker(string language)
    {
        NativeLibrary.Load("/System/Library/Frameworks/AVFoundation.framework/AVFoundation");

        _synth = Send(Send(objc_getClass("AVSpeechSynthesizer"), Sel("alloc")), Sel("init"));
        _voice = Send(objc_getClass("AVSpeechSynthesisVoice"), Sel("voiceWithLanguage:"), NSString(language));
        if (_voice != IntPtr.Zero)
            Send(_voice, Sel("retain"));

        _poll = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, (_, _) => Refresh());
    }

    public string Name => "AVSpeechSynthesizer";
    public SpeakerState State { get; private set; } = SpeakerState.Idle;
    public event Action<SpeakerState>? StateChanged;

    public void Speak(string text)
    {
        Stop();

        var utterance = Send(objc_getClass("AVSpeechUtterance"), Sel("speechUtteranceWithString:"), NSString(text));
        if (_voice != IntPtr.Zero)
            Send(utterance, Sel("setVoice:"), _voice);

        Send(_synth, Sel("speakUtterance:"), utterance);
        SetState(SpeakerState.Speaking);
        _poll.Start();
    }

    public void Pause()
    {
        if (State == SpeakerState.Speaking && SendBool(_synth, Sel("pauseSpeakingAtBoundary:"), BoundaryWord) != 0)
            SetState(SpeakerState.Paused);
    }

    public void Resume()
    {
        if (State == SpeakerState.Paused && SendBool(_synth, Sel("continueSpeaking")) != 0)
            SetState(SpeakerState.Speaking);
    }

    public void Stop()
    {
        if (State == SpeakerState.Idle)
            return;
        SendBool(_synth, Sel("stopSpeakingAtBoundary:"), BoundaryImmediate);
        _poll.Stop();
        SetState(SpeakerState.Idle);
    }

    private void Refresh()
    {
        // isSpeaking stays true while paused; a word-boundary pause also takes effect with a delay.
        if (SendBool(_synth, Sel("isSpeaking")) != 0)
            return;
        _poll.Stop();
        SetState(SpeakerState.Idle);
    }

    private static IntPtr NSString(string value)
    {
        var utf8 = Marshal.StringToCoTaskMemUTF8(value);
        try
        {
            return Send(objc_getClass("NSString"), Sel("stringWithUTF8String:"), utf8);
        }
        finally
        {
            Marshal.FreeCoTaskMem(utf8);
        }
    }

    private void SetState(SpeakerState state)
    {
        if (State == state)
            return;
        State = state;
        StateChanged?.Invoke(state);
    }
}
