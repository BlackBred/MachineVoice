using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using static MachineVoice.Platform.MacOS.Interop.ObjC;

namespace MachineVoice.Platform.MacOS;

/// <summary>The general pasteboard's plain text. Call from the main thread.</summary>
[SupportedOSPlatform("macos")]
public static class MacPasteboard
{
    const string PlainText = "public.utf8-plain-text";

    public static string? ReadText()
    {
        var pool = objc_autoreleasePoolPush();
        try
        {
            var pasteboard = Send(Class("NSPasteboard"), Sel("generalPasteboard"));
            if (pasteboard == IntPtr.Zero)
                return null;

            var value = Send(pasteboard, Sel("stringForType:"), NSString(PlainText));
            return value == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(Send(value, Sel("UTF8String")));
        }
        finally
        {
            objc_autoreleasePoolPop(pool);
        }
    }
}
