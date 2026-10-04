using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace MachineVoice.App;

public partial class App : Application
{
    AppController? _controller;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _controller = new AppController(this, desktop, Program.Options);
            Dispatcher.UIThread.Post(() => _ = _controller.StartAsync());
        }

        base.OnFrameworkInitializationCompleted();
    }
}
