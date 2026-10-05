using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using MachineVoice.Core;
using MachineVoice.Protocol;
using MachineVoice.Platform.MacOS.Interop;
using static MachineVoice.Platform.MacOS.Interop.ObjC;

namespace MachineVoice.Platform.MacOS;

/// <summary>
/// The macOS voice as audio: AVSpeechSynthesizer writes PCM buffers instead of playing them, and word marks
/// (macOS 14+) give the time of every word. The audio then plays like any other clip, so the playback rate
/// applies to it. The buffers arrive on the main queue: the process needs NSApplication or
/// <see cref="MacMainLoop.Run"/> on the main thread. A write runs about 50 times faster than real time.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class AvSpeechWriter : IChunkSynthesizer, IConfigurableTts, IDisposable
{
    public const string Name = "macOS voice";

    /// <summary>Longer than <see cref="WriteTimeout"/>, so the engine sees why a write failed.</summary>
    public static readonly TimeSpan ChunkTimeout = TimeSpan.FromSeconds(15);

    static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(10);
    const nint MarkWord = 1;

    static readonly ConcurrentDictionary<IntPtr, AvSpeechWriter> ByBlock = new();

    // Calls into the synthesizer hold _native; callbacks on the main thread take only _gate.
    readonly object _native = new();
    readonly object _gate = new();
    readonly SemaphoreSlim _turn = new(1, 1);
    readonly AvSpeechOptions _options;
    readonly IntPtr _synth;
    readonly IntPtr _voice;
    readonly IntPtr _bufferBlock;
    readonly IntPtr _markerBlock;
    readonly bool _marks;
    float _rate;
    Write? _pending;
    bool _broken;
    bool _disposed;

    public unsafe AvSpeechWriter(AvSpeechOptions? options = null)
    {
        _options = options ?? new AvSpeechOptions();
        _rate = _options.Rate ?? (float)SystemVoiceSettingsDto.DefaultRate;
        NativeLibrary.Load("/System/Library/Frameworks/AVFoundation.framework/AVFoundation");

        var pool = objc_autoreleasePoolPush();
        try
        {
            _synth = Send(Send(Class("AVSpeechSynthesizer"), Sel("alloc")), Sel("init"));
            _marks = RespondsTo(_synth, "writeUtterance:toBufferCallback:toMarkerCallback:");
            _voice = AvSpeechEngine.FindVoice(_options);
            if (_voice != IntPtr.Zero)
                Send(_voice, Sel("retain"));
        }
        finally
        {
            objc_autoreleasePoolPop(pool);
        }

        _bufferBlock = Blocks.ObjectCallback(&OnBuffer);
        _markerBlock = Blocks.ObjectCallback(&OnMarkers);
        ByBlock[_bufferBlock] = this;
        ByBlock[_markerBlock] = this;
    }

    /// <summary>
    /// The engine that speaks with the macOS voice through <paramref name="player"/>. The voice writes far faster
    /// than real time, so it writes the whole response ahead: the duration a seek works with is then exact.
    /// </summary>
    public static ChunkedAudioEngine Engine(IAudioPlayer player, AvSpeechOptions? options = null) =>
        new(player, new AvSpeechWriter(options), Name, ChunkTimeout, options?.Log, lookahead: int.MaxValue);

    /// <summary>The voice rate applies from the next utterance.</summary>
    public void Apply(TtsSettingsDto settings)
    {
        lock (_gate)
            _rate = (float)(settings.SystemVoice?.Rate ?? SystemVoiceSettingsDto.DefaultRate);
    }

    public ISynthesisSession Begin()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return new Session(this, _rate);
        }
    }

    public void Dispose()
    {
        Write? pending;
        lock (_native)
        {
            lock (_gate)
            {
                if (_disposed)
                    return;
                _disposed = true;
                pending = _pending;
                _pending = null;
            }

            ByBlock.TryRemove(_bufferBlock, out _);
            ByBlock.TryRemove(_markerBlock, out _);
            Release(_voice);
            Release(_synth);
        }

        pending?.Fail(new ObjectDisposedException(GetType().FullName));
    }

    async Task<SpeechAudio> WriteAsync(string text, float rate, CancellationToken cancellationToken)
    {
        // A write cannot be cancelled, and its callbacks do not say which utterance they belong to: the next
        // write waits until this one has ended.
        await _turn.WaitAsync(cancellationToken).ConfigureAwait(false);
        Write write;
        try
        {
            write = Start(text, rate);
        }
        catch
        {
            _turn.Release();
            throw;
        }

        _ = ReleaseTurnAsync(write);
        return await write.Done.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    Write Start(string text, float rate)
    {
        lock (_native)
        {
            var write = new Write(text);
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_broken)
                    throw new InvalidOperationException("AVSpeechSynthesizer stopped writing audio.");
                _pending = write;
            }

            var pool = objc_autoreleasePoolPush();
            try
            {
                var utterance = Send(Send(Class("AVSpeechUtterance"), Sel("alloc")), Sel("initWithString:"), NSString(text.Replace('\0', ' ')));
                if (_voice != IntPtr.Zero)
                    SendVoid(utterance, Sel("setVoice:"), _voice);
                SendVoid(utterance, Sel("setRate:"), rate);
                if (_marks)
                    SendVoid(_synth, Sel("writeUtterance:toBufferCallback:toMarkerCallback:"), utterance, _bufferBlock, _markerBlock);
                else
                    SendVoid(_synth, Sel("writeUtterance:toBufferCallback:"), utterance, _bufferBlock);
                Release(utterance);
            }
            catch
            {
                lock (_gate)
                    _pending = null;
                throw;
            }
            finally
            {
                objc_autoreleasePoolPop(pool);
            }

            return write;
        }
    }

    async Task ReleaseTurnAsync(Write write)
    {
        try
        {
            await write.Done.Task.WaitAsync(WriteTimeout).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Late buffers of this write would land in the next one; the engine falls back instead.
            lock (_gate)
                _broken = true;
            if (ex is TimeoutException)
            {
                _options.Log?.Invoke($"AVSpeechSynthesizer wrote no audio within {WriteTimeout.TotalSeconds:0} s.");
                write.Fail(new TimeoutException("AVSpeechSynthesizer wrote no audio."));
            }
        }

        lock (_gate)
        {
            if (_pending == write)
                _pending = null;
        }

        _turn.Release();
    }

    unsafe void Buffer(IntPtr buffer)
    {
        Write? write;
        lock (_gate)
            write = _pending;
        if (write is null)
            return;

        if (buffer == IntPtr.Zero || SendBool(buffer, Sel("isKindOfClass:"), (nint)Class("AVAudioPCMBuffer")) == 0)
        {
            write.Fail(new InvalidDataException("AVSpeechSynthesizer wrote a buffer that is not PCM."));
            return;
        }

        var frames = (int)SendUInt(buffer, Sel("frameLength"));
        if (frames == 0)
        {
            write.Finish();
            return;
        }

        var format = Send(buffer, Sel("format"));
        var common = (long)Send(format, Sel("commonFormat"));
        var channels = (int)SendUInt(format, Sel("channelCount"));
        var interleaved = SendBool(format, Sel("isInterleaved")) != 0;
        var stride = interleaved ? channels : 1;
        var sampleBytes = common switch
        {
            1 => 4,
            3 => 2,
            4 => 4,
            _ => 0,
        };
        if (sampleBytes == 0 || channels < 1)
        {
            write.Fail(new InvalidDataException($"AVSpeechSynthesizer wrote an unsupported format ({common}, {channels} channels)."));
            return;
        }

        write.SetFormat(SendDouble(format, Sel("sampleRate")), sampleBytes * stride);

        // The first channel only: speech is mono.
        var samples = new float[frames];
        switch (common)
        {
            case 1:
                var floats = *(float**)Send(buffer, Sel("floatChannelData"));
                for (var i = 0; i < frames; i++)
                    samples[i] = floats[i * stride];
                break;
            case 3:
                var shorts = *(short**)Send(buffer, Sel("int16ChannelData"));
                for (var i = 0; i < frames; i++)
                    samples[i] = shorts[i * stride] / 32768f;
                break;
            default:
                var ints = *(int**)Send(buffer, Sel("int32ChannelData"));
                for (var i = 0; i < frames; i++)
                    samples[i] = ints[i * stride] / 2147483648f;
                break;
        }

        write.Append(samples);
    }

    void Markers(IntPtr markers)
    {
        Write? write;
        lock (_gate)
            write = _pending;
        if (write is null || markers == IntPtr.Zero)
            return;

        var count = (int)Send(markers, Sel("count"));
        for (var i = 0; i < count; i++)
        {
            var marker = Send(markers, Sel("objectAtIndex:"), i);
            if (Send(marker, Sel("mark")) != MarkWord)
                continue;
            var range = SendRange(marker, Sel("textRange"));
            write.Mark((int)range.Location, (long)Send(marker, Sel("byteSampleOffset")));
        }
    }

    [UnmanagedCallersOnly]
    static void OnBuffer(IntPtr block, IntPtr buffer) => Deliver(block, writer => writer.Buffer(buffer));

    [UnmanagedCallersOnly]
    static void OnMarkers(IntPtr block, IntPtr markers) => Deliver(block, writer => writer.Markers(markers));

    static void Deliver(IntPtr block, Action<AvSpeechWriter> action)
    {
        if (!ByBlock.TryGetValue(block, out var writer))
            return;

        // An exception must not unwind into the Objective-C runtime.
        try
        {
            action(writer);
        }
        catch (Exception ex)
        {
            writer._options.Log?.Invoke($"AVSpeechSynthesizer write callback failed: {ex}");
            Write? write;
            lock (writer._gate)
                write = writer._pending;
            write?.Fail(ex);
        }
    }

    sealed class Session(AvSpeechWriter owner, float rate) : ISynthesisSession
    {
        public Task PrepareAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<SpeechAudio> SynthesizeAsync(string text, CancellationToken cancellationToken) =>
            owner.WriteAsync(text, rate, cancellationToken);

        public void Dispose()
        {
        }
    }

    /// <summary>One utterance being written. Filled on the main thread, completed once.</summary>
    sealed class Write(string text)
    {
        readonly List<float> _samples = [];
        readonly List<(int Location, long Offset)> _marks = [];
        double _sampleRate;
        int _bytesPerFrame;

        public TaskCompletionSource<SpeechAudio> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void SetFormat(double sampleRate, int bytesPerFrame)
        {
            _sampleRate = sampleRate;
            _bytesPerFrame = bytesPerFrame;
        }

        public void Append(float[] samples) => _samples.AddRange(samples);

        public void Mark(int location, long byteOffset) => _marks.Add((location, byteOffset));

        public void Finish()
        {
            if (_samples.Count == 0 || _sampleRate <= 0)
            {
                Fail(new InvalidDataException("AVSpeechSynthesizer wrote no audio."));
                return;
            }

            var starts = _marks.Count == 0
                ? null
                : SpeechChunks.WordStarts(text, _marks.Select(mark => (mark.Location, mark.Offset / (double)_bytesPerFrame / _sampleRate)));
            Done.TrySetResult(new SpeechAudio(WavFile.Mono16(_samples.ToArray(), (int)Math.Round(_sampleRate)), starts));
        }

        public void Fail(Exception error) => Done.TrySetException(error);
    }
}
