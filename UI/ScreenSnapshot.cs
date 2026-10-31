using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Desko.Interop;

namespace Desko.UI
{
    // A picture of part of the desktop exactly as it is on screen, for standing in for Explorer's icons while a
    // fence is dragged: drawn stand-ins never quite match Explorer's text (its soft shadow, where it wraps and
    // cuts names), a picture of them does to the pixel. Only taken when no other window lies over that part
    // (the picture would carry it along), and kept in memory only.
    internal static class ScreenSnapshot
    {
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out Native.RECT rect);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dest, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

        const uint SRCCOPY = 0x00CC0020, CAPTUREBLT = 0x40000000;
        const int DWMWA_CLOAKED = 14, WS_EX_TRANSPARENT = 0x20;

        public static BitmapSource? Take(Native.RECT area)
        {
            if (area.Width <= 0 || area.Height <= 0 || Covered(area)) return null;
            IntPtr screen = GetDC(IntPtr.Zero), dc = IntPtr.Zero, bitmap = IntPtr.Zero;
            try
            {
                dc = CreateCompatibleDC(screen);
                bitmap = CreateCompatibleBitmap(screen, area.Width, area.Height);
                var old = SelectObject(dc, bitmap);
                bool ok = BitBlt(dc, 0, 0, area.Width, area.Height, screen, area.Left, area.Top, SRCCOPY | CAPTUREBLT);
                SelectObject(dc, old);
                if (!ok) return null;
                var source = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            catch (Exception) { return null; }
            finally
            {
                if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
                if (dc != IntPtr.Zero) DeleteDC(dc);
                ReleaseDC(IntPtr.Zero, screen);
            }
        }

        // Another app's window over that part of the screen (the fences sit below every app window).
        static bool Covered(Native.RECT area)
        {
            bool covered = false;
            uint own = (uint)Environment.ProcessId;
            var cls = new StringBuilder(64);
            Native.EnumWindows((h, _) =>
            {
                if (!IsWindowVisible(h) || IsIconic(h)) return true;
                if (DwmGetWindowAttribute(h, DWMWA_CLOAKED, out int cloaked, 4) == 0 && cloaked != 0) return true;
                Native.GetWindowThreadProcessId(h, out uint pid);
                if (pid == own) return true;
                if ((Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE).ToInt64() & WS_EX_TRANSPARENT) != 0) return true; // click-through overlays
                cls.Clear();
                GetClassName(h, cls, 64);
                if (cls.ToString() is "Progman" or "WorkerW" or "CEF-OSC-WIDGET") return true;
                if (!GetWindowRect(h, out var r) || r.Right <= area.Left || r.Left >= area.Right || r.Bottom <= area.Top || r.Top >= area.Bottom) return true;
                covered = true;
                return false;
            }, IntPtr.Zero);
            return covered;
        }
    }
}
