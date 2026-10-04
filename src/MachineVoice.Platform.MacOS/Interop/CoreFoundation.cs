using System.Runtime.InteropServices;

namespace MachineVoice.Platform.MacOS.Interop;

static partial class CoreFoundation
{
    const string Lib = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    public const int RunLoopRunFinished = 1;

    static readonly Lazy<IntPtr> DefaultModeValue = new(() =>
        Marshal.ReadIntPtr(NativeLibrary.GetExport(NativeLibrary.Load(Lib), "kCFRunLoopDefaultMode")));

    public static IntPtr DefaultMode => DefaultModeValue.Value;

    [LibraryImport(Lib)]
    public static partial IntPtr CFRunLoopGetMain();

    [LibraryImport(Lib)]
    public static partial void CFRunLoopStop(IntPtr loop);

    [LibraryImport(Lib)]
    public static partial int CFRunLoopRunInMode(IntPtr mode, double seconds, [MarshalAs(UnmanagedType.U1)] bool returnAfterSourceHandled);

    [LibraryImport("/usr/lib/libSystem.dylib")]
    public static partial int pthread_main_np();
}
