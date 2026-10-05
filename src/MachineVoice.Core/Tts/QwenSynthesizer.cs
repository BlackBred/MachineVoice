using MachineVoice.Protocol;

namespace MachineVoice.Core;

/// <summary>
/// Qwen3-TTS through an mlx-audio server, for <see cref="ChunkedAudioEngine"/>. The model has no speed control
/// of its own (mlx-audio ignores <c>speed</c> for it), so only the playback rate changes its tempo.
/// </summary>
public sealed class QwenSynthesizer : IChunkSynthesizer, IConfigurableTts, IDisposable
{
    public const string Name = "Qwen3-TTS";
    public static readonly TimeSpan ChunkTimeout = TimeSpan.FromMinutes(2);

    // The first load downloads the model (about 4.5 GB).
    static readonly TimeSpan LoadTimeout = TimeSpan.FromMinutes(30);

    readonly object _gate = new();
    readonly ISpeechServer? _server;
    readonly HttpClient _http;
    readonly SpeechSynthesisClient _client;
    readonly Action<string>? _log;
    readonly CancellationTokenSource _lifetime = new();
    QwenTtsSettingsDto _settings = new();
    bool _enabled;
    int _sessions;
    bool _disposed;

    public QwenSynthesizer(ISpeechServer? server = null, HttpMessageHandler? httpHandler = null, Action<string>? log = null)
    {
        _server = server;
        _http = httpHandler is null ? new HttpClient() : new HttpClient(httpHandler, disposeHandler: false);
        _http.Timeout = Timeout.InfiniteTimeSpan;
        _client = new SpeechSynthesisClient(_http);
        _log = log;
    }

    /// <summary>The engine that speaks through this synthesizer.</summary>
    public static ChunkedAudioEngine Engine(IAudioPlayer player, ISpeechServer? server = null, HttpMessageHandler? httpHandler = null, Action<string>? log = null) =>
        new(player, new QwenSynthesizer(server, httpHandler, log), Name, ChunkTimeout, log);

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
            idle = !enabled && _enabled && _sessions == 0;
            _settings = qwen;
            _enabled = enabled;
        }

        if (warmUp)
            _ = Task.Run(() => WarmUpAsync(qwen));
        else if (idle)
            _server?.Idle(UnloadAfter(qwen));
    }

    public ISynthesisSession Begin()
    {
        QwenTtsSettingsDto settings;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _sessions++;
            settings = _settings;
        }

        _server?.Busy();
        return new Session(this, settings);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
        }

        _lifetime.Cancel();
        _server?.Dispose();
        _http.Dispose();
    }

    void End(QwenTtsSettingsDto settings)
    {
        lock (_gate)
        {
            _sessions--;
            if (_disposed)
                return;
        }

        _server?.Idle(UnloadAfter(settings));
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
            idle = _sessions == 0 && !_disposed;
        if (idle)
            _server?.Idle(UnloadAfter(settings));
    }

    static TimeSpan? UnloadAfter(QwenTtsSettingsDto settings) =>
        settings.UnloadAfterMinutes > 0 ? TimeSpan.FromMinutes(settings.UnloadAfterMinutes) : null;

    sealed class Session(QwenSynthesizer owner, QwenTtsSettingsDto settings) : ISynthesisSession
    {
        public Task PrepareAsync(CancellationToken cancellationToken) =>
            owner._server?.EnsureRunningAsync(new Uri(settings.Endpoint), cancellationToken) ?? Task.CompletedTask;

        public async Task<SpeechAudio> SynthesizeAsync(string text, CancellationToken cancellationToken) =>
            new(await owner._client.SynthesizeAsync(settings, text, cancellationToken).ConfigureAwait(false));

        public void Dispose() => owner.End(settings);
    }
}
