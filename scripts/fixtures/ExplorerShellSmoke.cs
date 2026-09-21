using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

public static class CrucibleExplorerSmoke
{
    public static void Invoke(string classId, string[] paths) => Sta(() =>
    {
        var ids = Parse(paths);
        object command = null;
        object selection = null;
        try
        {
            var id = new Guid(classId);
            var iid = typeof(IExecuteCommand).GUID;
            Marshal.ThrowExceptionForHR(CoCreateInstance(ref id, IntPtr.Zero, 4, ref iid, out command));
            Marshal.ThrowExceptionForHR(SHCreateShellItemArrayFromIDLists((uint)ids.Length, ids, out selection));
            var execute = (IExecuteCommand)command;
            execute.SetNoShowUI(true);
            ((IObjectWithSelection)command).SetSelection(selection);
            execute.Execute();
        }
        finally
        {
            if (command != null) Marshal.ReleaseComObject(command);
            if (selection != null) Marshal.ReleaseComObject(selection);
            foreach (var pidl in ids) Marshal.FreeCoTaskMem(pidl);
        }
        return 0;
    });

    public static string[] Menu(string[] paths) => MenuCore(paths, null);
    public static void InvokeMenu(string[] paths, string label) => MenuCore(paths, label);
    private static string[] MenuCore(string[] paths, string invokeLabel) => Sta(() =>
    {
        var ids = Parse(paths);
        IShellFolder folder = null;
        IContextMenu context = null;
        var menu = CreatePopupMenu();
        try
        {
            var folderId = typeof(IShellFolder).GUID;
            Marshal.ThrowExceptionForHR(SHBindToParent(ids[0], ref folderId, out folder, out var child));
            var children = new IntPtr[ids.Length];
            for (var index = 0; index < ids.Length; index++) children[index] = ILFindLastID(ids[index]);
            var contextId = typeof(IContextMenu).GUID;
            folder.GetUIObjectOf(IntPtr.Zero, (uint)children.Length, children, ref contextId, IntPtr.Zero, out context);
            context.QueryContextMenu(menu, 0, 1, 0x7fff, 0);
            var labels = new List<string>();
            var commands = new Dictionary<string, uint>();
            ReadMenu(menu, labels, context as IContextMenu2, commands, false);
            if (invokeLabel != null)
            {
                if (!commands.TryGetValue(invokeLabel, out var command)) throw new InvalidOperationException("Crucible menu command not found: " + invokeLabel);
                var info = new CommandInfo { Size = (uint)Marshal.SizeOf(typeof(CommandInfo)), Mask = 0x400, Verb = new IntPtr(command - 1) };
                var buffer = Marshal.AllocHGlobal((int)info.Size);
                try { Marshal.StructureToPtr(info, buffer, false); context.InvokeCommand(buffer); }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            return labels.ToArray();
        }
        finally
        {
            DestroyMenu(menu);
            if (context != null) Marshal.ReleaseComObject(context);
            if (folder != null) Marshal.ReleaseComObject(folder);
            foreach (var pidl in ids) Marshal.FreeCoTaskMem(pidl);
        }
    });

    private static void ReadMenu(IntPtr menu, List<string> labels, IContextMenu2 context, Dictionary<string, uint> commands, bool crucible)
    {
        for (var index = 0; index < GetMenuItemCount(menu); index++)
        {
            var text = new StringBuilder(512);
            GetMenuString(menu, (uint)index, text, text.Capacity, 0x400);
            var label = text.ToString().Replace("&", "");
            labels.Add(label);
            if (crucible) commands[label] = GetMenuItemID(menu, index);
            var child = GetSubMenu(menu, index);
            if (child != IntPtr.Zero)
            {
                // Cascading verbs are populated on hover, not by QueryContextMenu alone.
                if (context != null) context.HandleMenuMsg(0x117, child, new IntPtr(index));
                ReadMenu(child, labels, context, commands, crucible || label == "Crucible");
            }
        }
    }

    private static IntPtr[] Parse(string[] paths)
    {
        var ids = new IntPtr[paths.Length];
        try
        {
            for (var index = 0; index < paths.Length; index++)
                Marshal.ThrowExceptionForHR(SHParseDisplayName(paths[index], IntPtr.Zero, out ids[index], 0, out _));
            return ids;
        }
        catch { foreach (var id in ids) if (id != IntPtr.Zero) Marshal.FreeCoTaskMem(id); throw; }
    }

    private static T Sta<T>(Func<T> action)
    {
        T result = default(T);
        Exception error = null;
        var thread = new Thread(() =>
        {
            try
            {
                Marshal.ThrowExceptionForHR(OleInitialize(IntPtr.Zero));
                try { result = action(); }
                finally { OleUninitialize(); }
            }
            catch (Exception exception) { error = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error != null) throw new InvalidOperationException("Explorer shell test failed.", error);
        return result;
    }

    [ComImport, Guid("7F9185B0-CB92-43C5-80A9-92277A4F7B54"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IExecuteCommand
    {
        void SetKeyState(uint state);
        void SetParameters([MarshalAs(UnmanagedType.LPWStr)] string parameters);
        void SetPosition(Point point);
        void SetShowWindow(int show);
        void SetNoShowUI([MarshalAs(UnmanagedType.Bool)] bool noUi);
        void SetDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        void Execute();
    }
    [ComImport, Guid("1C9CD5BB-98E9-4491-A60F-31AACC72B83C"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IObjectWithSelection
    {
        void SetSelection([MarshalAs(UnmanagedType.Interface)] object selection);
        void GetSelection(ref Guid iid, out IntPtr selection);
    }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)] private struct CommandInfo
    {
        public uint Size; public uint Mask; public IntPtr Window; public IntPtr Verb;
        public IntPtr Parameters; public IntPtr Directory; public int Show; public uint HotKey; public IntPtr Icon;
    }
    [ComImport, Guid("000214E6-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellFolder
    {
        void ParseDisplayName(); void EnumObjects(); void BindToObject(); void BindToStorage();
        void CompareIDs(); void CreateViewObject(); void GetAttributesOf();
        void GetUIObjectOf(IntPtr owner, uint count, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] IntPtr[] children,
            ref Guid iid, IntPtr reserved, out IContextMenu menu);
        void GetDisplayNameOf(); void SetNameOf();
    }
    [ComImport, Guid("000214E4-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu
    {
        void QueryContextMenu(IntPtr menu, uint index, uint firstId, uint lastId, uint flags);
        void InvokeCommand(IntPtr info);
        void GetCommandString(UIntPtr id, uint flags, IntPtr reserved, IntPtr name, uint count);
    }
    [ComImport, Guid("000214F4-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu2
    {
        void QueryContextMenu(IntPtr menu, uint index, uint firstId, uint lastId, uint flags);
        void InvokeCommand(IntPtr info);
        void GetCommandString(UIntPtr id, uint flags, IntPtr reserved, IntPtr name, uint count);
        void HandleMenuMsg(uint message, IntPtr wParam, IntPtr lParam);
    }

    [DllImport("ole32.dll")] private static extern int OleInitialize(IntPtr reserved);
    [DllImport("ole32.dll")] private static extern void OleUninitialize();
    [DllImport("ole32.dll")] private static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint context, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object instance);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHParseDisplayName(string name, IntPtr bindContext, out IntPtr pidl, uint flags, out uint attributes);
    [DllImport("shell32.dll")] private static extern int SHCreateShellItemArrayFromIDLists(uint count, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] IntPtr[] ids, [MarshalAs(UnmanagedType.Interface)] out object selection);
    [DllImport("shell32.dll")] private static extern int SHBindToParent(IntPtr pidl, ref Guid iid, out IShellFolder folder, out IntPtr child);
    [DllImport("shell32.dll")] private static extern IntPtr ILFindLastID(IntPtr pidl);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] private static extern int GetMenuItemCount(IntPtr menu);
    [DllImport("user32.dll")] private static extern uint GetMenuItemID(IntPtr menu, int index);
    [DllImport("user32.dll")] private static extern IntPtr GetSubMenu(IntPtr menu, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetMenuString(IntPtr menu, uint item, StringBuilder text, int length, uint flags);
}
