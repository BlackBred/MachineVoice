using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MachineVoice.Stage0.Interop;

/// <summary>
/// Global hotkeys via Carbon RegisterEventHotKey: works without the Accessibility permission.
/// Handlers run on the main thread from the AppKit run loop.
/// </summary>
internal static unsafe class CarbonHotkeys
{
    private const string Carbon = "/System/Library/Frameworks/Carbon.framework/Carbon";

    public const uint CmdKey = 0x0100;
    public const uint ShiftKey = 0x0200;
    public const uint OptionKey = 0x0800;
    public const uint ControlKey = 0x1000;

    private const uint KEventClassKeyboard = 0x6B657962; // 'keyb'
    private const uint KEventHotKeyPressed = 5;
    private const uint KEventParamDirectObject = 0x2D2D2D2D; // '----'
    private const uint TypeEventHotKeyID = 0x686B6964; // 'hkid'
    private const uint Signature = 0x4D564F43; // 'MVOC'

    [StructLayout(LayoutKind.Sequential)]
    private struct EventTypeSpec { public uint EventClass; public uint EventKind; }

    [StructLayout(LayoutKind.Sequential)]
    private struct EventHotKeyID { public uint Signature; public uint Id; }

    [DllImport(Carbon)] private static extern IntPtr GetApplicationEventTarget();

    [DllImport(Carbon)]
    private static extern int InstallEventHandler(IntPtr target,
        delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, int> handler,
        nuint numTypes, EventTypeSpec* list, IntPtr userData, out IntPtr handlerRef);

    [DllImport(Carbon)]
    private static extern int RegisterEventHotKey(uint keyCode, uint modifiers, EventHotKeyID id,
        IntPtr target, uint options, out IntPtr hotKeyRef);

    [DllImport(Carbon)] private static extern int UnregisterEventHotKey(IntPtr hotKeyRef);

    [DllImport(Carbon)]
    private static extern int GetEventParameter(IntPtr evt, uint name, uint desiredType,
        IntPtr actualType, nuint bufferSize, IntPtr actualSize, void* data);

    private static readonly Dictionary<uint, Action> Actions = new();
    private static readonly List<IntPtr> Refs = new();
    private static bool _handlerInstalled;
    private static uint _nextId = 1;

    /// <returns>OSStatus: 0 on success, -9878 (eventHotKeyExistsErr) if the shortcut is taken.</returns>
    public static int Register(uint keyCode, uint modifiers, Action action)
    {
        if (!_handlerInstalled)
        {
            var spec = new EventTypeSpec { EventClass = KEventClassKeyboard, EventKind = KEventHotKeyPressed };
            var status = InstallEventHandler(GetApplicationEventTarget(), &OnHotKey, 1, &spec, IntPtr.Zero, out _);
            if (status != 0)
                return status;
            _handlerInstalled = true;
        }

        var id = _nextId++;
        var result = RegisterEventHotKey(keyCode, modifiers, new EventHotKeyID { Signature = Signature, Id = id },
            GetApplicationEventTarget(), 0, out var hotKeyRef);
        if (result == 0)
        {
            Actions[id] = action;
            Refs.Add(hotKeyRef);
        }
        return result;
    }

    public static void UnregisterAll()
    {
        foreach (var r in Refs)
            UnregisterEventHotKey(r);
        Refs.Clear();
        Actions.Clear();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnHotKey(IntPtr callRef, IntPtr evt, IntPtr userData)
    {
        EventHotKeyID hotKey;
        var status = GetEventParameter(evt, KEventParamDirectObject, TypeEventHotKeyID,
            IntPtr.Zero, (nuint)sizeof(EventHotKeyID), IntPtr.Zero, &hotKey);
        if (status != 0 || hotKey.Signature != Signature || !Actions.TryGetValue(hotKey.Id, out var action))
            return -9874; // eventNotHandledErr

        try
        {
            action();
        }
        catch (Exception e)
        {
            Log.Write($"ошибка в обработчике горячей клавиши: {e}");
        }
        return 0;
    }
}
