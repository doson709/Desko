using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Desko.UI
{
    // Light/dark + accent for Desko's own windows, with the Mica backdrop and a matching title bar.
    internal static class DialogTheme
    {
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        [StructLayout(LayoutKind.Sequential)] struct MARGINS { public int L, R, T, B; }
        [DllImport("dwmapi.dll")] static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS m);

        public static void Apply(Window w)
        {
            bool light = Theme.IsLight;
            Set(w, "Fg", light ? Color.FromRgb(0x1B, 0x1B, 0x1B) : Colors.White);
            Set(w, "FgSubtle", light ? Color.FromArgb(0x99, 0, 0, 0) : Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF));
            Set(w, "Card", light ? Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
            Set(w, "CardBorder", light ? Color.FromArgb(0x14, 0, 0, 0) : Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF));
            Set(w, "ControlFill", light ? Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
            var accent = Theme.Accent(lightSurface: light);
            Set(w, "Accent", accent);
            Set(w, "OnAccent", Theme.Luminance(accent) > 0.5 ? Colors.Black : Colors.White);

            var hwnd = new WindowInteropHelper(w).Handle;
            if (hwnd == IntPtr.Zero) return;
            try
            {
                int dark = light ? 0 : 1;
                DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int)); // dark title bar
                int mica = 2;                                            // DWMSBT_MAINWINDOW (Mica)
                if (DwmSetWindowAttribute(hwnd, 38, ref mica, sizeof(int)) == 0)
                {
                    var margins = new MARGINS { L = -1, R = -1, T = -1, B = -1 };
                    DwmExtendFrameIntoClientArea(hwnd, ref margins);
                    HwndSource.FromHwnd(hwnd)!.CompositionTarget.BackgroundColor = Colors.Transparent;
                    w.Background = Brushes.Transparent;
                }
                else w.Background = new SolidColorBrush(light ? Color.FromRgb(0xF3, 0xF3, 0xF3) : Color.FromRgb(0x20, 0x20, 0x20));
            }
            catch (DllNotFoundException) { }
        }

        static void Set(Window w, string key, Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            w.Resources[key] = b;
        }
    }
}
