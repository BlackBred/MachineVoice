using MachineVoice.Protocol;

namespace MachineVoice.Core;

/// <summary>
/// Routes speech to the engine chosen in the settings. A change applies from the next utterance; the one being
/// read stays on its engine. When the neural engine fails, the system voice reads the rest of the utterance;
/// when the system voice fails too, the last resort does. The Qwen engine is created the first time it is
/// selected. Owns all engines.
/// </summary>
public sealed class TtsSwitch : ITtsEngine, IConfigurableTts, IDisposable
{
    // Engines are called outside _gate: AVSpeechSynthesizer may wait for the main queue, where its callbacks
    // run and would wait for _gate in turn.
    readonly object _gate = new();
    readonly ITtsEngine _system;
    readonly ITtsEngine? _lastResort;
    readonly Func<ITtsEngine> _createQwen;
    readonly Action<string>? _log;
    TtsSettingsDto _settings = new();
    ITtsEngine? _qwen;
    Route? _route;
    bool _disposed;

    public TtsSwitch(ITtsEngine system, Func<ITtsEngine> createQwen, Action<string>? log = null, ITtsEngine? lastResort = null)
    {
        _system = system;
        _lastResort = lastResort;
        _createQwen = createQwen;
        _log = log;
        Subscribe(system);
        if (lastResort is not null)
            Subscribe(lastResort);
    }

    public event EventHandler<TtsProgressEventArgs>? Progress;
    public event EventHandler<TtsCompletedEventArgs>? Completed;

    public void Apply(TtsSettingsDto settings)
    {
        ITtsEngine? qwen;
        lock (_gate)
        {
            if (_disposed)
                return;
            _settings = settings;
            if (_qwen is null && settings.Engine == TtsEngineKind.Qwen)
            {
                _qwen = _createQwen();
                Subscribe(_qwen);
            }

            qwen = _qwen;
        }

        (_system as IConfigurableTts)?.Apply(settings);
        (_lastResort as IConfigurableTts)?.Apply(settings);
        (qwen as IConfigurableTts)?.Apply(settings);
    }

    public void Speak(string utteranceId, string text)
    {
        ITtsEngine engine;
        ITtsEngine? previous;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            engine = _settings.Engine == TtsEngineKind.Qwen && _qwen is not null ? _qwen : _system;
            previous = _route?.Engine;
            _route = new Route(engine, utteranceId, 0);
        }

        if (previous is not null && previous != engine)
            previous.Stop();
        engine.Speak(utteranceId, text);
    }

    public void Pause() => Routed()?.Pause();

    public void Resume() => Routed()?.Resume();

    public void Stop()
    {
        ITtsEngine? engine;
        lock (_gate)
        {
            engine = _route?.Engine;
            _route = null;
        }

        engine?.Stop();
    }

    public void Dispose()
    {
        ITtsEngine? qwen;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _route = null;
            qwen = _qwen;
        }

        (qwen as IDisposable)?.Dispose();
        (_system as IDisposable)?.Dispose();
        (_lastResort as IDisposable)?.Dispose();
    }

    ITtsEngine? Routed()
    {
        lock (_gate)
            return _route?.Engine;
    }

    void Subscribe(ITtsEngine engine)
    {
        engine.Progress += (_, args) => OnProgress(engine, args);
        engine.Completed += (_, args) => OnCompleted(engine, args);
        if (engine is IFallibleTtsEngine fallible)
            fallible.Failed += (_, args) => OnFailed(engine, args);
    }

    void OnProgress(ITtsEngine engine, TtsProgressEventArgs args)
    {
        int offset;
        lock (_gate)
        {
            if (!Matches(engine, args.UtteranceId))
                return;
            offset = _route!.WordOffset;
        }

        Progress?.Invoke(this, offset == 0 ? args : new TtsProgressEventArgs(args.UtteranceId, args.WordIndex + offset, args.Word));
    }

    void OnCompleted(ITtsEngine engine, TtsCompletedEventArgs args)
    {
        lock (_gate)
        {
            if (!Matches(engine, args.UtteranceId))
                return;
            _route = null;
        }

        Completed?.Invoke(this, args);
    }

    void OnFailed(ITtsEngine engine, TtsFailedEventArgs args)
    {
        ITtsEngine? next;
        lock (_gate)
        {
            if (!Matches(engine, args.UtteranceId))
                return;
            next = engine == _system ? _lastResort : engine == _qwen ? _system : null;
            _route = next is null ? null : new Route(next, args.UtteranceId, _route!.WordOffset + args.WordOffset);
        }

        if (next is null)
        {
            Completed?.Invoke(this, new TtsCompletedEventArgs(args.UtteranceId));
            return;
        }

        if (string.IsNullOrWhiteSpace(args.RemainingText))
        {
            OnCompleted(next, new TtsCompletedEventArgs(args.UtteranceId));
            return;
        }

        _log?.Invoke(next == _system ? "The system voice reads the rest of the response." : "The plain system voice reads the rest of the response.");
        try
        {
            next.Speak(args.UtteranceId, args.RemainingText);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"System voice failed: {ex.Message}");
            OnCompleted(next, new TtsCompletedEventArgs(args.UtteranceId));
        }
    }

    bool Matches(ITtsEngine engine, string utteranceId) =>
        _route is { } route && route.Engine == engine && route.Id == utteranceId;

    sealed record Route(ITtsEngine Engine, string Id, int WordOffset);
}
