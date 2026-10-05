using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using MachineVoice.Platform.MacOS;
using MachineVoice.Protocol;

namespace MachineVoice.App;

/// <summary>
/// The pill at the bottom of the screen. While a response is read it shows the player; in the confirm mode the
/// same window is the toast. It never takes focus from the frontmost app.
/// </summary>
public partial class OverlayWindow : Window
{
    const int BottomOffset = 72;

    public OverlayWindow()
    {
        InitializeComponent();
    }

    public event Action? SlowerRequested;
    public event Action? FasterRequested;
    public event Action? PauseResumeRequested;
    public event Action? StopRequested;
    public event Action? SkipRequested;
    public event Action? OpenChatRequested;
    public event Action? ListenRequested;
    public event Action? DismissRequested;

    IntPtr NSWindow =>
        TryGetPlatformHandle() is { HandleDescriptor: "NSWindow" } handle ? handle.Handle : IntPtr.Zero;

    public void ShowOverlay()
    {
        if (IsVisible)
            return;
        Show();
        MacOverlay.Apply(NSWindow);
        Place();
    }

    public void HideOverlay() => Hide();

    public void Update(ControlState state)
    {
        UpdateQueueBadge(state);

        if (state.Current is { } item)
        {
            var paused = state.Player == PlayerState.Paused;
            TitleText.Text = paused ? $"{Labels.Title(item)} · пауза" : Labels.Title(item);
            TopicText.Text = Labels.Topic(item);
            PauseIcon.Data = Glyph(paused ? "PlayGlyph" : "PauseGlyph");
            ToolTip.SetTip(PauseButton, paused ? "Продолжить" : "Пауза");
            RateText.Text = Labels.Rate(state.Settings.Tts.PlaybackRate);
            ProgressLine.IsVisible = true;
            PlaybackButtons.IsVisible = true;
            ConfirmButtons.IsVisible = false;
            UpdateProgress(state);
            return;
        }

        if (state.Confirmation is { } pending)
        {
            TitleText.Text = Labels.Title(pending);
            TopicText.Text = "Новый ответ: " + Labels.Topic(pending);
            ProgressLine.IsVisible = false;
            PlaybackButtons.IsVisible = false;
            ConfirmButtons.IsVisible = true;
        }
    }

    public void UpdateProgress(ControlState state) => ProgressLine.Value = state.Progress ?? 0;

    void UpdateQueueBadge(ControlState state)
    {
        var waiting = state.Waiting.Count();
        QueueBadge.IsVisible = waiting > 0;
        QueueCountText.Text = waiting > 99 ? "99+" : waiting.ToString();
        ToolTip.SetTip(QueueBadge, $"В очереди: {waiting}");
        Pill.Padding = waiting > 0 ? new Thickness(13, 10, 10, 10) : new Thickness(20, 10, 10, 10);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ClientSizeProperty && IsVisible)
            Place();
    }

    Geometry? Glyph(string key) => this.FindResource(key) as Geometry;

    void Place()
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

    void OnSlower(object? sender, RoutedEventArgs e) => SlowerRequested?.Invoke();
    void OnFaster(object? sender, RoutedEventArgs e) => FasterRequested?.Invoke();
    void OnPause(object? sender, RoutedEventArgs e) => PauseResumeRequested?.Invoke();
    void OnStop(object? sender, RoutedEventArgs e) => StopRequested?.Invoke();
    void OnSkip(object? sender, RoutedEventArgs e) => SkipRequested?.Invoke();
    void OnOpenChat(object? sender, RoutedEventArgs e) => OpenChatRequested?.Invoke();
    void OnListen(object? sender, RoutedEventArgs e) => ListenRequested?.Invoke();
    void OnDismiss(object? sender, RoutedEventArgs e) => DismissRequested?.Invoke();
}
