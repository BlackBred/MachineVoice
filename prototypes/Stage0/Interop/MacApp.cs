using static MachineVoice.Stage0.Interop.ObjC;

namespace MachineVoice.Stage0.Interop;

internal static class MacApp
{
    public enum ActivationPolicy : long { Regular = 0, Accessory = 1, Prohibited = 2 }

    private static IntPtr NSApp => Send(objc_getClass("NSApplication"), Sel("sharedApplication"));

    public static ActivationPolicy Policy
    {
        get => (ActivationPolicy)SendNInt(NSApp, Sel("activationPolicy"));
        set => SendBool(NSApp, Sel("setActivationPolicy:"), (IntPtr)(long)value);
    }

    public static bool IsActive => SendBool(NSApp, Sel("isActive")) != 0;

    public static string FrontmostAppName
    {
        get
        {
            var workspace = Send(objc_getClass("NSWorkspace"), Sel("sharedWorkspace"));
            var app = Send(workspace, Sel("frontmostApplication"));
            return app == IntPtr.Zero ? "—" : NSStringToString(Send(app, Sel("localizedName"))) ?? "—";
        }
    }

    public static string FocusSummary =>
        $"активное приложение: {FrontmostAppName}, MachineVoice активен: {IsActive}, политика: {Policy}";
}
