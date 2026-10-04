using System.Diagnostics;
using System.Runtime.Versioning;
using static MachineVoice.Platform.MacOS.Interop.ObjC;

namespace MachineVoice.Platform.MacOS;

[SupportedOSPlatform("macos")]
public static class MacApplication
{
    public const string CursorBundleId = "com.todesktop.230313mzl4w4u92";

    const nint AccessoryPolicy = 1;

    static IntPtr NSApp => Send(Class("NSApplication"), Sel("sharedApplication"));

    /// <summary>No Dock icon and no menu bar of its own; the overlay and hotkeys leave the frontmost app active.</summary>
    public static void UseAccessoryPolicy() =>
        SendBool(NSApp, Sel("setActivationPolicy:"), AccessoryPolicy);

    /// <summary>Brings the app forward, so a regular window (settings) can take keyboard focus.</summary>
    public static void Activate()
    {
        var app = NSApp;
        if (RespondsTo(app, "activate"))
            Send(app, Sel("activate"));
        else
            SendVoid(app, Sel("activateIgnoringOtherApps:"), (byte)1);
    }

    /// <summary>Launches the app or brings it to the front.</summary>
    public static bool Open(string bundleId, Action<string>? log = null)
    {
        try
        {
            var start = new ProcessStartInfo("/usr/bin/open") { UseShellExecute = false };
            start.ArgumentList.Add("-b");
            start.ArgumentList.Add(bundleId);
            using var process = Process.Start(start);
            return process is not null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            log?.Invoke($"Could not open {bundleId}: {ex.Message}");
            return false;
        }
    }
}
