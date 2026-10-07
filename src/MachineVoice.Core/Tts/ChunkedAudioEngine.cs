using MachineVoice.Protocol;

namespace MachineVoice.Core;

/// <param name="Wav">A complete audio file.</param>
/// <param name="WordStarts">
/// Seconds into the clip where each word of the chunk starts, one entry per word of <see cref="SpeechChunk.Words"/>.
/// Null when the synthesizer does not know; then the word is estimated from the position in the clip.
/// </param>
public sealed record SpeechAudio(byte[] Wav, IReadOnlyList<double>? WordStarts = null);

/// <summary>Turns the chunks of an utterance into audio for <see cref="ChunkedAudioEngine"/>.</summary>
public interface IChunkSynthesizer
{
    /// <summary>Starts an utterance with the current settings, before its first chunk.</summary>
    ISynthesisSession Begin();
}

/// <summary>One utterance. Disposed once, when the utterance finishes, fails, stops or is replaced.</summary>
public interface ISynthesisSession : IDisposable
{
    /// <summary>Runs once before the first chunk, without the per-chunk timeout (a server that has to start).</summary>
    Task PrepareAsync(CancellationToken cancellationToken);

    Task<SpeechAudio> SynthesizeAsync(string text, CancellationToken cancellationToken);
}

/// <summary>
/// Speaks through synthesized audio. The text is synthesized sentence by sentence, one chunk at a time, from the
/// chunk that plays up to <c>lookahead</c> chunks ahead, so a stop wastes little work. Pause holds the clip that
/// plays and does not start the next one. The playback rate follows the settings at once, also mid-clip.
/// A seek moves to an exact time of the utterance; chunks not synthesized yet count with an estimated length.
/// </summary>
public sealed class ChunkedAudioEngine : IFallibleTtsEngine, ISeekableTtsEngine, IConfigurableTts, IPreparableTts, IDisposable
{
    public const int DefaultLookahead = 5;
    static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(40);
    const long PositionIntervalMs = 250;

    // The length of a chunk that is not synthesized yet, until the utterance has one that is: about the pace of
    // both voices at their normal rate.
    const double DefaultSecondsPerChar = 0.065;

    // Player calls happen under _gate: the player never calls back, so this cannot deadlock.
    readonly object _gate = new();
    readonly IAudioPlayer _player;
    readonly IChunkSynthesizer _synthesizer;
    readonly string _name;
    readonly TimeSpan _chunkTimeout;
    readonly int _lookahead;
    readonly bool _packSentences;
    readonly Action<string>? _log;
    Utterance? _current;
    Utterance? _prepared;

    // The utterance whose chunk is in the synthesizer. The one that plays goes first.
    Utterance? _synthesizing;
    bool _disposed;

    /// <param name="name">Engine name for the log.</param>
    /// <param name="chunkTimeout">How long one chunk may take to synthesize.</param>
    /// <param name="lookahead">How many chunks past the one that plays are synthesized ahead.</param>
    /// <param name="packSentences">Several sentences per chunk, see <see cref="SpeechChunks.Split"/>.</param>
    public ChunkedAudioEngine(
        IAudioPlayer player,
        IChunkSynthesizer synthesizer,
        string name,
        TimeSpan chunkTimeout,
        Action<string>? log = null,
        int lookahead = DefaultLookahead,
        bool packSentences = false)
    {
        _player = player;
        _synthesizer = synthesizer;
        _name = name;
        _chunkTimeout = chunkTimeout;
        _lookahead = Math.Max(lookahead, 0);
        _packSentences = packSentences;
        _log = log;
    }

    public event EventHandler<TtsProgressEventArgs>? Progress;
    public event EventHandler<TtsCompletedEventArgs>? Completed;
    public event EventHandler<TtsFailedEventArgs>? Failed;
    public event EventHandler<TtsPositionEventArgs>? PositionChanged;

    public void Apply(TtsSettingsDto settings)
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _player.Rate = settings.PlaybackRate;
        }

        (_synthesizer as IConfigurableTts)?.Apply(settings);
    }

    public void Speak(string utteranceId, string text)
    {
        ArgumentNullException.ThrowIfNull(utteranceId);
        ArgumentNullException.ThrowIfNull(text);

        Utterance? previous;
        Utterance? dropped;
        Utterance? adopted;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            previous = _current;
            _current = null;
            if (previous?.Playing == true)
                _player.Stop();
            adopted = null;
            dropped = null;
            if (_prepared is { } prepared)
            {
                _prepared = null;
                if (prepared.Id == utteranceId && prepared.Text == text)
                    adopted = prepared;
                else
                    dropped = prepared;
            }
        }

        // The previous session ends before the next begins: a server goes idle, then busy again.
        previous?.End();
        dropped?.End();
        if (adopted is not null)
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    adopted.End();
                    throw new ObjectDisposedException(GetType().FullName);
                }

                _current = adopted;
                Wake(adopted);
            }

            _ = Task.Run(() => PlayUtteranceAsync(adopted));
            return;
        }

        var next = new Utterance(utteranceId, text, SpeechChunks.Split(text, _packSentences), _synthesizer.Begin());
        lock (_gate)
        {
            if (_disposed)
            {
                next.End();
                throw new ObjectDisposedException(GetType().FullName);
            }

            _current = next;
        }

        next.Synthesis = SynthesizeAsync(next);
        _ = Task.Run(() => PlayUtteranceAsync(next));
    }

    public void Prepare(string utteranceId, string text)
    {
        ArgumentNullException.ThrowIfNull(utteranceId);
        ArgumentNullException.ThrowIfNull(text);

        Utterance? previous;
        lock (_gate)
        {
            if (_disposed)
                return;
            if (_prepared is { } prepared && prepared.Id == utteranceId && prepared.Text == text)
                return;
            if (_current is { } current && current.Id == utteranceId && current.Text == text)
                return;
            previous = _prepared;
            _prepared = null;
        }

        previous?.End();
        var next = new Utterance(utteranceId, text, SpeechChunks.Split(text, _packSentences), _synthesizer.Begin());
        lock (_gate)
        {
            if (_disposed)
            {
                next.End();
                return;
            }

            _prepared = next;
        }

        next.Synthesis = SynthesizeAsync(next);
    }

    public void CancelPrepare()
    {
        Utterance? prepared;
        lock (_gate)
        {
            prepared = _prepared;
            _prepared = null;
        }

        prepared?.End();
    }

    public void Pause()
    {
        lock (_gate)
        {
            if (_current is not { Paused: false } utterance)
                return;
            utterance.Paused = true;
            if (utterance.Playing)
                _player.Pause();
            Wake(utterance);
        }
    }

    public void Resume()
    {
        lock (_gate)
        {
            if (_current is not { Paused: true } utterance)
                return;
            utterance.Paused = false;
            if (utterance.Playing)
                _player.Resume();
            Wake(utterance);
        }
    }

    /// <summary>
    /// Exact time, no snapping: the word starts of the synthesizer (or the silences in the audio) would allow
    /// moving to the nearest word boundary, which is not done for now.
    /// </summary>
    public void Seek(double position)
    {
        if (!double.IsFinite(position))
            return;

        TtsPositionEventArgs moved;
        List<CancellationTokenSource> cancelled;
        lock (_gate)
        {
            if (_current is not { } utterance || utterance.Chunks.Count == 0)
                return;

            var lengths = utterance.Lengths();
            var (index, fraction) = Locate(lengths, position);
            cancelled = [];
            // A clip that just ended is about to give way to the next one; the seek restarts it instead.
            if (utterance.Playing && utterance.PlayingIndex == index && utterance.Slots[index].Seconds is { } seconds && _player.State.Active)
            {
                _player.Seek(fraction * seconds);
            }
            else
            {
                utterance.Index = index;
                utterance.Fraction = fraction;
                utterance.Version++;
                if (utterance.Playing)
                {
                    _player.Stop();
                    utterance.Playing = false;
                }

                // Synthesis that the new position does not need gives way to the chunks that it does.
                for (var i = 0; i < utterance.Slots.Length; i++)
                {
                    if (utterance.Slots[i] is { State: SlotState.Running, Cancel: { } cancel } && (i < index || i > LastWanted(utterance, index)))
                        cancelled.Add(cancel);
                }

                Wake(utterance);
            }

            moved = new TtsPositionEventArgs(utterance.Id, Start(lengths, index) + fraction * lengths[index], lengths.Sum());
        }

        foreach (var cancel in cancelled)
            cancel.Cancel();
        PositionChanged?.Invoke(this, moved);
    }

    public void Stop()
    {
        Utterance? stopped;
        lock (_gate)
        {
            stopped = _current;
            _current = null;
            if (stopped?.Playing == true)
                _player.Stop();
        }

        stopped?.End();
    }

    public void Dispose()
    {
        Utterance? current;
        Utterance? prepared;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            current = _current;
            prepared = _prepared;
            _current = null;
            _prepared = null;
            _player.Stop();
        }

        current?.End();
        prepared?.End();
        _player.Dispose();
        (_synthesizer as IDisposable)?.Dispose();
    }

    async Task PlayUtteranceAsync(Utterance utterance)
    {
        var index = 0;
        try
        {
            while (true)
            {
                int version;
                double fraction;
                SlotState state;
                SpeechAudio? audio;
                Exception? error;
                bool paused;
                Task changed;
                lock (_gate)
                {
                    utterance.Token.ThrowIfCancellationRequested();
                    if (_current != utterance)
                        return;
                    index = utterance.Index;
                    if (index >= utterance.Chunks.Count)
                        break;
                    if (utterance.PrepareError is { } prepareError)
                        throw prepareError;

                    var slot = utterance.Slots[index];
                    (state, audio, error) = (slot.State, slot.Audio, slot.Error);
                    version = utterance.Version;
                    fraction = utterance.Fraction;
                    paused = utterance.Paused;
                    changed = utterance.Changed;
                }

                if (state == SlotState.Failed)
                    throw error!;
                if (state != SlotState.Ready || paused)
                {
                    await changed.WaitAsync(utterance.Token).ConfigureAwait(false);
                    continue;
                }

                if (!Begin(utterance, index, version, audio!, fraction))
                    continue;
                if (!await PlayAsync(utterance, index, version, audio!).ConfigureAwait(false))
                    continue;

                lock (_gate)
                {
                    if (utterance.Version == version)
                    {
                        utterance.Index = index + 1;
                        utterance.Fraction = 0;
                        Wake(utterance);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (utterance.Token.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            Fail(utterance, index, ex);
            return;
        }
        finally
        {
            utterance.Cancel();
            await utterance.Synthesis.ConfigureAwait(false);
        }

        Finish(utterance);
    }

    /// <summary>
    /// Synthesizes one chunk at a time: the first one in the window from the chunk that plays that is not
    /// synthesized. Never throws: a failure stays with its chunk until the player gets there.
    /// </summary>
    async Task SynthesizeAsync(Utterance utterance)
    {
        if (utterance.Chunks.Count == 0)
            return;
        try
        {
            await utterance.Session.PrepareAsync(utterance.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (utterance.Token.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                utterance.PrepareError = ex;
                Wake(utterance);
            }

            return;
        }

        while (true)
        {
            Slot? slot = null;
            var target = -1;
            Task changed;
            lock (_gate)
            {
                if (utterance.Token.IsCancellationRequested)
                {
                    Wake(utterance);
                    return;
                }

                changed = utterance.Changed;
                if (!Blocks(utterance))
                {
                    var last = LastWanted(utterance, utterance.Index);
                    for (var i = utterance.Index; i <= last; i++)
                    {
                        // Past a failed chunk the player stops anyway, unless a seek skips it.
                        if (utterance.Slots[i].State == SlotState.Failed)
                            break;
                        if (utterance.Slots[i].State != SlotState.Idle)
                            continue;
                        target = i;
                        slot = utterance.Slots[i];
                        slot.State = SlotState.Running;
                        slot.Cancel = CancellationTokenSource.CreateLinkedTokenSource(utterance.Token);
                        _synthesizing = utterance;
                        break;
                    }
                }
            }

            if (slot is null)
            {
                try
                {
                    await changed.WaitAsync(utterance.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    lock (_gate)
                        Wake(utterance);
                    return;
                }

                continue;
            }

            var seek = slot.Cancel!;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(seek.Token);
            timeout.CancelAfter(_chunkTimeout);
            SpeechAudio? audio = null;
            Exception? error = null;
            var again = false;
            try
            {
                audio = await utterance.Session.SynthesizeAsync(utterance.Chunks[target].Text, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (utterance.Token.IsCancellationRequested)
            {
                lock (_gate)
                {
                    if (_synthesizing == utterance)
                        _synthesizing = null;
                    Wake(utterance);
                }

                return;
            }
            catch (OperationCanceledException) when (seek.IsCancellationRequested)
            {
                again = true;
            }
            catch (OperationCanceledException)
            {
                error = new TimeoutException($"No audio within {_chunkTimeout.TotalSeconds:0} s.");
            }
            catch (Exception ex)
            {
                error = ex;
            }

            lock (_gate)
            {
                if (_synthesizing == utterance)
                    _synthesizing = null;
                slot.Cancel = null;
                if (again)
                {
                    slot.State = SlotState.Idle;
                }
                else if (error is not null)
                {
                    slot.State = SlotState.Failed;
                    slot.Error = error;
                }
                else
                {
                    slot.State = SlotState.Ready;
                    slot.Audio = audio;
                    slot.Seconds = WavFile.Seconds(audio!.Wav);
                }

                Wake(utterance);
            }

            seek.Dispose();
        }
    }

    bool Begin(Utterance utterance, int index, int version, SpeechAudio audio, double fraction)
    {
        lock (_gate)
        {
            if (_current != utterance || utterance.Version != version)
                return false;
            utterance.Playing = true;
            utterance.PlayingIndex = index;
            _player.Play(audio.Wav);

            var slot = utterance.Slots[index];
            if (slot.Seconds is null && _player.State.Duration > 0)
                slot.Seconds = _player.State.Duration;
            if (fraction > 0 && slot.Seconds is { } seconds)
                _player.Seek(fraction * seconds);
            if (utterance.Paused)
                _player.Pause();
            return true;
        }
    }

    /// <summary>True when the clip ended by itself, false when a seek replaced it.</summary>
    async Task<bool> PlayAsync(Utterance utterance, int index, int version, SpeechAudio audio)
    {
        var chunk = utterance.Chunks[index];
        var reported = -1;
        var reportedPosition = double.NaN;
        var reportedDuration = double.NaN;
        long nextPosition = 0;
        while (true)
        {
            AudioPlayback state;
            double start, duration;
            lock (_gate)
            {
                utterance.Token.ThrowIfCancellationRequested();
                if (_current != utterance || utterance.Version != version)
                    return false;
                state = _player.State;
                var lengths = utterance.Lengths();
                start = Start(lengths, index);
                duration = lengths.Sum();
            }

            if (!state.Active)
                return true;

            var word = WordAt(chunk, audio, state);
            if (word != reported)
            {
                reported = word;
                Report(utterance, chunk.FirstWord + word, chunk.Words[word]);
            }

            var position = start + state.Position;
            var now = Environment.TickCount64;
            if (now >= nextPosition && (Math.Abs(position - reportedPosition) > 0.01 || duration != reportedDuration))
            {
                nextPosition = now + PositionIntervalMs;
                (reportedPosition, reportedDuration) = (position, duration);
                ReportPosition(utterance, position, duration);
            }

            await Task.Delay(PollInterval, utterance.Token).ConfigureAwait(false);
        }
    }

    int LastWanted(Utterance utterance, int index) => (int)Math.Min(utterance.Chunks.Count - 1L, (long)index + _lookahead);

    /// <summary>The playing utterance takes the synthesizer; a prepared one waits until that window is full.</summary>
    bool Blocks(Utterance utterance)
    {
        if (_synthesizing is not null && _synthesizing != utterance)
            return true;
        return utterance != _current && _current is { } playing && NeedsChunk(playing);
    }

    bool NeedsChunk(Utterance utterance)
    {
        var last = LastWanted(utterance, utterance.Index);
        for (var i = utterance.Index; i <= last; i++)
        {
            if (utterance.Slots[i].State == SlotState.Failed)
                return false;
            if (utterance.Slots[i].State is SlotState.Idle or SlotState.Running)
                return true;
        }

        return false;
    }

    void Wake(Utterance utterance)
    {
        utterance.Signal();
        if (utterance != _current)
            _current?.Signal();
        if (utterance != _prepared)
            _prepared?.Signal();
    }

    static (int Index, double Fraction) Locate(double[] lengths, double position)
    {
        var start = 0.0;
        for (var i = 0; i < lengths.Length; i++)
        {
            if (position < start + lengths[i] || i == lengths.Length - 1)
                return (i, lengths[i] > 0 ? Math.Clamp((position - start) / lengths[i], 0, 1) : 0);
            start += lengths[i];
        }

        return (0, 0);
    }

    static double Start(double[] lengths, int index)
    {
        var start = 0.0;
        for (var i = 0; i < index; i++)
            start += lengths[i];
        return start;
    }

    static int WordAt(SpeechChunk chunk, SpeechAudio audio, AudioPlayback state)
    {
        var last = chunk.Words.Count - 1;
        if (audio.WordStarts is { Count: > 0 } starts)
        {
            var word = 0;
            while (word + 1 < starts.Count && starts[word + 1] <= state.Position)
                word++;
            return Math.Min(word, last);
        }

        return state.Duration <= 0 ? 0 : Math.Clamp((int)(state.Position / state.Duration * chunk.Words.Count), 0, last);
    }

    void Report(Utterance utterance, int wordIndex, string word)
    {
        lock (_gate)
        {
            if (_current != utterance)
                return;
        }

        Progress?.Invoke(this, new TtsProgressEventArgs(utterance.Id, wordIndex, word));
    }

    void ReportPosition(Utterance utterance, double position, double duration)
    {
        lock (_gate)
        {
            if (_current != utterance)
                return;
        }

        PositionChanged?.Invoke(this, new TtsPositionEventArgs(utterance.Id, position, duration));
    }

    void Finish(Utterance utterance)
    {
        lock (_gate)
        {
            if (_current != utterance)
                return;
            _current = null;
        }

        utterance.End();
        Completed?.Invoke(this, new TtsCompletedEventArgs(utterance.Id));
    }

    void Fail(Utterance utterance, int index, Exception error)
    {
        lock (_gate)
        {
            if (_current != utterance)
                return;
            _current = null;
            if (utterance.Playing)
                _player.Stop();
        }

        _log?.Invoke($"{_name} failed: {error.Message}");
        utterance.End();
        var handler = Failed;
        if (handler is null)
        {
            Completed?.Invoke(this, new TtsCompletedEventArgs(utterance.Id));
            return;
        }

        var chunks = utterance.Chunks;
        var remaining = string.Join(" ", chunks.Skip(index).Select(chunk => chunk.Text));
        var offset = index < chunks.Count ? chunks[index].FirstWord : 0;
        handler(this, new TtsFailedEventArgs(utterance.Id, remaining, offset, error));
    }

    enum SlotState
    {
        Idle,
        Running,
        Ready,
        Failed,
    }

    /// <summary>One chunk of an utterance. Guarded by the engine's _gate.</summary>
    sealed class Slot
    {
        public SlotState State;
        public SpeechAudio? Audio;
        public Exception? Error;
        public double? Seconds;
        public CancellationTokenSource? Cancel;
    }

    sealed class Utterance
    {
        readonly CancellationTokenSource _cancel = new();
        TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int _ended;

        public Utterance(string id, string text, IReadOnlyList<SpeechChunk> chunks, ISynthesisSession session)
        {
            Id = id;
            Text = text;
            Session = session;
            Chunks = chunks;
            Slots = Chunks.Select(_ => new Slot()).ToArray();
        }

        public string Id { get; }
        public string Text { get; }
        public Task Synthesis { get; set; } = Task.CompletedTask;
        public ISynthesisSession Session { get; }
        public IReadOnlyList<SpeechChunk> Chunks { get; }
        public Slot[] Slots { get; }
        public CancellationToken Token => _cancel.Token;

        // Guarded by the engine's _gate.
        public int Index { get; set; }
        public double Fraction { get; set; }
        public int Version { get; set; }
        public int PlayingIndex { get; set; } = -1;
        public bool Paused { get; set; }
        public bool Playing { get; set; }
        public Exception? PrepareError { get; set; }

        /// <summary>Completes on the next <see cref="Signal"/>.</summary>
        public Task Changed => _changed.Task;

        public void Signal()
        {
            var changed = _changed;
            _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            changed.TrySetResult();
        }

        /// <summary>Seconds of every chunk: synthesized ones exactly, the rest at the pace of those.</summary>
        public double[] Lengths()
        {
            double seconds = 0, characters = 0;
            for (var i = 0; i < Slots.Length; i++)
            {
                if (Slots[i].Seconds is { } known)
                {
                    seconds += known;
                    characters += Chunks[i].Text.Length;
                }
            }

            var pace = characters > 0 ? seconds / characters : DefaultSecondsPerChar;
            var lengths = new double[Slots.Length];
            for (var i = 0; i < Slots.Length; i++)
                lengths[i] = Slots[i].Seconds ?? Chunks[i].Text.Length * pace;
            return lengths;
        }

        public void Cancel() => _cancel.Cancel();

        public void End()
        {
            Cancel();
            if (Interlocked.Exchange(ref _ended, 1) == 0)
                Session.Dispose();
        }
    }
}
