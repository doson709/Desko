using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Desko.Interop;

namespace Desko.Shell
{
    // The real Explorer right-click menu (Open with, Send to, 7-Zip, Properties, ...) for files in a fence.
    internal sealed class ShellContextMenu
    {
        public const int CmdFirst = 1, CmdLast = 0x7000;
        // Verbs the fence handles itself, because a hidden fence item needs special care.
        public const int CmdRename = 0x7F00, CmdCopy = 0x7F02, CmdCut = 0x7F03, CmdDelete = 0x7F04;

        const uint CMF_NORMAL = 0x0, CMF_CANRENAME = 0x10, CMF_EXTENDEDVERBS = 0x100;
        const uint GCS_VERBW = 0x4;
        const uint CMIC_MASK_UNICODE = 0x4000, CMIC_MASK_PTINVOKE = 0x20000000,
                   CMIC_MASK_SHIFT_DOWN = 0x10000000, CMIC_MASK_CONTROL_DOWN = 0x40000000;

        [ComImport, Guid("000214E6-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellFolder
        {
            void _ParseDisplayName();
            void _EnumObjects();
            void _BindToObject();
            void _BindToStorage();
            void _CompareIDs();
            void _CreateViewObject();
            void _GetAttributesOf();
            [PreserveSig]
            int GetUIObjectOf(IntPtr hwndOwner, uint cidl,
                [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] IntPtr[] apidl,
                ref Guid riid, IntPtr reserved, out IntPtr ppv);
        }

        [ComImport, Guid("000214E4-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IContextMenu
        {
            [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint flags);
            [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX info);
            [PreserveSig] int GetCommandString(UIntPtr idCmd, uint type, IntPtr reserved, IntPtr name, uint cch);
        }

        [ComImport, Guid("000214F4-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IContextMenu2
        {
            [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint flags);
            [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX info);
            [PreserveSig] int GetCommandString(UIntPtr idCmd, uint type, IntPtr reserved, IntPtr name, uint cch);
            [PreserveSig] int HandleMenuMsg(uint msg, IntPtr wParam, IntPtr lParam);
        }

        [ComImport, Guid("BCFCE0A0-EC17-11D0-8D10-00A0C90F2719"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IContextMenu3
        {
            [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint flags);
            [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX info);
            [PreserveSig] int GetCommandString(UIntPtr idCmd, uint type, IntPtr reserved, IntPtr name, uint cch);
            [PreserveSig] int HandleMenuMsg(uint msg, IntPtr wParam, IntPtr lParam);
            [PreserveSig] int HandleMenuMsg2(uint msg, IntPtr wParam, IntPtr lParam, out IntPtr result);
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct CMINVOKECOMMANDINFOEX
        {
            public int cbSize;
            public uint fMask;
            public IntPtr hwnd;
            public IntPtr lpVerb;
            [MarshalAs(UnmanagedType.LPStr)] public string? lpParameters;
            [MarshalAs(UnmanagedType.LPStr)] public string? lpDirectory;
            public int nShow;
            public uint dwHotKey;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.LPStr)] public string? lpTitle;
            public IntPtr lpVerbW;
            [MarshalAs(UnmanagedType.LPWStr)] public string? lpParametersW;
            [MarshalAs(UnmanagedType.LPWStr)] public string? lpDirectoryW;
            [MarshalAs(UnmanagedType.LPWStr)] public string? lpTitleW;
            public Native.POINT ptInvoke;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern int SHParseDisplayName(string name, IntPtr bindCtx, out IntPtr pidl, uint sfgaoIn, out uint sfgaoOut);

        [DllImport("shell32.dll")]
        static extern int SHBindToParent(IntPtr pidl, ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out IShellFolder folder, out IntPtr pidlLast);

        static ShellContextMenu? _active;
        IContextMenu2? _cm2;
        IContextMenu3? _cm3;

        // Submenus such as "Open with" and "Send to" are filled lazily through these messages.
        public static bool TryHandleMessage(int msg, IntPtr wParam, IntPtr lParam, out IntPtr result)
        {
            result = IntPtr.Zero;
            var active = _active;
            if (active == null) return false;
            if (msg != Native.WM_INITMENUPOPUP && msg != Native.WM_DRAWITEM &&
                msg != Native.WM_MEASUREITEM && msg != Native.WM_MENUCHAR) return false;

            if (active._cm3 != null)
                return active._cm3.HandleMenuMsg2((uint)msg, wParam, lParam, out result) == 0;
            if (active._cm2 != null && msg != Native.WM_MENUCHAR && active._cm2.HandleMenuMsg((uint)msg, wParam, lParam) == 0)
            {
                if (msg != Native.WM_INITMENUPOPUP) result = new IntPtr(1);
                return true;
            }
            return false;
        }

        // Returns 0 when the menu was dismissed or a shell verb ran, otherwise one of the
        // caller's extra command ids (or CmdRename, which the caller implements itself).
        public static int Show(IntPtr hwnd, IReadOnlyList<string> paths, int x, int y,
                               IReadOnlyList<(int Id, string Text)> extra)
        {
            if (paths.Count == 0) return 0;
            var pidls = new List<IntPtr>();
            IContextMenu? cm = null;
            IntPtr menu = IntPtr.Zero;
            try
            {
                var cmPtr = GetUIObject(hwnd, paths, typeof(IContextMenu).GUID, pidls);
                if (cmPtr == IntPtr.Zero) return 0;
                cm = (IContextMenu)Marshal.GetObjectForIUnknown(cmPtr);
                Marshal.Release(cmPtr);

                menu = Native.CreatePopupMenu();
                foreach (var (id, text) in extra) Native.AppendMenu(menu, Native.MF_STRING, (UIntPtr)id, text);
                if (extra.Count > 0) Native.AppendMenu(menu, Native.MF_SEPARATOR, UIntPtr.Zero, null);

                bool shift = Native.IsKeyDown(Native.VK_SHIFT);
                uint flags = CMF_NORMAL | CMF_CANRENAME | (shift ? CMF_EXTENDEDVERBS : 0);
                cm.QueryContextMenu(menu, (uint)(extra.Count > 0 ? extra.Count + 1 : 0), CmdFirst, CmdLast, flags);

                _active = new ShellContextMenu { _cm2 = cm as IContextMenu2, _cm3 = cm as IContextMenu3 };
                int cmd;
                try { cmd = Native.TrackPopupMenuEx(menu, Native.TPM_RETURNCMD | Native.TPM_RIGHTBUTTON, x, y, hwnd, IntPtr.Zero); }
                finally { _active = null; }

                if (cmd >= CmdFirst && cmd <= CmdLast)
                {
                    int offset = cmd - CmdFirst;
                    switch (GetVerb(cm, offset)?.ToLowerInvariant())
                    {
                        case "rename": return CmdRename;
                        case "copy": return CmdCopy;
                        case "cut": return CmdCut;
                        case "delete": return CmdDelete;
                    }
                    Invoke(cm, offset, hwnd, x, y, Path.GetDirectoryName(paths[0]));
                    return 0;
                }
                return cmd;
            }
            finally
            {
                if (menu != IntPtr.Zero) Native.DestroyMenu(menu);
                if (cm != null) Marshal.ReleaseComObject(cm);
                foreach (var p in pidls) Marshal.FreeCoTaskMem(p);
            }
        }

        [ComImport, Guid("00000122-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IDropTarget
        {
            [PreserveSig] int DragEnter(System.Runtime.InteropServices.ComTypes.IDataObject data, uint keyState, Native.POINT pt, ref uint effect);
            [PreserveSig] int DragOver(uint keyState, Native.POINT pt, ref uint effect);
            [PreserveSig] int DragLeave();
            [PreserveSig] int Drop(System.Runtime.InteropServices.ComTypes.IDataObject data, uint keyState, Native.POINT pt, ref uint effect);
        }

        // Explorer's own drop handler for an item: folders take files in, zips add them, programs open them...
        // Null when the item accepts no drops (plain documents).
        public static IDropTarget? GetDropTarget(IntPtr hwnd, string path)
        {
            var pidls = new List<IntPtr>();
            try
            {
                var ptr = GetUIObject(hwnd, new[] { path }, typeof(IDropTarget).GUID, pidls);
                if (ptr == IntPtr.Zero) return null;
                var target = Marshal.GetObjectForIUnknown(ptr) as IDropTarget;
                Marshal.Release(ptr);
                return target;
            }
            catch (Exception) { return null; }
            finally
            {
                foreach (var p in pidls) Marshal.FreeCoTaskMem(p);
            }
        }

        [DllImport("shell32.dll")]
        static extern int SHDoDragDrop(IntPtr hwnd, IntPtr dataObject, IntPtr dropSource, uint okEffects, out uint effect);

        [ComImport, Guid("00000121-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IDropSource
        {
            [PreserveSig] int QueryContinueDrag([MarshalAs(UnmanagedType.Bool)] bool escapePressed, uint keyState);
            [PreserveSig] int GiveFeedback(uint effect);
        }

        // Standard drop source, except the caller may take over the drop (it is then cancelled for the target).
        [ComVisible(true)]
        sealed class DropSource : IDropSource
        {
            const int S_OK = 0, DRAGDROP_S_DROP = 0x40100, DRAGDROP_S_CANCEL = 0x40101, DRAGDROP_S_USEDEFAULTCURSORS = 0x40102;
            const uint MK_LBUTTON = 1, MK_RBUTTON = 2;
            readonly Func<bool> _interceptDrop;

            public DropSource(Func<bool> interceptDrop) => _interceptDrop = interceptDrop;

            public int QueryContinueDrag(bool escapePressed, uint keyState)
            {
                if (escapePressed) return DRAGDROP_S_CANCEL;
                if ((keyState & (MK_LBUTTON | MK_RBUTTON)) != 0) return S_OK;
                bool intercepted;
                try { intercepted = _interceptDrop(); }
                catch (Exception) { intercepted = false; }
                return intercepted ? DRAGDROP_S_CANCEL : DRAGDROP_S_DROP;
            }

            public int GiveFeedback(uint effect) => DRAGDROP_S_USEDEFAULTCURSORS;
        }

        // Drags the files exactly like Explorer does: same data object, drag image with icons, drop descriptions.
        // `interceptDrop` runs at the moment of release; returning true cancels the drop so the caller can handle it.
        // Returns false if the shell could not build a data object (caller falls back to a plain WPF drag).
        public static bool DoDragDrop(IntPtr hwnd, IReadOnlyList<string> paths, Func<bool> interceptDrop)
        {
            const uint DROPEFFECT_COPY = 1, DROPEFFECT_MOVE = 2, DROPEFFECT_LINK = 4;
            var pidls = new List<IntPtr>();
            IntPtr data = IntPtr.Zero, source = IntPtr.Zero;
            try
            {
                data = GetUIObject(hwnd, paths, new Guid("0000010e-0000-0000-C000-000000000046"), pidls);
                if (data == IntPtr.Zero) return false;
                source = Marshal.GetComInterfaceForObject(new DropSource(interceptDrop), typeof(IDropSource));
                SHDoDragDrop(hwnd, data, source, DROPEFFECT_COPY | DROPEFFECT_MOVE | DROPEFFECT_LINK, out _);
                return true;
            }
            finally
            {
                if (source != IntPtr.Zero) Marshal.Release(source);
                if (data != IntPtr.Zero) Marshal.Release(data);
                foreach (var p in pidls) Marshal.FreeCoTaskMem(p);
            }
        }

        // All items must share one parent folder (always true inside a fence). Caller frees `pidls`.
        static IntPtr GetUIObject(IntPtr hwnd, IReadOnlyList<string> paths, Guid iid, List<IntPtr> pidls)
        {
            IShellFolder? parent = null;
            try
            {
                var children = new IntPtr[paths.Count];
                var iidFolder = typeof(IShellFolder).GUID;
                for (int i = 0; i < paths.Count; i++)
                {
                    if (SHParseDisplayName(paths[i], IntPtr.Zero, out var pidl, 0, out _) != 0 || pidl == IntPtr.Zero)
                        return IntPtr.Zero;
                    pidls.Add(pidl);
                    if (SHBindToParent(pidl, ref iidFolder, out var folder, out var child) != 0) return IntPtr.Zero;
                    children[i] = child;
                    if (parent == null) parent = folder;
                    else Marshal.ReleaseComObject(folder);
                }
                return parent!.GetUIObjectOf(hwnd, (uint)children.Length, children, ref iid, IntPtr.Zero, out var ptr) == 0
                    ? ptr
                    : IntPtr.Zero;
            }
            finally
            {
                if (parent != null) Marshal.ReleaseComObject(parent);
            }
        }

        static string? GetVerb(IContextMenu cm, int offset)
        {
            IntPtr buf = Marshal.AllocHGlobal(1024);
            try
            {
                Marshal.WriteInt16(buf, 0);
                return cm.GetCommandString((UIntPtr)offset, GCS_VERBW, IntPtr.Zero, buf, 512) == 0
                    ? Marshal.PtrToStringUni(buf)
                    : null;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        static void Invoke(IContextMenu cm, int offset, IntPtr hwnd, int x, int y, string? directory)
        {
            uint mask = CMIC_MASK_UNICODE | CMIC_MASK_PTINVOKE;
            if (Native.IsKeyDown(Native.VK_SHIFT)) mask |= CMIC_MASK_SHIFT_DOWN;
            if (Native.IsKeyDown(Native.VK_CONTROL)) mask |= CMIC_MASK_CONTROL_DOWN;
            var info = new CMINVOKECOMMANDINFOEX
            {
                cbSize = Marshal.SizeOf<CMINVOKECOMMANDINFOEX>(),
                fMask = mask,
                hwnd = hwnd,
                lpVerb = new IntPtr(offset),
                lpVerbW = new IntPtr(offset),
                lpDirectoryW = directory,
                nShow = 1,
                ptInvoke = new Native.POINT(x, y),
            };
            cm.InvokeCommand(ref info);
        }
    }
}
