using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using MachineVoice.Protocol;

namespace MachineVoice.App;

/// <summary>The menu bar icon: status, playback, mode, queue, history, settings and quit.</summary>
sealed class TrayMenu : IDisposable
{
    const int HistoryShown = 15;

    readonly TrayIcon _icon;
    readonly NativeMenuItem _status = new() { IsEnabled = false };
    readonly NativeMenuItem _pause = new("Пауза");
    readonly NativeMenuItem _stop = new("Стоп");
    readonly NativeMenuItem _skip = new("Пропустить");
    readonly NativeMenuItem _queue = new("Очередь") { Menu = new NativeMenu() };
    readonly NativeMenuItem _history = new("История") { Menu = new NativeMenu() };
    readonly Dictionary<PlaybackMode, NativeMenuItem> _modes = [];

    public TrayMenu(Application application)
    {
        var modes = new NativeMenu();
        foreach (var mode in new[] { PlaybackMode.Auto, PlaybackMode.Confirm, PlaybackMode.Silent })
        {
            var item = new NativeMenuItem(Labels.Mode(mode)) { ToggleType = MenuItemToggleType.Radio };
            item.Click += (_, _) => ModeRequested?.Invoke(mode);
            _modes[mode] = item;
            modes.Items.Add(item);
        }

        _pause.Click += (_, _) => PauseResumeRequested?.Invoke();
        _stop.Click += (_, _) => StopRequested?.Invoke();
        _skip.Click += (_, _) => SkipRequested?.Invoke();
        var settings = new NativeMenuItem("Настройки…");
        settings.Click += (_, _) => SettingsRequested?.Invoke();
        var quit = new NativeMenuItem("Выход");
        quit.Click += (_, _) => QuitRequested?.Invoke();

        var menu = new NativeMenu();
        menu.Items.Add(_status);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(_pause);
        menu.Items.Add(_stop);
        menu.Items.Add(_skip);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(new NativeMenuItem("Режим") { Menu = modes });
        menu.Items.Add(_queue);
        menu.Items.Add(_history);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(settings);
        menu.Items.Add(quit);

        _icon = new TrayIcon
        {
            Icon = new WindowIcon(RenderGlyph()),
            ToolTipText = "MachineVoice",
            Menu = menu,
            IsVisible = true,
        };
        MacOSProperties.SetIsTemplateIcon(_icon, true);
        // The native status item is created only once the icon is attached to the application.
        TrayIcon.SetIcons(application, [_icon]);
    }

    public event Action? PauseResumeRequested;
    public event Action? StopRequested;
    public event Action? SkipRequested;
    public event Action<PlaybackMode>? ModeRequested;
    public event Action<string>? OpenChatRequested;
    public event Action? SettingsRequested;
    public event Action? QuitRequested;

    public void Update(ControlState state)
    {
        var status = Labels.Player(state);
        _status.Header = status;
        _icon.ToolTipText = "MachineVoice — " + status;

        var resumable = state.Player == PlayerState.Paused || (state.Player == PlayerState.Idle && state.Holding && state.Queue.Count > 0);
        _pause.Header = resumable ? "Продолжить" : "Пауза";
        _pause.IsEnabled = resumable || state.Player == PlayerState.Speaking;
        _stop.IsEnabled = state.Player != PlayerState.Idle;
        _skip.IsEnabled = state.Current is not null || state.Confirmation is not null || state.Queue.Count > 0;

        foreach (var (mode, item) in _modes)
            item.IsChecked = mode == state.Mode;

        UpdateQueue(state);
        UpdateHistory(state);
    }

    public void Dispose()
    {
        _icon.IsVisible = false;
        _icon.Dispose();
    }

    void UpdateQueue(ControlState state)
    {
        var items = _queue.Menu!.Items;
        items.Clear();
        _queue.Header = state.Queue.Count == 0 ? "Очередь" : $"Очередь ({state.Queue.Count})";
        if (state.Queue.Count == 0)
        {
            items.Add(new NativeMenuItem("Пусто") { IsEnabled = false });
            return;
        }

        foreach (var queued in state.Queue)
            items.Add(new NativeMenuItem(Labels.Line(queued)) { IsEnabled = false });
    }

    void UpdateHistory(ControlState state)
    {
        var items = _history.Menu!.Items;
        items.Clear();
        if (state.History.Count == 0)
        {
            items.Add(new NativeMenuItem("Пусто") { IsEnabled = false });
            return;
        }

        foreach (var entry in state.History.TakeLast(HistoryShown).Reverse())
        {
            var time = entry.FinishedAt.ToLocalTime().ToString("HH:mm");
            var item = new NativeMenuItem($"{Labels.Outcome(entry.Outcome)} {time}  {Labels.Line(entry.Item)}")
            {
                ToolTip = "Открыть чат",
            };
            var id = entry.Item.Id;
            item.Click += (_, _) => OpenChatRequested?.Invoke(id);
            items.Add(item);
        }
    }

    static Bitmap RenderGlyph()
    {
        var bitmap = new RenderTargetBitmap(new PixelSize(36, 36), new Vector(144, 144));
        using (var context = bitmap.CreateDrawingContext())
        {
            // Mirrors Assets/tray-icon-bars.svg; Assets/tray-icon-sine.svg is the alternative design.
            var ring = new EllipseGeometry(new Rect(1.4, 1.4, 15.2, 15.2));
            var bars = StreamGeometry.Parse("M5,7.7 V10.3 M7,6 V12 M9,4.4 V13.6 M11,6.4 V11.6 M13,7.5 V10.5");
            context.DrawGeometry(null, new Pen(Brushes.Black, 1.4), ring);
            context.DrawGeometry(null, new Pen(Brushes.Black, 1.3, lineCap: PenLineCap.Round), bars);
        }

        return bitmap;
    }
}
