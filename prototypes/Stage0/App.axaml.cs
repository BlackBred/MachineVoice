using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using MachineVoice.Stage0.Interop;
using MachineVoice.Stage0.Tts;

namespace MachineVoice.Stage0;

public partial class App : Application
{
    private const string TestPhrase =
        "Cursor, проект MachineVoice. Это проверка прототипа. Сейчас должна сработать пауза по горячей клавише, " +
        "а потом продолжение с того же места. Если вы слышите этот текст целиком и без повторов, " +
        "значит сигналы стоп и продолжить работают как надо.";

    // Carbon virtual key codes (kVK_ANSI_*).
    private const uint KeyP = 0x23;
    private const uint KeyS = 0x01;
    private const uint KeyO = 0x1F;
    private const uint KeyT = 0x11;

    private ISpeaker _speaker = null!;
    private OverlayWindow? _overlay;
    private TrayIcon? _tray;
    private IClassicDesktopStyleApplicationLifetime? _desktop;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            desktop.Exit += (_, _) => CarbonHotkeys.UnregisterAll();

            _speaker = Program.UseSay ? new SaySpeaker() : new AvSpeaker("ru-RU");
            _overlay = new OverlayWindow();
            _overlay.SpeakRequested += SpeakTest;
            _overlay.PauseRequested += _speaker.TogglePause;
            _overlay.StopRequested += _speaker.Stop;
            _speaker.StateChanged += state => Dispatcher.UIThread.Post(() => OnSpeakerState(state));

            _tray = CreateTrayIcon();
            RegisterHotkeys();
            MacApp.Policy = Program.OverlayPolicy;

            Dispatcher.UIThread.Post(() =>
            {
                _overlay.ShowOverlay();
                Log.Write("оверлей показан:" + Environment.NewLine + MacOverlay.Describe(_overlay.NSWindow));
                Log.Write(MacApp.FocusSummary);
                DispatcherTimer.RunOnce(() =>
                {
                    Log.Write($"кнопка «Пауза» на экране: {_overlay.PauseButtonScreenCenter}");
                    Log.Write(MacApp.DescribeWindows());
                }, TimeSpan.FromMilliseconds(500));
                if (Program.DiagnoseFor is { } diagnoseFor)
                {
                    var last = "";
                    var started = DateTime.Now;
                    DispatcherTimer.Run(() =>
                    {
                        var now = MacApp.FocusSummary;
                        if (now != last)
                            Log.Write($"+{(DateTime.Now - started).TotalMilliseconds:F0} мс: {now}");
                        last = now;
                        return DateTime.Now - started < diagnoseFor;
                    }, TimeSpan.FromMilliseconds(50));
                    DispatcherTimer.RunOnce(Diagnose, diagnoseFor + TimeSpan.FromMilliseconds(200));
                }
            });
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void Diagnose()
    {
        Log.Write("итоговая диагностика:" + Environment.NewLine + MacOverlay.Describe(_overlay!.NSWindow));
        Log.Write(MacApp.FocusSummary);
        _overlay.HideOverlay();
        _overlay.ShowOverlay();
        Log.Write("после повторного показа:" + Environment.NewLine + MacOverlay.Describe(_overlay.NSWindow));
        _desktop?.Shutdown();
    }

    private void SpeakTest() => _speaker.Speak(TestPhrase);

    private void OnSpeakerState(SpeakerState state)
    {
        var text = state switch
        {
            SpeakerState.Speaking => $"говорит: {_speaker.Name}",
            SpeakerState.Paused => $"пауза: {_speaker.Name}",
            _ => "тишина",
        };
        _overlay?.SetStatus(text);
        Log.Write($"плеер: {text}");
    }

    private void ToggleOverlay()
    {
        if (_overlay is null)
            return;
        if (_overlay.IsVisible) _overlay.HideOverlay();
        else _overlay.ShowOverlay();
    }

    private void RegisterHotkeys()
    {
        const uint mods = CarbonHotkeys.ControlKey | CarbonHotkeys.OptionKey;
        Register("⌃⌥P пауза / продолжить", KeyP, mods, _speaker.TogglePause);
        Register("⌃⌥S стоп", KeyS, mods, _speaker.Stop);
        Register("⌃⌥T тестовая фраза", KeyT, mods, SpeakTest);
        Register("⌃⌥O показать / скрыть оверлей", KeyO, mods, ToggleOverlay);
    }

    private static void Register(string name, uint key, uint mods, Action action)
    {
        var status = CarbonHotkeys.Register(key, mods, () =>
        {
            Log.Write($"горячая клавиша {name}: {MacApp.FocusSummary}");
            action();
        });
        Log.Write(status == 0 ? $"горячая клавиша {name}: зарегистрирована" : $"горячая клавиша {name}: ошибка {status}");
    }

    private TrayIcon CreateTrayIcon()
    {
        var menu = new NativeMenu();
        menu.Items.Add(MenuItem("Показать / скрыть оверлей  ⌃⌥O", ToggleOverlay));
        menu.Items.Add(MenuItem("Тестовая фраза  ⌃⌥T", SpeakTest));
        menu.Items.Add(MenuItem("Пауза / продолжить  ⌃⌥P", _speaker.TogglePause));
        menu.Items.Add(MenuItem("Стоп  ⌃⌥S", _speaker.Stop));
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(MenuItem("Диагностика окна в лог", () =>
            Log.Write(string.Join(Environment.NewLine,
                MacOverlay.Describe(_overlay!.NSWindow), MacApp.FocusSummary, MacApp.DescribeWindows()))));
        menu.Items.Add(MenuItem("Выход", () => _desktop?.Shutdown()));

        var tray = new TrayIcon
        {
            Icon = new WindowIcon(RenderTrayGlyph()),
            ToolTipText = "MachineVoice (этап 0)",
            Menu = menu,
            IsVisible = true,
        };
        MacOSProperties.SetIsTemplateIcon(tray, true);
        // The native status item is only created once the icon is attached to the application.
        TrayIcon.SetIcons(this, [tray]);
        return tray;
    }

    private static NativeMenuItem MenuItem(string header, Action action)
    {
        var item = new NativeMenuItem(header);
        item.Click += (_, _) => action();
        return item;
    }

    private static Bitmap RenderTrayGlyph()
    {
        var bitmap = new RenderTargetBitmap(new PixelSize(36, 36), new Vector(144, 144));
        using (var ctx = bitmap.CreateDrawingContext())
        {
            var speaker = StreamGeometry.Parse("M2,6.5 L5,6.5 L9,3 L9,15 L5,11.5 L2,11.5 Z");
            var waves = StreamGeometry.Parse("M11.5,6.5 Q13,9 11.5,11.5 M13.5,4.5 Q16.5,9 13.5,13.5");
            ctx.DrawGeometry(Brushes.Black, null, speaker);
            ctx.DrawGeometry(null, new Pen(Brushes.Black, 1.4, lineCap: PenLineCap.Round), waves);
        }
        return bitmap;
    }
}
