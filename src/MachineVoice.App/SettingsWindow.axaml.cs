using Avalonia.Controls;
using Avalonia.Interactivity;
using MachineVoice.Protocol;

namespace MachineVoice.App;

/// <summary>Settings that only the user changes: sources, the MCP entry, the LLM endpoint and key, the TTS engine.</summary>
public partial class SettingsWindow : Window
{
    const string CursorSource = "cursor";

    readonly IControlClient _client = null!;
    readonly ControlState _state = new();
    readonly Action<string> _log = _ => { };
    SummarySettingsDto _shownSummary = new();
    TtsSettingsDto _shownTts = new();
    bool _updating;
    bool _busy;

    /// <summary>For the XAML previewer only.</summary>
    public SettingsWindow()
    {
        InitializeComponent();
    }

    public SettingsWindow(IControlClient client, ControlState state, Action<string> log) : this()
    {
        _client = client;
        _state = state;
        _log = log;
        ShowSummary(state.Settings.Summary);
        ShowTts(state.Settings.Tts);
        Update(state);
        Opened += async (_, _) => await RefreshStatusesAsync();
    }

    public void Update(ControlState state)
    {
        _updating = true;
        ModeAuto.IsChecked = state.Mode == PlaybackMode.Auto;
        ModeConfirm.IsChecked = state.Mode == PlaybackMode.Confirm;
        ModeSilent.IsChecked = state.Mode == PlaybackMode.Silent;
        var rate = (decimal)state.Settings.Tts.PlaybackRate;
        if (PlaybackRate.Value != rate)
            PlaybackRate.Value = rate;
        _updating = false;

        state.Sources.TryGetValue(CursorSource, out var cursor);
        var cursorStatus = cursor?.Status ?? SourceConnectionStatus.Disconnected;
        CursorStatusText.Text = Labels.CursorStatus(cursorStatus);
        CursorButton.Content = cursorStatus == SourceConnectionStatus.Connected ? "Отключить Cursor" : "Подключить Cursor";
        CursorButton.IsEnabled = !_busy;
        LegacyWarning.IsVisible = cursor?.LegacySpeaker == true;

        if (state.Mcp is { } mcp)
        {
            McpStatusText.Text = Labels.McpStatus(mcp);
            McpButton.Content = DisconnectsMcp(mcp) ? "Отключить MCP от Cursor" : "Подключить MCP к Cursor";
            McpButton.IsEnabled = !_busy && (DisconnectsMcp(mcp) || mcp.Available);
            if (mcp.Status != SourceConnectionStatus.Connected)
                McpHint.IsVisible = false;
        }
        else
        {
            McpStatusText.Text = "Проверяется…";
            McpButton.Content = "Подключить MCP к Cursor";
            McpButton.IsEnabled = false;
        }

        // Settings changed elsewhere (the MCP tool) replace the fields only while the user has not edited them.
        if (Same(ReadSummary(), _shownSummary) && !Same(state.Settings.Summary, _shownSummary))
            ShowSummary(state.Settings.Summary);
        if (Same(ReadTts(), _shownTts) && !Same(state.Settings.Tts, _shownTts))
            ShowTts(state.Settings.Tts);
    }

    static bool DisconnectsMcp(McpStatusDto mcp) =>
        mcp.Status == SourceConnectionStatus.Connected || (mcp.Status == SourceConnectionStatus.Stale && !mcp.Available);

    async Task RefreshStatusesAsync()
    {
        try
        {
            var cursor = await _client.GetSourceStatusAsync(CursorSource);
            if (cursor is { Ok: true, Source: { } source })
                _state.SetSource(source);
            var mcp = await _client.GetMcpStatusAsync();
            if (mcp is { Ok: true, Mcp: { } status })
                _state.SetMcp(status);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log($"Settings status refresh failed: {ex.Message}");
        }

        Update(_state);
    }

    async void OnCursorClick(object? sender, RoutedEventArgs e)
    {
        if (_busy)
            return;

        _state.Sources.TryGetValue(CursorSource, out var current);
        var disconnect = current?.Status == SourceConnectionStatus.Connected;
        CursorError.IsVisible = false;
        await RunAsync(async () =>
        {
            var result = disconnect
                ? await _client.DisconnectSourceAsync(CursorSource)
                : await _client.ConnectSourceAsync(CursorSource);
            if (result is { Ok: true, Source: { } source })
            {
                _state.SetSource(source);
                return;
            }

            _log($"Cursor {(disconnect ? "disconnect" : "connect")} failed: {result.Error}");
            CursorError.Text = "Не удалось изменить ~/.cursor/hooks.json: файл не разобрать или не записать. Исправьте его вручную и попробуйте снова.";
            CursorError.IsVisible = true;
        });
    }

    async void OnMcpClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _state.Mcp is not { } current)
            return;

        var disconnect = DisconnectsMcp(current);
        McpError.IsVisible = false;
        await RunAsync(async () =>
        {
            var result = disconnect ? await _client.DisconnectMcpAsync() : await _client.ConnectMcpAsync();
            if (result is { Ok: true, Mcp: { } status })
            {
                _state.SetMcp(status);
                McpHint.IsVisible = !disconnect && status.Status == SourceConnectionStatus.Connected;
                return;
            }

            _log($"MCP {(disconnect ? "disconnect" : "connect")} failed: {result.Error}");
            McpError.Text = current.Available
                ? "Не удалось изменить ~/.cursor/mcp.json: файл не разобрать или не записать. Исправьте его вручную и попробуйте снова."
                : "В этой сборке нет MCP-сервера.";
            McpError.IsVisible = true;
        });
    }

    void OnModeChecked(object? sender, RoutedEventArgs e)
    {
        if (_updating || sender is not RadioButton { IsChecked: true } button)
            return;

        var mode = button == ModeConfirm ? PlaybackMode.Confirm
            : button == ModeSilent ? PlaybackMode.Silent
            : PlaybackMode.Auto;
        if (mode != _state.Mode)
            _ = RunAsync(async () => await _client.SetModeAsync(mode));
    }

    async void OnSaveSummary(object? sender, RoutedEventArgs e)
    {
        var summary = ReadSummary();
        SummaryStatus.Text = "";
        await RunAsync(async () =>
        {
            var result = await _client.UpdateSettingsAsync(summary: summary);
            if (result.Ok)
            {
                _shownSummary = summary;
                SummaryStatus.Text = "Сохранено";
                return;
            }

            SummaryStatus.Text = result.Error == ProtocolErrors.InvalidArgument
                ? $"Проверьте поля: адрес http(s), модель обязательна для пересказа, тайм-аут от 1 до {SummarySettingsDto.MaxTimeoutSeconds} с."
                : "Не удалось сохранить: " + result.Error;
        });
    }

    void OnEngineChecked(object? sender, RoutedEventArgs e)
    {
        QwenFields.IsEnabled = EngineQwen.IsChecked == true;
        SystemFields.IsEnabled = EngineSystem.IsChecked == true;
    }

    // Not through RunAsync: its Update would put the old rate back into the field before the core confirms.
    void OnPlaybackRateChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (_updating || e.NewValue is not { } value)
            return;
        var rate = (double)value;
        if (rate != _state.Settings.Tts.PlaybackRate)
            _ = SetPlaybackRateAsync(rate);
    }

    async Task SetPlaybackRateAsync(double rate)
    {
        try
        {
            var result = await _client.SetPlaybackRateAsync(rate);
            if (!result.Ok)
                _log($"Playback rate rejected: {result.Error}");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log($"Playback rate failed: {ex.Message}");
        }
    }

    async void OnSaveTts(object? sender, RoutedEventArgs e)
    {
        var tts = ReadTts();
        TtsStatus.Text = "";
        await RunAsync(async () =>
        {
            var result = await _client.UpdateSettingsAsync(tts: tts);
            if (result.Ok)
            {
                _shownTts = tts;
                TtsStatus.Text = tts.Engine == TtsEngineKind.Qwen
                    ? "Сохранено. Модель загружается в фоне, первая фраза может подождать."
                    : "Сохранено";
                return;
            }

            TtsStatus.Text = result.Error == ProtocolErrors.InvalidArgument
                ? $"Проверьте поля: адрес http(s), модель и голос обязательны, простой от 0 до {QwenTtsSettingsDto.MaxUnloadAfterMinutes} мин."
                : "Не удалось сохранить: " + result.Error;
        });
    }

    async Task RunAsync(Func<Task> action)
    {
        _busy = true;
        Update(_state);
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log($"Settings command failed: {ex}");
        }
        finally
        {
            _busy = false;
            Update(_state);
        }
    }

    void ShowSummary(SummarySettingsDto summary)
    {
        SummaryEnabled.IsChecked = summary.Enabled;
        SummaryEndpoint.Text = summary.Endpoint;
        SummaryModel.Text = summary.Model;
        SummaryApiKey.Text = summary.ApiKey ?? "";
        SummaryTimeout.Value = summary.TimeoutSeconds;
        _shownSummary = ReadSummary();
    }

    SummarySettingsDto ReadSummary() => new()
    {
        Enabled = SummaryEnabled.IsChecked == true,
        Endpoint = string.IsNullOrWhiteSpace(SummaryEndpoint.Text) ? SummarySettingsDto.DefaultEndpoint : SummaryEndpoint.Text.Trim(),
        Model = SummaryModel.Text?.Trim() ?? "",
        ApiKey = string.IsNullOrWhiteSpace(SummaryApiKey.Text) ? null : SummaryApiKey.Text.Trim(),
        TimeoutSeconds = (int)(SummaryTimeout.Value ?? SummarySettingsDto.DefaultTimeoutSeconds),
    };

    void ShowTts(TtsSettingsDto tts)
    {
        EngineSystem.IsChecked = tts.Engine == TtsEngineKind.System;
        EngineQwen.IsChecked = tts.Engine == TtsEngineKind.Qwen;
        QwenFields.IsEnabled = tts.Engine == TtsEngineKind.Qwen;
        SystemFields.IsEnabled = tts.Engine == TtsEngineKind.System;
        SystemRate.Value = (decimal)tts.SystemVoice.Rate;

        var voices = QwenTtsSettingsDto.Voices.ToList();
        if (!voices.Contains(tts.Qwen.Voice))
            voices.Insert(0, tts.Qwen.Voice);
        QwenVoice.ItemsSource = voices;
        QwenVoice.SelectedItem = tts.Qwen.Voice;
        QwenEndpoint.Text = tts.Qwen.Endpoint;
        QwenModel.Text = tts.Qwen.Model;
        QwenUnload.Value = tts.Qwen.UnloadAfterMinutes;
        _shownTts = ReadTts();
    }

    TtsSettingsDto ReadTts() => new()
    {
        Engine = EngineQwen.IsChecked == true ? TtsEngineKind.Qwen : TtsEngineKind.System,
        PlaybackRate = _state.Settings.Tts.PlaybackRate,
        SystemVoice = new SystemVoiceSettingsDto
        {
            Rate = (double)(SystemRate.Value ?? (decimal)SystemVoiceSettingsDto.DefaultRate),
        },
        Qwen = new QwenTtsSettingsDto
        {
            Endpoint = string.IsNullOrWhiteSpace(QwenEndpoint.Text) ? QwenTtsSettingsDto.DefaultEndpoint : QwenEndpoint.Text.Trim(),
            Model = string.IsNullOrWhiteSpace(QwenModel.Text) ? QwenTtsSettingsDto.DefaultModel : QwenModel.Text.Trim(),
            Voice = QwenVoice.SelectedItem as string ?? QwenTtsSettingsDto.DefaultVoice,
            UnloadAfterMinutes = (int)(QwenUnload.Value ?? QwenTtsSettingsDto.DefaultUnloadAfterMinutes),
        },
    };

    // The playback rate is not compared: the field applies it at once.
    static bool Same(TtsSettingsDto a, TtsSettingsDto b) =>
        a.Engine == b.Engine
        && a.SystemVoice.Rate == b.SystemVoice.Rate
        && a.Qwen.Endpoint == b.Qwen.Endpoint
        && a.Qwen.Model == b.Qwen.Model
        && a.Qwen.Voice == b.Qwen.Voice
        && a.Qwen.UnloadAfterMinutes == b.Qwen.UnloadAfterMinutes;

    static bool Same(SummarySettingsDto a, SummarySettingsDto b) =>
        a.Enabled == b.Enabled
        && a.Endpoint == b.Endpoint
        && a.Model == b.Model
        && (a.ApiKey ?? "") == (b.ApiKey ?? "")
        && a.TimeoutSeconds == b.TimeoutSeconds;
}
