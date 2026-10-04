using System.Runtime.InteropServices;

namespace MachineVoice.Platform.MacOS.Interop;

static partial class ObjC
{
    const string Lib = "/usr/lib/libobjc.A.dylib";

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial IntPtr objc_getClass(string name);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial IntPtr sel_registerName(string name);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial IntPtr objc_getProtocol(string name);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial IntPtr objc_allocateClassPair(IntPtr superclass, string name, nuint extraBytes);

    [LibraryImport(Lib)]
    public static partial void objc_registerClassPair(IntPtr cls);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool class_addMethod(IntPtr cls, IntPtr selector, IntPtr imp, string types);

    [LibraryImport(Lib)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool class_addProtocol(IntPtr cls, IntPtr protocol);

    [LibraryImport(Lib)]
    public static partial IntPtr objc_autoreleasePoolPush();

    [LibraryImport(Lib)]
    public static partial void objc_autoreleasePoolPop(IntPtr pool);

    [LibraryImport(Lib, EntryPoint = "objc_msgSend")]
    public static partial IntPtr Send(IntPtr receiver, IntPtr selector);

    [LibraryImport(Lib, EntryPoint = "objc_msgSend")]
    public static partial IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr arg);

    [LibraryImport(Lib, EntryPoint = "objc_msgSend")]
    public static partial void SendVoid(IntPtr receiver, IntPtr selector, IntPtr arg);

    [LibraryImport(Lib, EntryPoint = "objc_msgSend")]
    public static partial void SendVoid(IntPtr receiver, IntPtr selector, float arg);

    [LibraryImport(Lib, EntryPoint = "objc_msgSend")]
    public static partial byte SendBool(IntPtr receiver, IntPtr selector);

    [LibraryImport(Lib, EntryPoint = "objc_msgSend")]
    public static partial byte SendBool(IntPtr receiver, IntPtr selector, nint arg);

    public static IntPtr Sel(string name) => sel_registerName(name);

    public static IntPtr Class(string name) => objc_getClass(name);

    public static void Release(IntPtr obj)
    {
        if (obj != IntPtr.Zero)
            Send(obj, Sel("release"));
    }

    /// <summary>Returns an autoreleased NSString; call inside an autorelease pool.</summary>
    public static IntPtr NSString(string value)
    {
        var utf8 = Marshal.StringToCoTaskMemUTF8(value);
        try
        {
            return Send(Class("NSString"), Sel("stringWithUTF8String:"), utf8);
        }
        finally
        {
            Marshal.FreeCoTaskMem(utf8);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NSRange
    {
        public nuint Location;
        public nuint Length;
    }
}
