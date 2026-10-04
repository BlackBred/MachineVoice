using System.Runtime.Versioning;
using static MachineVoice.Platform.MacOS.Interop.ObjC;

namespace MachineVoice.Platform.MacOS;

/// <summary>
/// Makes a regular NSWindow behave as an overlay: it floats above full-screen apps, is visible on every Space,
/// never becomes key and does not activate the app when clicked.
/// </summary>
/// <remarks>
/// Avalonia windows cannot be turned into NSPanel: by the time managed code sees the window, KVO has replaced its
/// class with NSKVONotifying_AvnWindow, and NSWindow drops NSWindowStyleMaskNonactivatingPanel. The private
/// _setPreventsActivation: that NSPanel uses gives the same behaviour.
/// </remarks>
[SupportedOSPlatform("macos")]
public static class MacOverlay
{
    const nuint CanJoinAllSpaces = 1 << 0;
    const nuint Stationary = 1 << 4;
    const nuint IgnoresCycle = 1 << 6;
    const nuint FullScreenAuxiliary = 1 << 8;

    const nint StatusWindowLevel = 25;

    /// <summary>Call after every show: Avalonia resets the window state while showing it.</summary>
    public static void Apply(IntPtr nsWindow)
    {
        if (nsWindow == IntPtr.Zero)
            return;

        if (RespondsTo(nsWindow, "_setPreventsActivation:"))
            SendVoid(nsWindow, Sel("_setPreventsActivation:"), (byte)1);
        if (RespondsTo(nsWindow, "setCanBecomeKeyWindow:"))
            SendVoid(nsWindow, Sel("setCanBecomeKeyWindow:"), (byte)0);

        SendVoid(nsWindow, Sel("setLevel:"), StatusWindowLevel);
        SendVoid(nsWindow, Sel("setCollectionBehavior:"), CanJoinAllSpaces | Stationary | IgnoresCycle | FullScreenAuxiliary);
    }
}
