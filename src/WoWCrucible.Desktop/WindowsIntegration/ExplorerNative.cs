using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace WoWCrucible.Desktop.WindowsIntegration;

[ComVisible(true), Guid("00000001-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IExplorerClassFactory
{
    [PreserveSig] int CreateInstance(nint outer, ref Guid iid, out nint instance);
    [PreserveSig] int LockServer([MarshalAs(UnmanagedType.Bool)] bool locked);
}

[ComVisible(true), Guid("7F9185B0-CB92-43C5-80A9-92277A4F7B54"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IExplorerExecuteCommand
{
    [PreserveSig] int SetKeyState(uint keyState);
    [PreserveSig] int SetParameters([MarshalAs(UnmanagedType.LPWStr)] string? parameters);
    [PreserveSig] int SetPosition(ExplorerPoint point);
    [PreserveSig] int SetShowWindow(int show);
    [PreserveSig] int SetNoShowUI([MarshalAs(UnmanagedType.Bool)] bool noShowUi);
    [PreserveSig] int SetDirectory([MarshalAs(UnmanagedType.LPWStr)] string? directory);
    [PreserveSig] int Execute();
}

[ComVisible(true), Guid("1C9CD5BB-98E9-4491-A60F-31AACC72B83C"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IExplorerObjectWithSelection
{
    [PreserveSig] int SetSelection(IShellItemArray? selection);
    [PreserveSig] int GetSelection(ref Guid iid, out nint selection);
}

[ComVisible(true), Guid("85075ACF-231F-40EA-9610-D26B7B58F638"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IExplorerInitializeCommand
{
    [PreserveSig] int Initialize([MarshalAs(UnmanagedType.LPWStr)] string name, nint propertyBag);
}

[ComImport, Guid("B63EA76D-1F85-456F-A19C-48159EFA858B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IShellItemArray
{
    void BindToHandler(nint bindContext, ref Guid handler, ref Guid iid, out nint result);
    void GetPropertyStore(uint flags, ref Guid iid, out nint store);
    void GetPropertyDescriptionList(nint propertyKey, ref Guid iid, out nint descriptions);
    void GetAttributes(uint flags, uint mask, out uint attributes);
    void GetCount(out uint count);
    void GetItemAt(uint index, out IShellItem item);
    void EnumItems(out nint items);
}

[ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IShellItem
{
    void BindToHandler(nint bindContext, ref Guid handler, ref Guid iid, out nint result);
    void GetParent(out IShellItem parent);
    void GetDisplayName(uint nameType, out nint name);
    void GetAttributes(uint mask, out uint attributes);
    void Compare(IShellItem other, uint hint, out int order);
}

[ComImport, Guid("EBBC7C04-315E-11D2-B62F-006097DF5BD4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IExplorerProgressDialog
{
    void StartProgressDialog(nint parent, nint modal, uint flags, nint reserved);
    void StopProgressDialog();
    void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
    void SetAnimation(nint instance, uint animation);
    [PreserveSig]
    [return: MarshalAs(UnmanagedType.Bool)] bool HasUserCancelled();
    void SetProgress(uint complete, uint total);
    void SetProgress64(ulong complete, ulong total);
    void SetLine(uint line, [MarshalAs(UnmanagedType.LPWStr)] string text, [MarshalAs(UnmanagedType.Bool)] bool compactPath, nint reserved);
    void SetCancelMsg([MarshalAs(UnmanagedType.LPWStr)] string text, nint reserved);
    void Timer(uint action, nint reserved);
}

[StructLayout(LayoutKind.Sequential)]
public struct ExplorerPoint { public int X; public int Y; }

[StructLayout(LayoutKind.Sequential)]
internal struct ExplorerMessage
{
    public nint Window;
    public uint Message;
    public nuint WParam;
    public nint LParam;
    public uint Time;
    public ExplorerPoint Point;
    public uint Private;
}

[SupportedOSPlatform("windows")]
internal static class ExplorerNative
{
    [DllImport("ole32.dll")] internal static extern int OleInitialize(nint reserved);
    [DllImport("ole32.dll")] internal static extern void OleUninitialize();
    [DllImport("ole32.dll")] internal static extern int CoRegisterClassObject(ref Guid clsid, [MarshalAs(UnmanagedType.IUnknown)] object factory, uint context, uint flags, out uint cookie);
    [DllImport("ole32.dll")] internal static extern int CoRevokeClassObject(uint cookie);
    [DllImport("ole32.dll")] internal static extern int CoResumeClassObjects();
    [DllImport("ole32.dll")] internal static extern int CoSuspendClassObjects();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int MessageBox(nint parent, string text, string caption, uint type);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool PeekMessage(out ExplorerMessage message, nint window, uint min, uint max, uint remove);
    [DllImport("user32.dll")] internal static extern nint DispatchMessage(ref ExplorerMessage message);
    [DllImport("user32.dll")] internal static extern uint MsgWaitForMultipleObjectsEx(uint count, nint handles, uint milliseconds, uint wakeMask, uint flags);
    [DllImport("shell32.dll")] internal static extern void SHChangeNotify(uint eventId, uint flags, nint item1, nint item2);
}
