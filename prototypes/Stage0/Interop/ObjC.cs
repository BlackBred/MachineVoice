using System.Runtime.InteropServices;

namespace MachineVoice.Stage0.Interop;

internal static class ObjC
{
    private const string Lib = "/usr/lib/libobjc.A.dylib";

    [DllImport(Lib)] public static extern IntPtr objc_getClass(string name);
    [DllImport(Lib)] public static extern IntPtr sel_registerName(string name);
    [DllImport(Lib)] public static extern IntPtr object_getClass(IntPtr obj);
    [DllImport(Lib)] public static extern IntPtr class_getName(IntPtr cls);

    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern IntPtr Send(IntPtr receiver, IntPtr selector);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr arg);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern nint SendNInt(IntPtr receiver, IntPtr selector);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern nuint SendNUInt(IntPtr receiver, IntPtr selector);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern byte SendBool(IntPtr receiver, IntPtr selector);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern byte SendBool(IntPtr receiver, IntPtr selector, IntPtr arg);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern void SendVoid(IntPtr receiver, IntPtr selector, nint arg);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern void SendVoid(IntPtr receiver, IntPtr selector, nuint arg);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern void SendVoid(IntPtr receiver, IntPtr selector, byte arg);

    public static IntPtr Sel(string name) => sel_registerName(name);

    public static string ClassName(IntPtr obj) =>
        Marshal.PtrToStringUTF8(class_getName(object_getClass(obj))) ?? "?";

    public static bool RespondsTo(IntPtr obj, string selector) =>
        SendBool(obj, Sel("respondsToSelector:"), Sel(selector)) != 0;

    public static string? NSStringToString(IntPtr nsString) =>
        nsString == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(Send(nsString, Sel("UTF8String")));
}
