using static MachineVoice.Stage0.Interop.ObjC;

namespace MachineVoice.Stage0.Interop;

/// <summary>
/// Makes an Avalonia window behave as an overlay: it floats above full-screen apps, is visible on
/// every Space, never becomes key and does not activate the app when clicked.
/// </summary>
/// <remarks>
/// Swapping the window to Avalonia's AvnPanel (NSPanel) class is not possible: by the time the
/// window is reachable from managed code, KVO has already replaced its class with
/// NSKVONotifying_AvnWindow. NSWindow also drops NSWindowStyleMaskNonactivatingPanel.
/// </remarks>
internal static class MacOverlay
{
    private const nuint CanJoinAllSpaces = 1 << 0;
    private const nuint Stationary = 1 << 4;
    private const nuint IgnoresCycle = 1 << 6;
    private const nuint FullScreenAuxiliary = 1 << 8;

    private const nint NSStatusWindowLevel = 25;

    /// <summary>Must be called after every Show(): Avalonia resets window state while showing.</summary>
    public static void Apply(IntPtr nsWindow)
    {
        if (nsWindow == IntPtr.Zero)
            return;

        // Private AppKit API used by NSPanel for non-activating panels.
        if (RespondsTo(nsWindow, "_setPreventsActivation:"))
            SendVoid(nsWindow, Sel("_setPreventsActivation:"), (byte)1);

        if (RespondsTo(nsWindow, "setCanBecomeKeyWindow:"))
            SendVoid(nsWindow, Sel("setCanBecomeKeyWindow:"), (byte)0);

        SendVoid(nsWindow, Sel("setLevel:"), NSStatusWindowLevel);
        SendVoid(nsWindow, Sel("setCollectionBehavior:"),
            CanJoinAllSpaces | Stationary | IgnoresCycle | FullScreenAuxiliary);
    }

    public static string Describe(IntPtr nsWindow)
    {
        if (nsWindow == IntPtr.Zero)
            return "окно не создано";

        var preventsActivation = RespondsTo(nsWindow, "_preventsActivation")
            ? (SendBool(nsWindow, Sel("_preventsActivation")) != 0).ToString()
            : "нет API";
        return string.Join(Environment.NewLine,
            $"класс: {ClassName(nsWindow)}",
            $"level: {SendNInt(nsWindow, Sel("level"))}, collectionBehavior: 0x{SendNUInt(nsWindow, Sel("collectionBehavior")):X}",
            $"isKeyWindow: {SendBool(nsWindow, Sel("isKeyWindow")) != 0}, isVisible: {SendBool(nsWindow, Sel("isVisible")) != 0}, _preventsActivation: {preventsActivation}");
    }
}
