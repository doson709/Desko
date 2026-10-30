using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Desko.Interop;
using Desko.Models;

namespace Desko.Core
{
    // One fence layout per display configuration (which monitors, where, at what DPI), so docking a laptop,
    // plugging in a second screen or changing the resolution puts every fence back where it was for that setup.
    internal static class DisplayLayouts
    {
        delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, ref Native.RECT rect, IntPtr data);

        [DllImport("user32.dll")]
        static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);

        [DllImport("shcore.dll")]
        static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);

        // e.g. "-1920,0,0,1080@96|0,0,1920,1080@120"
        public static string CurrentSignature()
        {
            var parts = new List<string>();
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr m, IntPtr _, ref Native.RECT r, IntPtr _) =>
            {
                uint dpi = 96;
                try { GetDpiForMonitor(m, 0, out dpi, out _); } catch (DllNotFoundException) { }
                parts.Add($"{r.Left},{r.Top},{r.Right},{r.Bottom}@{dpi}");
                return true;
            }, IntPtr.Zero);
            parts.Sort(StringComparer.Ordinal);
            return string.Join("|", parts);
        }

        // Records where each fence is (by group: tabs share one rectangle) under this signature.
        public static void Remember(AppConfig config, string signature)
        {
            var layout = new Dictionary<string, FenceRect>();
            foreach (var f in config.Fences)
                layout[FenceManager.GroupKey(f)] = new FenceRect { X = f.X, Y = f.Y, Width = f.Width, Height = f.Height };
            config.Layouts[signature] = layout;
        }

        // Applies the layout saved for this signature, if any. Returns whether one was applied.
        public static bool Restore(AppConfig config, string signature)
        {
            if (!config.Layouts.TryGetValue(signature, out var layout)) return false;
            foreach (var f in config.Fences)
            {
                if (!layout.TryGetValue(FenceManager.GroupKey(f), out var r)) continue;
                f.X = r.X; f.Y = r.Y; f.Width = r.Width; f.Height = r.Height;
            }
            return true;
        }

        // An unknown setup: fences left off-screen are cascaded onto the primary monitor instead of piling up.
        public static void BringOffscreenHome(AppConfig config)
        {
            var work = Native.WorkAreaFromPoint(new Native.POINT(0, 0));
            double s = Native.ScaleForPoint(new Native.POINT(work.Left + 1, work.Top + 1));
            int step = (int)(28 * s), n = 0;
            foreach (var group in config.Fences.GroupBy(FenceManager.GroupKey))
            {
                var f = group.First();
                var r = new Native.RECT(f.X, f.Y, f.X + f.Width, f.Y + (int)(30 * s));
                if (Native.MonitorFromRect(ref r, Native.MONITOR_DEFAULTTONULL) != IntPtr.Zero) continue;
                int x = work.Left + (int)(40 * s) + n * step, y = work.Top + (int)(40 * s) + n * step;
                foreach (var tab in group) { tab.X = x; tab.Y = y; }
                n++;
            }
        }
    }
}
