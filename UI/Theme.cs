using System;
using System.Windows.Media;
using Microsoft.Win32;

namespace Desko.UI
{
    // Windows' light/dark setting and accent colour, re-read when the system announces a change.
    internal static class Theme
    {
        public static bool IsLight { get; private set; }

        // Accent shades as Windows itself uses them: a lighter one on dark surfaces, a deeper one on light.
        public static Color AccentOnDark { get; private set; } = Color.FromRgb(0x60, 0xCD, 0xFF);
        public static Color AccentOnLight { get; private set; } = Color.FromRgb(0x00, 0x5F, 0xB8);

        static Theme() => Refresh();

        public static void Refresh()
        {
            try
            {
                using (var personalize = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                    IsLight = personalize?.GetValue("AppsUseLightTheme") is int light && light != 0;

                // AccentPalette: 8 RGBA entries, lightest to darkest (index 3 is the accent itself).
                using var accent = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent");
                if (accent?.GetValue("AccentPalette") is byte[] p && p.Length >= 32)
                {
                    AccentOnDark = Color.FromRgb(p[4], p[5], p[6]);    // Light2
                    AccentOnLight = Color.FromRgb(p[16], p[17], p[18]); // Dark1
                    return;
                }
                using var dwm = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
                if (dwm?.GetValue("AccentColor") is int abgr)
                {
                    uint u = unchecked((uint)abgr);
                    var c = Color.FromRgb((byte)(u & 0xFF), (byte)((u >> 8) & 0xFF), (byte)((u >> 16) & 0xFF));
                    AccentOnDark = AccentOnLight = c;
                }
            }
            catch (Exception) { }
        }

        public static Color Accent(bool lightSurface) => lightSurface ? AccentOnLight : AccentOnDark;

        public static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

        // Relative luminance, 0 (black) .. 1 (white).
        public static double Luminance(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;
    }
}
