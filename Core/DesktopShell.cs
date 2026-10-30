using System;
using Desko.Interop;

namespace Desko.Core
{
    // Helpers for poking at Explorer's desktop window (Progman → SHELLDLL_DefView → SysListView32).
    internal static class DesktopShell
    {
        const int CmdToggleDesktopIcons = 0x7402;

        // Looked up once: it is needed for every fenced item on every refresh, and the Desktop watcher
        // is bound to this folder for the whole session anyway.
        static string? _desktopPath;
        public static string DesktopPath => _desktopPath ??= Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

        public static IntPtr Progman => Native.FindWindow("Progman", null);

        public static IntPtr FindDefView()
        {
            IntPtr dv = Native.FindWindowEx(Progman, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (dv != IntPtr.Zero) return dv;

            // With a slideshow wallpaper the DefView is moved into one of the WorkerW windows.
            IntPtr found = IntPtr.Zero;
            Native.EnumWindows((h, _) =>
            {
                if (Native.ClassOf(h) != "WorkerW") return true;
                var child = Native.FindWindowEx(h, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (child == IntPtr.Zero) return true;
                found = child;
                return false;
            }, IntPtr.Zero);
            return found;
        }

        public static IntPtr FindListView()
        {
            var dv = FindDefView();
            return dv == IntPtr.Zero ? IntPtr.Zero : Native.FindWindowEx(dv, IntPtr.Zero, "SysListView32", null);
        }

        public static bool IconsVisible
        {
            get
            {
                var lv = FindListView();
                return lv == IntPtr.Zero || Native.IsWindowVisible(lv);
            }
        }

        public static void SetIconsVisible(bool visible)
        {
            if (IconsVisible == visible) return;
            var dv = FindDefView();
            if (dv != IntPtr.Zero)
                Native.PostMessage(dv, Native.WM_COMMAND, new IntPtr(CmdToggleDesktopIcons), IntPtr.Zero);
        }

        static bool IsDesktopHost(IntPtr h)
        {
            var cls = Native.ClassOf(h);
            return cls == "Progman" || cls == "WorkerW";
        }

        // Cheap enough to call from inside the low-level mouse hook.
        public static bool IsDesktopAt(Native.POINT pt)
        {
            IntPtr h = Native.WindowFromPoint(pt);
            switch (Native.ClassOf(h))
            {
                case "SysListView32":
                    var dv = Native.GetParent(h);
                    return Native.ClassOf(dv) == "SHELLDLL_DefView" && IsDesktopHost(Native.GetParent(dv));
                case "SHELLDLL_DefView":
                    return IsDesktopHost(Native.GetParent(h));
                case "Progman":
                case "WorkerW":
                    return true;
                default:
                    return false;
            }
        }

        // A few ms of cross-process COM to Explorer – never call this from the hook callback itself.
        public static bool IsIconAt(Native.POINT pt) => Shell.DesktopIconPlacer.IsIconAt(pt) ?? false;
    }
}
