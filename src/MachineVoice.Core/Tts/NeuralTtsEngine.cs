using MachineVoice.Protocol;

namespace MachineVoice.Core;

/// <summary>
/// Qwen3-TTS through an mlx-audio server. The text is synthesized sentence by sentence while earlier sentences
/// play, at most <see cref="Lookahead"/> chunks ahead, so a stop wastes little work. Pause holds the clip
/// that plays and does not start the next one. Word progress is estimated from the position in the clip.
/// </summary>
public sealed class NeuralTtsEngine : IFallibleTtsEngine, IConfigurableTts, IDisposable
{
    const int Lookahead = 2;
    static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(40);
    static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(2);

    // The first load downloads the model (about 4.5 GB).
    static readonly TimeSpan LoadTimeout = TimeSpan.FromMinutes(30);

    // Player calls happen under _gate: the player never calls back, so this cannot deadlock.
    readonly object _gate = new();
    readonly IAudioPlayer _player;
    readonly ISpeechServer? _server;
    readonly HttpClient _http;
    readonly SpeechSynthesisClient _client;
    readonly Action<string>? _log;
    readonly CancellationTokenSource _lifetime = new();
    QwenTtsSettingsDto _settings = new();
    bool _enabled;
    Utterance? _current;
    bool _disposed;

    public NeuralTtsEngine(IAudioPlayer player, ISpeechServer? server = null, HttpMessageHandler? httpHandler = null, Action<string>? log = null)
    {
        _player = player;
        _server = server;
        _http = httpHandler is null ? new HttpClient() : new HttpClient(httpHandler, disposeHandler: false);
        _http.Timeout = Timeout.InfiniteTimeSpan;
        _client = new SpeechSynthesisClient(_http);
        _log = log;
    }

    public event EventHandler<TtsProgressEventArgs>? Progress;
    public event EventHandler<TtsCompletedEventArgs>? Completed;
    public event EventHandler<TtsFailedEventArgs>? Failed;

    /// <summary>New settings apply from the next utterance. Selecting this engine loads the model in the background.</summary>
    public void Apply(TtsSettingsDto settings)
    {
        var qwen = settings.Qwen ?? new QwenTtsSettingsDto();
        bool warmUp, idle;
        lock (_gate)
        {
            if (_disposed)
                return;
            var enabled = settings.Engine == TtsEngineKind.Qwen;
            warmUp = enabled && (!_enabled || !TtsRules.Same(qwen, _settings));
            idle = !enabled && _enabled && _current is null;
            _settings = qwen;
            _enabled = enabled;
        }

        if (warmUp)
            _ = Task.Run(() => WarmUpAsync(qwen));
        else if (idle)
            _server?.Idle(UnloadAfter(qwen));
    }

    public void Speak(string utteranceId, string text)
    {
        ArgumentNullException.ThrowIfNull(utteranceId);
        ArgumentNullException.ThrowIfNull(text);

        Utterance next;
        Utterance? previous;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            previous = _current;
            next = new Utterance(utteranceId, text, _settings);
            _current = next;
            if (previous?.Playing == true)
                _player.Stop();
        }

        previous?.Cancel();
        _server?.Busy();
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

        if (stopped is null)
            return;
        stopped.Cancel();
        _server?.Idle(UnloadAfter(stopped.Settings));
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

        current?.Cancel();
        _lifetime.Cancel();
        _player.Dispose();
        _server?.Dispose();
        _http.Dispose();
    }

    async Task RunAsync(Utterance utterance)
    {
        var chunks = SpeechChunks.Split(utterance.Text);
        var ready = chunks.Select(_ => new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
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
                await PlayAsync(utterance, chunks[index]).ConfigureAwait(false);
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
    async Task ProduceAsync(Utterance utterance, IReadOnlyList<SpeechChunk> chunks, TaskCompletionSource<byte[]>[] ready, SemaphoreSlim slots)
    {
        var index = 0;
        try
        {
            if (chunks.Count > 0 && _server is not null)
                await _server.EnsureRunningAsync(new Uri(utterance.Settings.Endpoint), utterance.Token).ConfigureAwait(false);

            for (; index < chunks.Count; index++)
            {
                await slots.WaitAsync(utterance.Token).ConfigureAwait(false);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(utterance.Token);
                timeout.CancelAfter(RequestTimeout);
                try
                {
                    ready[index].TrySetResult(await _client.SynthesizeAsync(utterance.Settings, chunks[index].Text, timeout.Token).ConfigureAwait(false));
                }
                catch (OperationCanceledException) when (!utterance.Token.IsCancellationRequested)
                {
                    throw new TimeoutException($"No audio within {RequestTimeout.TotalSeconds:0} s.");
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

    bool Begin(Utterance utterance, byte[] audio)
    {
        lock (_gate)
        {
            if (_current != utterance)
                return false;
            utterance.Playing = true;
            _player.Play(audio);
            if (utterance.Paused)
                _player.Pause();
            return true;
        }
    }

    async Task PlayAsync(Utterance utterance, SpeechChunk chunk)
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
            if (state.Duration <= 0)
                continue;

            var word = Math.Clamp((int)(state.Position / state.Duration * chunk.Words.Count), 0, chunk.Words.Count - 1);
            if (word <= reported)
                continue;
            reported = word;
            Report(utterance, chunk.FirstWord + word, chunk.Words[word]);
        }
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

        _server?.Idle(UnloadAfter(utterance.Settings));
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

        _log?.Invoke($"Qwen3-TTS failed: {error.Message}");
        _server?.Idle(UnloadAfter(utterance.Settings));
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

    async Task WarmUpAsync(QwenTtsSettingsDto settings)
    {
        _server?.Busy();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        timeout.CancelAfter(LoadTimeout);
        try
        {
            if (_server is not null)
                await _server.EnsureRunningAsync(new Uri(settings.Endpoint), timeout.Token).ConfigureAwait(false);
            await _client.LoadModelAsync(settings, timeout.Token).ConfigureAwait(false);
            _log?.Invoke($"Qwen3-TTS model loaded: {settings.Model}");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Qwen3-TTS warm-up failed: {ex.Message}");
        }

        bool idle;
        lock (_gate)
            idle = _current is null && !_disposed;
        if (idle)
            _server?.Idle(UnloadAfter(settings));
    }

    static TimeSpan? UnloadAfter(QwenTtsSettingsDto settings) =>
        settings.UnloadAfterMinutes > 0 ? TimeSpan.FromMinutes(settings.UnloadAfterMinutes) : null;

    sealed class Utterance(string id, string text, QwenTtsSettingsDto settings)
    {
        readonly CancellationTokenSource _cancel = new();

        public string Id { get; } = id;
        public string Text { get; } = text;
        public QwenTtsSettingsDto Settings { get; } = settings;
        public CancellationToken Token => _cancel.Token;

        // Guarded by the engine's _gate.
        public bool Paused { get; set; }
        public bool Playing { get; set; }

        public void Cancel() => _cancel.Cancel();
    }
}
