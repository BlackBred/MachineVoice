using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using MachineVoice.Core;
using static MachineVoice.Platform.MacOS.Interop.ObjC;

namespace MachineVoice.Platform.MacOS;

/// <summary>
/// AVAudioPlayer through the Objective-C runtime. Unlike afplay under SIGSTOP (see stage 0 in the plan), pause
/// does not leave CoreAudio looping a buffer. Needs no delegate and no main loop: the end of a clip is polled.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class AvAudioPlayer : IAudioPlayer
{
    readonly object _gate = new();
    readonly float? _volume;
    IntPtr _player;
    bool _paused;
    bool _disposed;

    /// <param name="volume">0..1. Null keeps full volume.</param>
    public AvAudioPlayer(float? volume = null)
    {
        _volume = volume;
        NativeLibrary.Load("/System/Library/Frameworks/AVFoundation.framework/AVFoundation");
    }

    public unsafe void Play(byte[] audio)
    {
        ArgumentNullException.ThrowIfNull(audio);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ReleasePlayer();

            var pool = objc_autoreleasePoolPush();
            try
            {
                IntPtr data;
                fixed (byte* bytes = audio)
                    data = Send(Class("NSData"), Sel("dataWithBytes:length:"), (IntPtr)bytes, (nuint)audio.Length);

                IntPtr error = IntPtr.Zero;
                var player = Send(Send(Class("AVAudioPlayer"), Sel("alloc")), Sel("initWithData:error:"), data, (IntPtr)(&error));
                if (player == IntPtr.Zero)
                    throw new InvalidDataException("AVAudioPlayer cannot read the audio: " + Describe(error));

                if (_volume is { } volume)
                    SendVoid(player, Sel("setVolume:"), volume);
                SendBool(player, Sel("prepareToPlay"));
                if (SendBool(player, Sel("play")) == 0)
                {
                    Release(player);
                    throw new InvalidOperationException("AVAudioPlayer did not start.");
                }

                _player = player;
                _paused = false;
            }
            finally
            {
                objc_autoreleasePoolPop(pool);
            }
        }
    }

    public void Pause()
    {
        lock (_gate)
        {
            if (_player == IntPtr.Zero || _paused)
                return;
            SendVoid(_player, Sel("pause"));
            _paused = true;
        }
    }

    public void Resume()
    {
        lock (_gate)
        {
            if (_player == IntPtr.Zero || !_paused)
                return;
            SendBool(_player, Sel("play"));
            _paused = false;
        }
    }

    public void Stop()
    {
        lock (_gate)
            ReleasePlayer();
    }

    public AudioPlayback State
    {
        get
        {
            lock (_gate)
            {
                if (_player == IntPtr.Zero)
                    return default;
                var playing = SendBool(_player, Sel("isPlaying")) != 0;
                return new AudioPlayback(
                    playing || _paused,
                    SendDouble(_player, Sel("currentTime")),
                    SendDouble(_player, Sel("duration")));
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            ReleasePlayer();
            _disposed = true;
        }
    }

    void ReleasePlayer()
    {
        if (_player == IntPtr.Zero)
            return;
        SendVoid(_player, Sel("stop"));
        Release(_player);
        _player = IntPtr.Zero;
        _paused = false;
    }

    static string Describe(IntPtr error)
    {
        if (error == IntPtr.Zero)
            return "unknown error";
        var description = Send(error, Sel("localizedDescription"));
        return description == IntPtr.Zero
            ? "unknown error"
            : Marshal.PtrToStringUTF8(Send(description, Sel("UTF8String"))) ?? "unknown error";
    }
}
