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
/// Speaks through synthesized audio. The text is synthesized sentence by sentence while earlier sentences play,
/// at most <see cref="Lookahead"/> chunks ahead, so a stop wastes little work. Pause holds the clip that plays
/// and does not start the next one. The playback rate follows the settings at once, also mid-clip.
/// </summary>
public sealed class ChunkedAudioEngine : IFallibleTtsEngine, IConfigurableTts, IDisposable
{
    const int Lookahead = 2;
    static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(40);

    // Player calls happen under _gate: the player never calls back, so this cannot deadlock.
    readonly object _gate = new();
    readonly IAudioPlayer _player;
    readonly IChunkSynthesizer _synthesizer;
    readonly string _name;
    readonly TimeSpan _chunkTimeout;
    readonly Action<string>? _log;
    Utterance? _current;
    bool _disposed;

    /// <param name="name">Engine name for the log.</param>
    /// <param name="chunkTimeout">How long one chunk may take to synthesize.</param>
    public ChunkedAudioEngine(IAudioPlayer player, IChunkSynthesizer synthesizer, string name, TimeSpan chunkTimeout, Action<string>? log = null)
    {
        _player = player;
        _synthesizer = synthesizer;
        _name = name;
        _chunkTimeout = chunkTimeout;
        _log = log;
    }

    public event EventHandler<TtsProgressEventArgs>? Progress;
    public event EventHandler<TtsCompletedEventArgs>? Completed;
    public event EventHandler<TtsFailedEventArgs>? Failed;

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
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            previous = _current;
            _current = null;
            if (previous?.Playing == true)
                _player.Stop();
        }

        // The previous session ends before the next begins: a server goes idle, then busy again.
        previous?.End();
        var next = new Utterance(utteranceId, text, _synthesizer.Begin());
        lock (_gate)
        {
            if (_disposed)
            {
                next.End();
                throw new ObjectDisposedException(GetType().FullName);
            }

            _current = next;
        }

        _ = Task.Run(() => RunAsync(next));
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
        }
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
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            current = _current;
            _current = null;
            _player.Stop();
        }

        current?.End();
        _player.Dispose();
        (_synthesizer as IDisposable)?.Dispose();
    }

    async Task RunAsync(Utterance utterance)
    {
        var chunks = SpeechChunks.Split(utterance.Text);
        var ready = chunks.Select(_ => new TaskCompletionSource<SpeechAudio>(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        var slots = new SemaphoreSlim(Lookahead);
        var producer = ProduceAsync(utterance, chunks, ready, slots);
        var index = 0;
        try
        {
            for (; index < chunks.Count; index++)
            {
                var audio = await ready[index].Task.WaitAsync(utterance.Token).ConfigureAwait(false);
                while (IsPaused(utterance))
                    await Task.Delay(PollInterval, utterance.Token).ConfigureAwait(false);
                if (!Begin(utterance, audio))
                    return;
                slots.Release();
                await PlayAsync(utterance, chunks[index], audio).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (utterance.Token.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            Fail(utterance, chunks, index, ex);
            return;
        }
        finally
        {
            utterance.Cancel();
            await producer.ConfigureAwait(false);
            slots.Dispose();
        }

        Finish(utterance);
    }

    /// <summary>Synthesizes the chunks in order. Never throws: a failure goes to the chunks that are not ready.</summary>
    async Task ProduceAsync(Utterance utterance, IReadOnlyList<SpeechChunk> chunks, TaskCompletionSource<SpeechAudio>[] ready, SemaphoreSlim slots)
    {
        var index = 0;
        try
        {
            if (chunks.Count > 0)
                await utterance.Session.PrepareAsync(utterance.Token).ConfigureAwait(false);

            for (; index < chunks.Count; index++)
            {
                await slots.WaitAsync(utterance.Token).ConfigureAwait(false);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(utterance.Token);
                timeout.CancelAfter(_chunkTimeout);
                try
                {
                    ready[index].TrySetResult(await utterance.Session.SynthesizeAsync(chunks[index].Text, timeout.Token).ConfigureAwait(false));
                }
                catch (OperationCanceledException) when (!utterance.Token.IsCancellationRequested)
                {
                    throw new TimeoutException($"No audio within {_chunkTimeout.TotalSeconds:0} s.");
                }
            }
        }
        catch (Exception ex)
        {
            for (; index < chunks.Count; index++)
            {
                if (utterance.Token.IsCancellationRequested)
                    ready[index].TrySetCanceled(utterance.Token);
                else
                    ready[index].TrySetException(ex);
            }
        }
    }

    bool IsPaused(Utterance utterance)
    {
        lock (_gate)
        {
            utterance.Token.ThrowIfCancellationRequested();
            return utterance.Paused;
        }
    }

    bool Begin(Utterance utterance, SpeechAudio audio)
    {
        lock (_gate)
        {
            if (_current != utterance)
                return false;
            utterance.Playing = true;
            _player.Play(audio.Wav);
            if (utterance.Paused)
                _player.Pause();
            return true;
        }
    }

    async Task PlayAsync(Utterance utterance, SpeechChunk chunk, SpeechAudio audio)
    {
        var reported = 0;
        Report(utterance, chunk.FirstWord, chunk.Words[0]);
        while (true)
        {
            await Task.Delay(PollInterval, utterance.Token).ConfigureAwait(false);
            AudioPlayback state;
            lock (_gate)
            {
                utterance.Token.ThrowIfCancellationRequested();
                if (_current != utterance)
                    return;
                state = _player.State;
            }

            if (!state.Active)
                return;

            var word = WordAt(chunk, audio, state);
            if (word <= reported)
                continue;
            reported = word;
            Report(utterance, chunk.FirstWord + word, chunk.Words[word]);
        }
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

    void Fail(Utterance utterance, IReadOnlyList<SpeechChunk> chunks, int index, Exception error)
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

        var remaining = string.Join(" ", chunks.Skip(index).Select(chunk => chunk.Text));
        var offset = index < chunks.Count ? chunks[index].FirstWord : 0;
        handler(this, new TtsFailedEventArgs(utterance.Id, remaining, offset, error));
    }

    sealed class Utterance(string id, string text, ISynthesisSession session)
    {
        readonly CancellationTokenSource _cancel = new();
        int _ended;

        public string Id { get; } = id;
        public string Text { get; } = text;
        public ISynthesisSession Session { get; } = session;
        public CancellationToken Token => _cancel.Token;

        // Guarded by the engine's _gate.
        public bool Paused { get; set; }
        public bool Playing { get; set; }

        public void Cancel() => _cancel.Cancel();

        public void End()
        {
            Cancel();
            if (Interlocked.Exchange(ref _ended, 1) == 0)
                Session.Dispose();
        }
    }
}
