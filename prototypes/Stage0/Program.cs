using Avalonia;
using MachineVoice.Stage0.Interop;

namespace MachineVoice.Stage0;

internal static class Program
{
    /// <summary>--diagnose[=seconds]: log window and focus state, then quit.</summary>
    public static TimeSpan? DiagnoseFor { get; private set; }

    /// <summary>--policy=accessory|prohibited: activation policy while only the overlay is shown.</summary>
    public static MacApp.ActivationPolicy OverlayPolicy { get; private set; } = MacApp.ActivationPolicy.Accessory;

    /// <summary>--tts=say: use /usr/bin/say with SIGSTOP pause instead of AVSpeechSynthesizer.</summary>
    public static bool UseSay { get; private set; }

    [STAThread]
    public static void Main(string[] args)
    {
        foreach (var arg in args)
        {
            if (arg == "--diagnose")
                DiagnoseFor = TimeSpan.FromSeconds(3);
            else if (arg.StartsWith("--diagnose=") && double.TryParse(arg["--diagnose=".Length..], out var seconds))
                DiagnoseFor = TimeSpan.FromSeconds(seconds);
            else if (arg == "--policy=prohibited")
                OverlayPolicy = MacApp.ActivationPolicy.Prohibited;
            else if (arg == "--tts=say")
                UseSay = true;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // Avalonia's app delegate activates the app on launch, which steals focus from the
            // frontmost app. Its other duties (Dock menu, reopen, quit events) are not needed here.
            .With(new MacOSPlatformOptions { ShowInDock = false, DisableAvaloniaAppDelegate = true })
            .LogToTrace();
}
