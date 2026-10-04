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

    /// <summary>All windows of the app, including NSStatusBarWindow hosting menu bar items.</summary>
    public static string DescribeWindows()
    {
        var windows = Send(NSApp, Sel("windows"));
        var count = SendNUInt(windows, Sel("count"));
        var lines = new List<string>();
        for (nuint i = 0; i < count; i++)
        {
            var window = Send(windows, Sel("objectAtIndex:"), i);
            var frame = SendRect(window, Sel("frame"));
            lines.Add($"  {ClassName(window)}: x {frame.X:F0} y {frame.Y:F0} w {frame.Width:F0} h {frame.Height:F0}, " +
                      $"visible {SendBool(window, Sel("isVisible")) != 0}, occlusion 0x{SendNUInt(window, Sel("occlusionState")):X}");
        }
        return $"окна приложения ({count}):" + Environment.NewLine + string.Join(Environment.NewLine, lines);
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
