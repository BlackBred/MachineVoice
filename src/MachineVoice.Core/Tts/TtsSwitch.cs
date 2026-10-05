using MachineVoice.Protocol;

namespace MachineVoice.Core;

/// <summary>
/// Routes speech to the engine chosen in the settings. A change applies from the next utterance; the one being
/// read stays on its engine. When the neural engine fails, the system voice reads the rest of the utterance;
/// when the system voice fails too, the last resort does. A neural engine is created the first time it is
/// selected. Owns all engines. After a fallback the times continue from where the failed engine stopped, and a
/// seek reaches only the rest that the next engine reads.
/// </summary>
public sealed class TtsSwitch : ISeekableTtsEngine, IConfigurableTts, IDisposable
{
    // Engines are called outside _gate: AVSpeechSynthesizer may wait for the main queue, where its callbacks
    // run and would wait for _gate in turn.
    readonly object _gate = new();
    readonly ITtsEngine _system;
    readonly ITtsEngine? _lastResort;
    readonly IReadOnlyDictionary<TtsEngineKind, Func<ITtsEngine>> _factories;
    readonly Dictionary<TtsEngineKind, ITtsEngine> _neural = [];
    readonly Action<string>? _log;
    TtsSettingsDto _settings = new();
    Route? _route;
    bool _disposed;

    /// <param name="neural">The engines other than the system voice, by the setting that selects them.</param>
    public TtsSwitch(
        ITtsEngine system,
        IReadOnlyDictionary<TtsEngineKind, Func<ITtsEngine>> neural,
        Action<string>? log = null,
        ITtsEngine? lastResort = null)
    {
        _system = system;
        _lastResort = lastResort;
        _factories = neural;
        _log = log;
        Subscribe(system);
        if (lastResort is not null)
            Subscribe(lastResort);
    }

    public event EventHandler<TtsProgressEventArgs>? Progress;
    public event EventHandler<TtsCompletedEventArgs>? Completed;
    public event EventHandler<TtsPositionEventArgs>? PositionChanged;

    /// <summary>The selected engine gets the settings first: it takes the speech server before another one leaves it.</summary>
    public void Apply(TtsSettingsDto settings)
    {
        List<ITtsEngine> engines;
        lock (_gate)
        {
            if (_disposed)
                return;
            _settings = settings;
            if (!_neural.ContainsKey(settings.Engine) && _factories.TryGetValue(settings.Engine, out var create))
            {
                var created = create();
                _neural[settings.Engine] = created;
                Subscribe(created);
            }

            engines = _neural.OrderBy(pair => pair.Key == settings.Engine ? 0 : 1).Select(pair => pair.Value).ToList();
        }

        (_system as IConfigurableTts)?.Apply(settings);
        (_lastResort as IConfigurableTts)?.Apply(settings);
        foreach (var engine in engines)
            (engine as IConfigurableTts)?.Apply(settings);
    }

    public void Speak(string utteranceId, string text)
    {
        ITtsEngine engine;
        ITtsEngine? previous;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            engine = _neural.GetValueOrDefault(_settings.Engine) ?? _system;
            previous = _route?.Engine;
            _route = new Route(engine, utteranceId, 0, 0);
        }

        if (previous is not null && previous != engine)
            previous.Stop();
        engine.Speak(utteranceId, text);
    }

    public void Pause() => Routed()?.Pause();

    public void Resume() => Routed()?.Resume();

    public void Seek(double position)
    {
        ISeekableTtsEngine? engine;
        double offset;
        lock (_gate)
        {
            engine = _route?.Engine as ISeekableTtsEngine;
            offset = _route?.TimeOffset ?? 0;
        }

        engine?.Seek(Math.Max(position - offset, 0));
    }

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
        List<ITtsEngine> neural;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _route = null;
            neural = [.. _neural.Values];
        }

        foreach (var engine in neural)
            (engine as IDisposable)?.Dispose();
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
        if (engine is ISeekableTtsEngine seekable)
            seekable.PositionChanged += (_, args) => OnPosition(engine, args);
    }

    void OnPosition(ITtsEngine engine, TtsPositionEventArgs args)
    {
        double offset;
        lock (_gate)
        {
            if (!Matches(engine, args.UtteranceId))
                return;
            offset = _route!.TimeOffset;
            _route.Position = args.Position + offset;
        }

        PositionChanged?.Invoke(this, offset == 0
            ? args
            : new TtsPositionEventArgs(args.UtteranceId, args.Position + offset, args.Duration + offset));
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
            next = engine == _system ? _lastResort : _neural.ContainsValue(engine) ? _system : null;
            var failed = _route!;
            _route = next is null ? null : new Route(next, args.UtteranceId, failed.WordOffset + args.WordOffset, failed.Position);
        }

        if (next is null)
        {
            Completed?.Invoke(this, new TtsCompletedEventArgs(args.UtteranceId));
            return;
        }

        // Without times from the next engine the progress goes back to words.
        if (next is not ISeekableTtsEngine)
            PositionChanged?.Invoke(this, new TtsPositionEventArgs(args.UtteranceId, 0, 0));

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

    /// <param name="TimeOffset">Seconds of the utterance read before <paramref name="Engine"/> took over.</param>
    sealed record Route(ITtsEngine Engine, string Id, int WordOffset, double TimeOffset)
    {
        /// <summary>The last position in the whole utterance; guarded by _gate.</summary>
        public double Position { get; set; } = TimeOffset;
    }
}
