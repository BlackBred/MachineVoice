using System.Runtime.Versioning;
using Avalonia;
using MachineVoice.Core;

[assembly: SupportedOSPlatform("macos")]

namespace MachineVoice.App;

sealed record LaunchOptions(string RootDirectory, string? CursorDirectory, string McpServerBinary);

static class Program
{
    public static LaunchOptions Options { get; private set; } = null!;

    [STAThread]
    public static int Main(string[] args)
    {
        if (!TryParse(args, out var options))
        {
            Console.Error.WriteLine("Usage: MachineVoice [--root <data directory>] [--cursor-dir <directory with hooks.json and mcp.json>] [--mcp <MachineVoice.Mcp binary>]");
            return 2;
        }

        Options = options;
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // Avalonia's app delegate activates the app on launch and steals focus from the frontmost app.
            .With(new MacOSPlatformOptions { ShowInDock = false, DisableAvaloniaAppDelegate = true })
            .LogToTrace();

    static bool TryParse(string[] args, out LaunchOptions options)
    {
        string root = MachineVoiceHost.DefaultRootDirectory;
        string? cursor = null;
        var mcp = Path.Combine(AppContext.BaseDirectory, "MachineVoice.Mcp");
        options = null!;
        for (var i = 0; i < args.Length; i++)
        {
            if (i + 1 >= args.Length)
                return false;
            switch (args[i])
            {
                case "--root":
                    root = Path.GetFullPath(args[++i]);
                    break;
                case "--cursor-dir":
                    cursor = Path.GetFullPath(args[++i]);
                    break;
                case "--mcp":
                    mcp = Path.GetFullPath(args[++i]);
                    break;
                default:
                    return false;
            }
        }

        options = new LaunchOptions(root, cursor, mcp);
        return true;
    }
}
