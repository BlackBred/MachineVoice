using System.Net.Sockets;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using MachineVoice.Core;
using MachineVoice.Platform.MacOS;
using MachineVoice.Protocol;

namespace MachineVoice.App;

/// <summary>
/// Hosts the core in this process and drives the UI from the control protocol only: commands go through
/// <see cref="IControlClient"/>, the UI reads <see cref="ControlState"/> that the event stream keeps current.
/// </summary>
sealed class AppController
{
    // Between two responses the player is idle for a moment; hiding at once would make the pill blink.
    static readonly TimeSpan HideDelay = TimeSpan.FromMilliseconds(700);
    const double RateStep = 0.1;

    readonly Application _application;
    readonly IClassicDesktopStyleApplicationLifetime _desktop;
    readonly LaunchOptions _options;
    readonly AppLog _log;
    readonly ControlState _state = new();
    readonly CancellationTokenSource _pump = new();
    readonly DispatcherTimer _hideTimer;
    readonly List<PosixSignalRegistration> _signals = [];

    TtsSwitch? _tts;
    SharedSpeechServer? _speechServer;
    OmniVoiceSynthesizer? _omniVoice;
    MachineVoiceHost? _host;
    IControlClient? _client;
    TrayMenu? _tray;
    OverlayWindow? _overlay;
    SettingsWindow? _settings;
    bool _quitting;

    public AppController(Application application, IClassicDesktopStyleApplicationLifetime desktop, LaunchOptions options)
    {
        _application = application;
        _desktop = desktop;
        _options = options;
        _log = new AppLog(options.RootDirectory);
        _hideTimer = new DispatcherTimer { Interval = HideDelay };
        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer.Stop();
            if (!OverlayWanted())
                _overlay?.HideOverlay();
        };
    }

    public async Task StartAsync()
    {
        var controlSocket = MachineVoiceHost.ControlSocketIn(_options.RootDirectory);
        if (IsListening(controlSocket))
        {
            // A second daemon would replay the inbox and read the same responses again.
            _log.Write($"MachineVoice is already running ({controlSocket}); exiting.");
            _desktop.Shutdown(1);
            return;
        }

        try
        {
            // Qwen3-TTS and OmniVoice share one mlx_audio.server process.
            var speechServer = _speechServer = new SharedSpeechServer(new MlxAudioServer(_options.RootDirectory, _log.Write));
            var voices = new VoiceLibrary(Path.Combine(_options.RootDirectory, VoiceLibrary.DirectoryName), _log.Write);
            var omniVoice = _omniVoice = new OmniVoiceSynthesizer(voices, speechServer.Lease(), log: _log.Write);
            _tts = new TtsSwitch(
                AvSpeechWriter.Engine(new AvAudioPlayer(), new AvSpeechOptions { Log = _log.Write }),
                new Dictionary<TtsEngineKind, Func<ITtsEngine>>
                {
                    [TtsEngineKind.Qwen] = () => QwenSynthesizer.Engine(new AvAudioPlayer(), speechServer.Lease(), log: _log.Write),
                    [TtsEngineKind.OmniVoice] = () => OmniVoiceSynthesizer.Engine(new AvAudioPlayer(), omniVoice, _log.Write),
                },
                _log.Write,
                lastResort: new AvSpeechEngine(new AvSpeechOptions { Log = _log.Write }));
            _host = await MachineVoiceHost.StartAsync(new MachineVoiceOptions
            {
                RootDirectory = _options.RootDirectory,
                CursorDirectory = _options.CursorDirectory,
                Tts = _tts,
                McpServerBinary = _options.McpServerBinary,
                Log = _log.Write,
                Voices = new VoiceStudio(voices, omniVoice),
            });
            _client = await _host.ConnectInProcessAsync();
            var snapshot = await _client.GetSnapshotAsync();
            if (snapshot.Snapshot is not null)
                _state.Reset(snapshot.Snapshot);
        }
        catch (Exception ex)
        {
            _log.Write($"Start failed: {ex}");
            await StopCoreAsync();
            _desktop.Shutdown(1);
            return;
        }

        _log.Write($"Started: {_options.RootDirectory}");
        try
        {
            CreateUi();
            _ = PumpAsync(_client, _pump.Token);
            foreach (var signal in new[] { PosixSignal.SIGTERM, PosixSignal.SIGINT })
            {
                _signals.Add(PosixSignalRegistration.Create(signal, context =>
                {
                    context.Cancel = true;
                    Dispatcher.UIThread.Post(() => _ = QuitAsync());
                }));
            }

            Refresh();
            if (!_state.Settings.Sources.Any(source => source.Name == "cursor"))
                ShowSettings();
        }
        catch (Exception ex)
        {
            _log.Write($"UI start failed: {ex}");
            await QuitAsync();
        }
    }

    void CreateUi()
    {
        _overlay = new OverlayWindow();
        _overlay.SeekRequested += Seek;
        _overlay.SlowerRequested += () => StepRate(-RateStep);
        _overlay.FasterRequested += () => StepRate(RateStep);
        _overlay.PauseResumeRequested += PauseResume;
        _overlay.SkipRequested += () => Send(client => client.SkipAsync());
        _overlay.OpenChatRequested += () =>
        {
            if ((_state.Current ?? _state.Confirmation)?.Id is { } id)
                Send(client => client.OpenChatAsync(id));
        };
        _overlay.ListenRequested += () =>
        {
            if (_state.Confirmation?.Id is { } id)
                Send(client => client.ListenAsync(id));
        };
        _overlay.DismissRequested += () =>
        {
            if (_state.Confirmation?.Id is { } id)
                Send(client => client.DismissAsync(id));
        };

        _tray = new TrayMenu(_application);
        _tray.PauseResumeRequested += PauseResume;
        _tray.SkipRequested += () => Send(client => client.SkipAsync());
        _tray.ModeRequested += mode => Send(client => client.SetModeAsync(mode));
        _tray.ListenRequested += id => Send(client => client.ListenAsync(id));
        _tray.OpenChatRequested += id => Send(client => client.OpenChatAsync(id));
        _tray.SettingsRequested += ShowSettings;
        _tray.QuitRequested += () => _ = QuitAsync();
        MacApplication.UseAccessoryPolicy();
    }

    async Task PumpAsync(IControlClient client, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var message in client.EventsAsync(cancellationToken).ConfigureAwait(false))
                Dispatcher.UIThread.Post(() => OnEvent(message));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _log.Write($"Event stream failed: {ex}");
        }
    }

    void OnEvent(EventMessage message)
    {
        if (_quitting)
            return;
        if (message is ChatRequestedEvent chat)
        {
            OpenChat(chat);
            return;
        }

        if (!_state.Apply(message))
            return;
        if (_state.NeedsSnapshot)
            _ = ResyncAsync();

        if (message is PlayerProgressEvent or PlayerPositionEvent)
        {
            _overlay?.UpdateProgress(_state);
            return;
        }

        Refresh();
    }

    async Task ResyncAsync()
    {
        if (_client is null)
            return;
        var result = await _client.GetSnapshotAsync();
        if (result.Snapshot is null)
            return;
        _state.Reset(result.Snapshot);
        Refresh();
    }

    void Refresh()
    {
        _tray?.Update(_state);
        _settings?.Update(_state);
        if (_overlay is null)
            return;

        if (OverlayWanted())
        {
            _hideTimer.Stop();
            _overlay.Update(_state);
            _overlay.ShowOverlay();
        }
        else if (_overlay.IsVisible && !_hideTimer.IsEnabled)
        {
            _hideTimer.Start();
        }
    }

    bool OverlayWanted() =>
        (_state.Player != PlayerState.Idle && _state.Current is not null) || _state.Confirmation is not null;

    void PauseResume()
    {
        if (_state.Player == PlayerState.Speaking)
            Send(client => client.PauseAsync());
        else
            Send(client => client.ResumeAsync());
    }

    /// <summary>
    /// Exact time under the click. Snapping to the nearest word or pause would be possible, but is not done for now.
    /// The plain system voice reports no duration, so there is nothing to seek in.
    /// </summary>
    void Seek(double fraction)
    {
        var duration = _state.Duration;
        if (_state.Current is null || duration <= 0)
            return;
        Send(client => client.SeekAsync(fraction * duration));
    }

    void StepRate(double step)
    {
        var current = _state.Settings.Tts.PlaybackRate;
        var next = Math.Clamp(Math.Round(current + step, 1), TtsSettingsDto.MinPlaybackRate, TtsSettingsDto.MaxPlaybackRate);
        if (next != current)
            Send(client => client.SetPlaybackRateAsync(next));
    }

    void OpenChat(ChatRequestedEvent chat)
    {
        // Until a Cursor bridge plugin can open a specific chat, bringing Cursor forward is the best available.
        if (chat.Source == "cursor")
            MacApplication.Open(MacApplication.CursorBundleId, _log.Write);
        else
            _log.Write($"No way to open a chat of source {chat.Source}.");
    }

    void ShowSettings()
    {
        if (_client is null || _quitting)
            return;
        if (_settings is null)
        {
            _settings = new SettingsWindow(_client, _state, _log.Write);
            _settings.Closed += (_, _) => _settings = null;
        }

        MacApplication.Activate();
        _settings.Show();
        _settings.Activate();
    }

    void Send(Func<IControlClient, Task<ResultMessage>> command)
    {
        if (_client is not { } client || _quitting)
            return;
        _ = SendAsync(client, command);
    }

    async Task SendAsync(IControlClient client, Func<IControlClient, Task<ResultMessage>> command)
    {
        try
        {
            var result = await command(client);
            if (!result.Ok)
                _log.Write($"Command rejected: {result.Error}");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.Write($"Command failed: {ex.Message}");
        }
    }

    async Task QuitAsync()
    {
        if (_quitting)
            return;
        _quitting = true;
        _log.Write("Quitting.");

        _hideTimer.Stop();
        _settings?.Close();
        _overlay?.Close();
        _pump.Cancel();
        await StopCoreAsync();
        _tray?.Dispose();
        foreach (var signal in _signals)
            signal.Dispose();
        _desktop.Shutdown();
    }

    async Task StopCoreAsync()
    {
        try
        {
            if (_client is not null)
                await _client.DisposeAsync();
            if (_host is not null)
                await _host.DisposeAsync();
        }
        catch (Exception ex)
        {
            _log.Write($"Shutdown failed: {ex}");
        }

        // The OmniVoice engine disposes its synthesizer too, if it was ever selected.
        _tts?.Dispose();
        _omniVoice?.Dispose();
        _speechServer?.Dispose();
        _client = null;
        _host = null;
        _tts = null;
        _omniVoice = null;
        _speechServer = null;
    }

    static bool IsListening(string socketPath)
    {
        if (!File.Exists(socketPath))
            return false;
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            socket.Connect(new UnixDomainSocketEndPoint(socketPath));
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}
