using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using MachineVoice.Core;
using static MachineVoice.Platform.MacOS.Interop.ObjC;

namespace MachineVoice.Platform.MacOS;

public sealed class AvSpeechOptions
{
    /// <summary>Voice identifier, e.g. com.apple.voice.compact.ru-RU.Milena. Wins over <see cref="Language"/>.</summary>
    public string? VoiceIdentifier { get; init; }

    /// <summary>BCP 47 language for the default voice of that language. Null means the system voice.</summary>
    public string? Language { get; init; } = "ru-RU";

    /// <summary>AVSpeechUtterance rate, 0..1. Null keeps the system default.</summary>
    public float? Rate { get; init; }

    /// <summary>0..1. Null keeps the system default.</summary>
    public float? Volume { get; init; }

    public Action<string>? Log { get; init; }
}

/// <summary>
/// AVSpeechSynthesizer through the Objective-C runtime. Pause takes effect at the next word boundary,
/// word progress and the end of an utterance come from the synthesizer delegate. The delegate runs on the
/// main queue: the process needs NSApplication or <see cref="MacMainLoop.Run"/> on the main thread.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class AvSpeechEngine : ITtsEngine, IDisposable
{
    const nint BoundaryImmediate = 0;
    const nint BoundaryWord = 1;

    static readonly ConcurrentDictionary<IntPtr, AvSpeechEngine> ByDelegate = new();
    static readonly Lazy<IntPtr> DelegateClass = new(RegisterDelegateClass);

    // Calls into the synthesizer hold _native; delegate callbacks on the main thread take only _gate,
    // so a synthesizer call that waits for the main queue cannot deadlock against a callback.
    readonly object _native = new();
    readonly object _gate = new();
    readonly AvSpeechOptions _options;
    readonly IntPtr _synth;
    readonly IntPtr _delegate;
    readonly IntPtr _voice;
    Utterance? _current;
    bool _disposed;

    public AvSpeechEngine(AvSpeechOptions? options = null)
    {
        _options = options ?? new AvSpeechOptions();
        NativeLibrary.Load("/System/Library/Frameworks/AVFoundation.framework/AVFoundation");

        var pool = objc_autoreleasePoolPush();
        try
        {
            _synth = Send(Send(Class("AVSpeechSynthesizer"), Sel("alloc")), Sel("init"));
            _delegate = Send(Send(DelegateClass.Value, Sel("alloc")), Sel("init"));
            ByDelegate[_delegate] = this;
            SendVoid(_synth, Sel("setDelegate:"), _delegate);

            _voice = FindVoice(_options);
            if (_voice != IntPtr.Zero)
                Send(_voice, Sel("retain"));
        }
        finally
        {
            objc_autoreleasePoolPop(pool);
        }
    }

    public event EventHandler<TtsProgressEventArgs>? Progress;
    public event EventHandler<TtsCompletedEventArgs>? Completed;

    /// <summary>Identifiers of the installed voices, for settings.</summary>
    public static IReadOnlyList<string> VoiceIdentifiers()
    {
        NativeLibrary.Load("/System/Library/Frameworks/AVFoundation.framework/AVFoundation");
        var pool = objc_autoreleasePoolPush();
        try
        {
            var voices = Send(Class("AVSpeechSynthesisVoice"), Sel("speechVoices"));
            var count = (int)Send(voices, Sel("count"));
            var result = new List<string>(count);
            for (var i = 0; i < count; i++)
            {
                var voice = Send(voices, Sel("objectAtIndex:"), i);
                if (ToManaged(Send(voice, Sel("identifier"))) is { } identifier)
                    result.Add(identifier);
            }

            return result;
        }
        finally
        {
            objc_autoreleasePoolPop(pool);
        }
    }

    public void Speak(string utteranceId, string text)
    {
        ArgumentNullException.ThrowIfNull(utteranceId);
        ArgumentNullException.ThrowIfNull(text);

        lock (_native)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var pool = objc_autoreleasePoolPush();
            try
            {
                var handle = Send(Send(Class("AVSpeechUtterance"), Sel("alloc")), Sel("initWithString:"), NSString(text.Replace('\0', ' ')));
                if (_voice != IntPtr.Zero)
                    SendVoid(handle, Sel("setVoice:"), _voice);
                if (_options.Rate is { } rate)
                    SendVoid(handle, Sel("setRate:"), rate);
                if (_options.Volume is { } volume)
                    SendVoid(handle, Sel("setVolume:"), volume);

                // The synthesizer queues utterances; the previous one is cancelled, not finished.
                var previous = Swap(new Utterance(handle, utteranceId, text));
                if (previous is not null)
                    SendBool(_synth, Sel("stopSpeakingAtBoundary:"), BoundaryImmediate);
                SendVoid(_synth, Sel("speakUtterance:"), handle);
                Release(previous?.Handle ?? IntPtr.Zero);
            }
            finally
            {
                objc_autoreleasePoolPop(pool);
            }
        }
    }

    public void Pause()
    {
        lock (_native)
        {
            if (!_disposed && HasCurrent)
                SendBool(_synth, Sel("pauseSpeakingAtBoundary:"), BoundaryWord);
        }
    }

    public void Resume()
    {
        lock (_native)
        {
            if (!_disposed && HasCurrent)
                SendBool(_synth, Sel("continueSpeaking"));
        }
    }

    public void Stop()
    {
        lock (_native)
        {
            if (!_disposed)
                CancelCurrent();
        }
    }

    public void Dispose()
    {
        lock (_native)
        {
            if (_disposed)
                return;
            CancelCurrent();
            _disposed = true;

            SendVoid(_synth, Sel("setDelegate:"), IntPtr.Zero);
            ByDelegate.TryRemove(_delegate, out _);
            Release(_delegate);
            Release(_voice);
            Release(_synth);
        }
    }

    bool HasCurrent
    {
        get
        {
            lock (_gate)
                return _current is not null;
        }
    }

    Utterance? Swap(Utterance? next)
    {
        lock (_gate)
        {
            var previous = _current;
            _current = next;
            return previous;
        }
    }

    void CancelCurrent()
    {
        if (Swap(null) is not { } previous)
            return;

        SendBool(_synth, Sel("stopSpeakingAtBoundary:"), BoundaryImmediate);
        Release(previous.Handle);
    }

    void OnWord(IntPtr utterance, NSRange range)
    {
        TtsProgressEventArgs args;
        lock (_gate)
        {
            if (_current is not { } current || current.Handle != utterance)
                return;

            var start = (int)Math.Min(range.Location, (nuint)current.Text.Length);
            var length = (int)Math.Min(range.Length, (nuint)(current.Text.Length - start));
            args = new TtsProgressEventArgs(current.Id, current.WordIndex++, current.Text.Substring(start, length));
        }

        Progress?.Invoke(this, args);
    }

    void OnEnded(IntPtr utterance)
    {
        Utterance ended;
        lock (_gate)
        {
            if (_current is not { } current || current.Handle != utterance)
                return;

            // A cancellation that Stop or Speak did not ask for still ends the item, otherwise the
            // host would wait for this utterance forever.
            ended = current;
            _current = null;
        }

        Release(ended.Handle);
        Completed?.Invoke(this, new TtsCompletedEventArgs(ended.Id));
    }

    static IntPtr FindVoice(AvSpeechOptions options)
    {
        var voiceClass = Class("AVSpeechSynthesisVoice");
        if (!string.IsNullOrWhiteSpace(options.VoiceIdentifier))
        {
            var voice = Send(voiceClass, Sel("voiceWithIdentifier:"), NSString(options.VoiceIdentifier));
            if (voice != IntPtr.Zero)
                return voice;
        }

        return string.IsNullOrWhiteSpace(options.Language)
            ? IntPtr.Zero
            : Send(voiceClass, Sel("voiceWithLanguage:"), NSString(options.Language));
    }

    static string? ToManaged(IntPtr nsString) =>
        nsString == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(Send(nsString, Sel("UTF8String")));

    static unsafe IntPtr RegisterDelegateClass()
    {
        const string name = "MachineVoiceSpeechDelegate";
        var cls = objc_allocateClassPair(Class("NSObject"), name, 0);
        if (cls == IntPtr.Zero)
            return Class(name);

        class_addMethod(cls, Sel("speechSynthesizer:willSpeakRangeOfSpeechString:utterance:"),
            (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, NSRange, IntPtr, void>)&WillSpeakRange, "v@:@{_NSRange=QQ}@");
        class_addMethod(cls, Sel("speechSynthesizer:didFinishSpeechUtterance:"),
            (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, void>)&DidFinish, "v@:@@");
        class_addMethod(cls, Sel("speechSynthesizer:didCancelSpeechUtterance:"),
            (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, void>)&DidCancel, "v@:@@");

        var protocol = objc_getProtocol("AVSpeechSynthesizerDelegate");
        if (protocol != IntPtr.Zero)
            class_addProtocol(cls, protocol);
        objc_registerClassPair(cls);
        return cls;
    }

    [UnmanagedCallersOnly]
    static void WillSpeakRange(IntPtr self, IntPtr selector, IntPtr synth, NSRange range, IntPtr utterance) =>
        Deliver(self, engine => engine.OnWord(utterance, range));

    [UnmanagedCallersOnly]
    static void DidFinish(IntPtr self, IntPtr selector, IntPtr synth, IntPtr utterance) =>
        Deliver(self, engine => engine.OnEnded(utterance));

    [UnmanagedCallersOnly]
    static void DidCancel(IntPtr self, IntPtr selector, IntPtr synth, IntPtr utterance) =>
        Deliver(self, engine => engine.OnEnded(utterance));

    static void Deliver(IntPtr self, Action<AvSpeechEngine> action)
    {
        if (!ByDelegate.TryGetValue(self, out var engine))
            return;

        // An exception must not unwind into the Objective-C runtime.
        try
        {
            action(engine);
        }
        catch (Exception ex)
        {
            engine._options.Log?.Invoke($"AVSpeechSynthesizer callback failed: {ex}");
        }
    }

    sealed class Utterance(IntPtr handle, string id, string text)
    {
        public IntPtr Handle { get; } = handle;
        public string Id { get; } = id;
        public string Text { get; } = text;
        public int WordIndex { get; set; }
    }
}
