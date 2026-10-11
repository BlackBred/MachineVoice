using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace MachineVoice.Platform.MacOS;

/// <summary>
/// Global hotkeys via Carbon RegisterEventHotKey. They work without the Accessibility permission,
/// and the handler runs on the main thread from the AppKit run loop.
/// </summary>
[SupportedOSPlatform("macos")]
public static unsafe class CarbonHotkeys
{
    const string Carbon = "/System/Library/Frameworks/Carbon.framework/Carbon";

    public const uint Command = 0x0100;
    public const uint Shift = 0x0200;
    public const uint Option = 0x0800;
    public const uint Control = 0x1000;

    /// <summary>kVK_ANSI_V.</summary>
    public const uint KeyV = 0x09;

    const uint EventClassKeyboard = 0x6B657962; // 'keyb'
    const uint HotKeyPressed = 5;
    const uint ParamDirectObject = 0x2D2D2D2D; // '----'
    const uint TypeHotKeyId = 0x686B6964; // 'hkid'
    const uint Signature = 0x4D564F43; // 'MVOC'

    [StructLayout(LayoutKind.Sequential)]
    struct EventTypeSpec
    {
        public uint EventClass;
        public uint EventKind;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct EventHotKeyId
    {
        public uint Signature;
        public uint Id;
    }

    [DllImport(Carbon)]
    static extern IntPtr GetApplicationEventTarget();

    [DllImport(Carbon)]
    static extern int InstallEventHandler(
        IntPtr target,
        delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, int> handler,
        nuint numTypes,
        EventTypeSpec* list,
        IntPtr userData,
        out IntPtr handlerRef);

    [DllImport(Carbon)]
    static extern int RegisterEventHotKey(
        uint keyCode,
        uint modifiers,
        EventHotKeyId id,
        IntPtr target,
        uint options,
        out IntPtr hotKeyRef);

    [DllImport(Carbon)]
    static extern int UnregisterEventHotKey(IntPtr hotKeyRef);

    [DllImport(Carbon)]
    static extern int GetEventParameter(
        IntPtr evt,
        uint name,
        uint desiredType,
        IntPtr actualType,
        nuint bufferSize,
        IntPtr actualSize,
        void* data);

    static readonly Dictionary<uint, Action> Actions = [];
    static readonly List<IntPtr> Refs = [];
    static bool _handlerInstalled;
    static uint _nextId = 1;

    /// <returns>OSStatus: 0 on success, -9878 (eventHotKeyExistsErr) when the shortcut is taken.</returns>
    public static int Register(uint keyCode, uint modifiers, Action action)
    {
        if (!_handlerInstalled)
        {
            var spec = new EventTypeSpec { EventClass = EventClassKeyboard, EventKind = HotKeyPressed };
            var status = InstallEventHandler(GetApplicationEventTarget(), &OnHotKey, 1, &spec, IntPtr.Zero, out _);
            if (status != 0)
                return status;
            _handlerInstalled = true;
        }

        var id = _nextId++;
        var result = RegisterEventHotKey(
            keyCode,
            modifiers,
            new EventHotKeyId { Signature = Signature, Id = id },
            GetApplicationEventTarget(),
            0,
            out var hotKeyRef);
        if (result == 0)
        {
            Actions[id] = action;
            Refs.Add(hotKeyRef);
        }

        return result;
    }

    public static void UnregisterAll()
    {
        foreach (var hotKey in Refs)
            UnregisterEventHotKey(hotKey);
        Refs.Clear();
        Actions.Clear();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static int OnHotKey(IntPtr callRef, IntPtr evt, IntPtr userData)
    {
        EventHotKeyId hotKey;
        var status = GetEventParameter(
            evt,
            ParamDirectObject,
            TypeHotKeyId,
            IntPtr.Zero,
            (nuint)sizeof(EventHotKeyId),
            IntPtr.Zero,
            &hotKey);
        if (status != 0 || hotKey.Signature != Signature || !Actions.TryGetValue(hotKey.Id, out var action))
            return -9874; // eventNotHandledErr

        try
        {
            action();
        }
        catch (Exception)
        {
            // The run loop has to survive a faulty handler. The action reports its own failures.
        }

        return 0;
    }
}
