using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text.RegularExpressions;
using Desko.Core;
using Desko.Interop;
using Microsoft.Win32;

namespace Desko.Shell
{
    // Items without a file behind them (Recycle Bin, This PC, Start-menu apps...): they arrive as a
    // "Shell IDList Array" only, so the fence stores a shortcut to them instead.
    internal static class VirtualItems
    {
        public const string IdListFormat = "Shell IDList Array";

        // The desktop icons that can be switched off in "Desktop icon settings".
        static readonly HashSet<string> DesktopIconClsids = new(StringComparer.OrdinalIgnoreCase)
        {
            "{645FF040-5081-101B-9F08-00AA002F954E}", // Recycle Bin
            "{20D04FE0-3AEA-1069-A2D8-08002B30309D}", // This PC
            "{59031a47-3f72-44a7-89c5-5595fe6b30ee}", // User's files
            "{F02C1A0D-BE21-4350-88B0-7367FC96EF3C}", // Network
            "{5399E694-6CE5-4D6C-8FCE-AD5AB0FF0A6F}", // Control Panel
        };

        const string HideIconsKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\NewStartPanel";
        const uint SIGDN_NORMALDISPLAY = 0, SIGDN_DESKTOPABSOLUTEPARSING = 0x80028000;

        [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellLinkW
        {
            void _GetPath();
            void _GetIDList();
            [PreserveSig] int SetIDList(IntPtr pidl);
        }

        [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
        class ShellLink { }

        [DllImport("shell32.dll")] static extern IntPtr ILCombine(IntPtr parent, IntPtr child);
        [DllImport("shell32.dll")] static extern int SHGetNameFromIDList(IntPtr pidl, uint sigdn, out IntPtr name);
        [DllImport("shell32.dll")] static extern void SHChangeNotify(int eventId, uint flags, IntPtr a, IntPtr b);
        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr w, IntPtr l);

        // Absolute PIDLs of the dragged items; the caller frees them with Marshal.FreeCoTaskMem.
        public static List<IntPtr> ReadIdList(System.Windows.IDataObject data)
        {
            var result = new List<IntPtr>();
            if (data.GetData(IdListFormat) is not MemoryStream ms) return result;
            byte[] bytes = ms.ToArray();
            if (bytes.Length < 8) return result;
            IntPtr mem = Marshal.AllocHGlobal(bytes.Length);
            try
            {
                Marshal.Copy(bytes, 0, mem, bytes.Length);
                // CIDA: count, offset of parent folder, offsets of each child (relative to the parent).
                int count = Marshal.ReadInt32(mem);
                IntPtr parent = mem + Marshal.ReadInt32(mem, 4);
                for (int i = 0; i < count && 8 + 4 * i + 4 <= bytes.Length; i++)
                {
                    IntPtr abs = ILCombine(parent, mem + Marshal.ReadInt32(mem, 8 + 4 * i));
                    if (abs != IntPtr.Zero) result.Add(abs);
                }
            }
            finally { Marshal.FreeHGlobal(mem); }
            return result;
        }

        public static string? GetName(IntPtr pidl, bool parsing)
        {
            if (SHGetNameFromIDList(pidl, parsing ? SIGDN_DESKTOPABSOLUTEPARSING : SIGDN_NORMALDISPLAY, out var ptr) != 0)
                return null;
            try { return Marshal.PtrToStringUni(ptr); }
            finally { Marshal.FreeCoTaskMem(ptr); }
        }

        // "{645FF040-...}" when the item is one of the standard desktop icons, else null.
        public static string? DesktopIconClsid(IntPtr pidl)
        {
            var parsing = GetName(pidl, parsing: true);
            if (parsing == null) return null;
            var m = Regex.Match(parsing, @"^::(\{[0-9A-Fa-f\-]{36}\})$");
            return m.Success && DesktopIconClsids.Contains(m.Groups[1].Value) ? m.Groups[1].Value : null;
        }

        public static bool CreateShortcut(IntPtr pidl, string path)
        {
            object? link = null;
            try
            {
                link = new ShellLink();
                if (((IShellLinkW)link).SetIDList(pidl) != 0) return false;
                ((IPersistFile)link).Save(path, true);
                return File.Exists(path);
            }
            catch (Exception ex)
            {
                Models.Log.Error(ex);
                return false;
            }
            finally
            {
                if (link != null) Marshal.ReleaseComObject(link);
            }
        }

        // Same switch as Settings → Desktop icon settings. Returns true if it changed.
        public static bool SetDesktopIconHidden(string clsid, bool hidden)
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(HideIconsKey);
                int current = key.GetValue(clsid) is int v ? v : 0;
                int wanted = hidden ? 1 : 0;
                if (current == wanted) return false;
                key.SetValue(clsid, wanted, RegistryValueKind.DWord);
                return true;
            }
            catch (Exception ex)
            {
                Models.Log.Error(ex);
                return false;
            }
        }

        public static void RefreshDesktop()
        {
            const int SHCNE_ASSOCCHANGED = 0x08000000, WM_KEYDOWN = 0x0100, VK_F5 = 0x74;
            SHChangeNotify(SHCNE_ASSOCCHANGED, 0, IntPtr.Zero, IntPtr.Zero);
            var listView = DesktopShell.FindListView();
            if (listView != IntPtr.Zero) PostMessage(listView, WM_KEYDOWN, new IntPtr(VK_F5), IntPtr.Zero);
        }
    }
}
