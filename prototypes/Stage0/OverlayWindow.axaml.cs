using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MachineVoice.Stage0.Interop;

namespace MachineVoice.Stage0;

public partial class OverlayWindow : Window
{
    private const int BottomOffset = 80;

    private readonly DispatcherTimer _focusTimer;

    public event Action? SpeakRequested;
    public event Action? PauseRequested;
    public event Action? StopRequested;

    public OverlayWindow()
    {
        InitializeComponent();
        _focusTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background,
            (_, _) => FocusText.Text = MacApp.FocusSummary);
    }

    public IntPtr NSWindow =>
        TryGetPlatformHandle() is { HandleDescriptor: "NSWindow" } handle ? handle.Handle : IntPtr.Zero;

    public void ShowOverlay()
    {
        Show();
        MacOverlay.Apply(NSWindow);
        PlaceAtBottomCenter();
        _focusTimer.Start();
    }

    public void HideOverlay()
    {
        _focusTimer.Stop();
        Hide();
    }

    public void SetStatus(string status) => StatusText.Text = status;

    /// <summary>Center of the pause button in global screen points (top-left origin), for click tests.</summary>
    public string PauseButtonScreenCenter
    {
        get
        {
            var center = PauseButton.TranslatePoint(new Point(PauseButton.Bounds.Width / 2, PauseButton.Bounds.Height / 2), this);
            if (center is null)
                return "—";
            var scaling = Screens.ScreenFromWindow(this)?.Scaling ?? 1;
            return $"{Position.X / scaling + center.Value.X:F0},{Position.Y / scaling + center.Value.Y:F0}";
        }
    }

    private void PlaceAtBottomCenter()
    {
        var screen = Screens.Primary;
        if (screen is null)
            return;

        var area = screen.WorkingArea;
        var size = PixelSize.FromSize(FrameSize ?? ClientSize, screen.Scaling);
        Position = new PixelPoint(
            area.X + (area.Width - size.Width) / 2,
            area.Y + area.Height - size.Height - (int)(BottomOffset * screen.Scaling));
    }

    private void OnSpeak(object? sender, RoutedEventArgs e) => Clicked("Тест", SpeakRequested);
    private void OnPause(object? sender, RoutedEventArgs e) => Clicked("Пауза", PauseRequested);
    private void OnStop(object? sender, RoutedEventArgs e) => Clicked("Стоп", StopRequested);
    private void OnHide(object? sender, RoutedEventArgs e) => Clicked("Скрыть", HideOverlay);

    private void Clicked(string button, Action? action)
    {
        action?.Invoke();
        Log.Write($"клик «{button}»: {MacApp.FocusSummary}");
        DispatcherTimer.RunOnce(() => Log.Write($"  через 300 мс: {MacApp.FocusSummary}"), TimeSpan.FromMilliseconds(300));
    }
}
