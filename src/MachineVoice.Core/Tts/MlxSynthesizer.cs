using MachineVoice.Protocol;

namespace MachineVoice.Core;

/// <summary>
/// A model served by mlx-audio, for <see cref="ChunkedAudioEngine"/>. Selecting the engine starts the server and
/// loads the model in the background; leaving it frees the model, since the server may keep running for another
/// one. While nothing is read the server may stop after the idle period of the settings.
/// </summary>
public abstract class MlxSynthesizer<TSettings> : IChunkSynthesizer, IConfigurableTts, IDisposable
    where TSettings : class, IMlxModelSettings
{
    public static readonly TimeSpan ChunkTimeout = TimeSpan.FromMinutes(2);

    // The first load downloads the model (gigabytes).
    static readonly TimeSpan LoadTimeout = TimeSpan.FromMinutes(30);
    static readonly TimeSpan UnloadTimeout = TimeSpan.FromSeconds(30);

    readonly object _gate = new();
    readonly TtsEngineKind _kind;
    readonly ISpeechServer? _server;
    readonly HttpClient _http;
    readonly SpeechSynthesisClient _client;
    readonly Action<string>? _log;
    readonly CancellationTokenSource _lifetime = new();
    TSettings _settings;
    bool _enabled;
    int _sessions;

    // The engine was left while it read; the model goes once the reading ends.
    bool _leftWhileBusy;
    bool _disposed;

    private protected MlxSynthesizer(
        string name,
        TtsEngineKind kind,
        TSettings defaults,
        ISpeechServer? server,
        HttpMessageHandler? httpHandler,
        Action<string>? log)
    {
        Name = name;
        _kind = kind;
        _settings = defaults;
        _server = server;
        _http = httpHandler is null ? new HttpClient() : new HttpClient(httpHandler, disposeHandler: false);
        _http.Timeout = Timeout.InfiniteTimeSpan;
        _client = new SpeechSynthesisClient(_http);
        _log = log;
    }

    /// <summary>Engine name for the log.</summary>
    public string Name { get; }

    /// <summary>New settings apply from the next utterance.</summary>
    public void Apply(TtsSettingsDto settings)
    {
        var selected = Select(settings);
        TSettings previous;
        bool warmUp, left, idle;
        lock (_gate)
        {
            if (_disposed)
                return;
            var enabled = settings.Engine == _kind;
            warmUp = enabled && (!_enabled || !Same(selected, _settings));
            left = !enabled && _enabled;
            idle = left && _sessions == 0;
            _leftWhileBusy = enabled ? false : _leftWhileBusy || (left && _sessions > 0);
            previous = _settings;
            _settings = selected;
            _enabled = enabled;
        }

        // Busy before the background work starts, so that another engine leaving the server does not stop it.
        if (warmUp)
        {
            _server?.Busy();
            _ = Task.Run(() => WarmUpAsync(selected));
        }
        else if (idle)
        {
            _server?.Idle(UnloadAfter(selected));
            _ = Task.Run(() => UnloadModelAsync(previous));
        }
    }

    public ISynthesisSession Begin()
    {
        TSettings settings;
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

    private protected abstract TSettings Select(TtsSettingsDto settings);

    private protected abstract bool Same(TSettings a, TSettings b);

    /// <summary>The request for one chunk; throws when the settings cannot be met (a voice that is gone).</summary>
    private protected abstract SpeechRequest Request(TSettings settings, string text);

    /// <summary>The clip as it plays.</summary>
    private protected virtual byte[] Clean(byte[] wav) => wav;

    /// <summary>
    /// One clip outside an utterance, with any settings of this model: starts the server and loads the model
    /// first, so the first call may take as long as a download.
    /// </summary>
    private protected async Task<byte[]> SynthesizeOnceAsync(TSettings settings, SpeechRequest request, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _sessions++;
        }

        _server?.Busy();
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            using (var load = CancellationTokenSource.CreateLinkedTokenSource(linked.Token))
            {
                load.CancelAfter(LoadTimeout);
                if (_server is not null)
                    await _server.EnsureRunningAsync(new Uri(settings.Endpoint), load.Token).ConfigureAwait(false);
                await _client.LoadModelAsync(settings, load.Token).ConfigureAwait(false);
            }

            linked.CancelAfter(ChunkTimeout);
            return Clean(await _client.SynthesizeAsync(settings, request, linked.Token).ConfigureAwait(false));
        }
        finally
        {
            End(settings);
        }
    }

    void End(TSettings settings)
    {
        bool left;
        lock (_gate)
        {
            _sessions--;
            if (_disposed || _sessions > 0)
                return;
            left = _leftWhileBusy;
            _leftWhileBusy = false;
        }

        _server?.Idle(UnloadAfter(settings));
        if (left)
            _ = Task.Run(() => UnloadModelAsync(settings));
    }

    async Task WarmUpAsync(TSettings settings)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        timeout.CancelAfter(LoadTimeout);
        try
        {
            if (_server is not null)
                await _server.EnsureRunningAsync(new Uri(settings.Endpoint), timeout.Token).ConfigureAwait(false);
            await _client.LoadModelAsync(settings, timeout.Token).ConfigureAwait(false);
            _log?.Invoke($"{Name} model loaded: {settings.Model}");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"{Name} warm-up failed: {ex.Message}");
        }

        bool idle;
        lock (_gate)
            idle = _sessions == 0 && !_disposed;
        if (idle)
            _server?.Idle(UnloadAfter(settings));
    }

    /// <summary>Only a running server can have the model; one that is not running is not started for this.</summary>
    async Task UnloadModelAsync(TSettings settings)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        timeout.CancelAfter(UnloadTimeout);
        try
        {
            await _client.UnloadModelAsync(settings, timeout.Token).ConfigureAwait(false);
            _log?.Invoke($"{Name} model unloaded: {settings.Model}");
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
        }
    }

    static TimeSpan? UnloadAfter(TSettings settings) =>
        settings.UnloadAfterMinutes > 0 ? TimeSpan.FromMinutes(settings.UnloadAfterMinutes) : null;

    sealed class Session(MlxSynthesizer<TSettings> owner, TSettings settings) : ISynthesisSession
    {
        public Task PrepareAsync(CancellationToken cancellationToken) =>
            owner._server?.EnsureRunningAsync(new Uri(settings.Endpoint), cancellationToken) ?? Task.CompletedTask;

        public async Task<SpeechAudio> SynthesizeAsync(string text, CancellationToken cancellationToken)
        {
            var request = owner.Request(settings, text);
            return new SpeechAudio(owner.Clean(await owner._client.SynthesizeAsync(settings, request, cancellationToken).ConfigureAwait(false)));
        }

        public void Dispose() => owner.End(settings);
    }
}
