using System.Runtime.InteropServices;

namespace MachineVoice.Platform.MacOS.Interop;

/// <summary>
/// Objective-C blocks built by hand (the Block ABI of libclosure). The blocks are marked global: Block_copy and
/// Block_release leave them alone, so a block keeps its address and the callback finds its owner by it.
/// </summary>
static unsafe class Blocks
{
    const int IsGlobal = 1 << 28;
    const int HasSignature = 1 << 30;

    static readonly Lazy<IntPtr> GlobalBlockClass = new(() =>
        NativeLibrary.GetExport(NativeLibrary.Load("/usr/lib/libSystem.B.dylib"), "_NSConcreteGlobalBlock"));

    /// <summary>
    /// A block <c>void (^)(id)</c> that calls <paramref name="invoke"/> with the block itself and the argument.
    /// Never freed: the framework may call it after its owner is gone.
    /// </summary>
    public static IntPtr ObjectCallback(delegate* unmanaged<IntPtr, IntPtr, void> invoke)
    {
        var descriptor = (Descriptor*)NativeMemory.AllocZeroed((nuint)sizeof(Descriptor));
        descriptor->Size = (nuint)sizeof(Literal);
        descriptor->Signature = Marshal.StringToCoTaskMemUTF8("v16@?0@8");

        var block = (Literal*)NativeMemory.AllocZeroed((nuint)sizeof(Literal));
        block->Isa = GlobalBlockClass.Value;
        block->Flags = IsGlobal | HasSignature;
        block->Invoke = (IntPtr)invoke;
        block->Descriptor = descriptor;
        return (IntPtr)block;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Literal
    {
        public IntPtr Isa;
        public int Flags;
        public int Reserved;
        public IntPtr Invoke;
        public Descriptor* Descriptor;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Descriptor
    {
        public nuint Reserved;
        public nuint Size;
        public IntPtr Signature;
        public IntPtr Layout;
    }
}
