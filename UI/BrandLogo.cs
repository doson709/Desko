using System.Windows;
using System.Windows.Media;

namespace Desko.UI
{
    // The Desko mark: a rounded tile in a sky-to-blue gradient holding a 2×2 grid of white tiles
    // (a desktop sorted into fences). Used for the tray icon; assets/desko.ico and the README images
    // are rendered from the same drawing.
    internal static class BrandLogo
    {
        public static readonly Color Sky = Color.FromRgb(0x38, 0xBD, 0xF8);
        public static readonly Color Blue = Color.FromRgb(0x25, 0x63, 0xEB);

        public static void Draw(DrawingContext dc, double size, double x = 0, double y = 0)
        {
            double s = size;
            var background = new LinearGradientBrush(Sky, Blue, new Point(0, 0), new Point(1, 1));
            background.Freeze();
            dc.DrawRoundedRectangle(background, null, new Rect(x + s * 0.03, y + s * 0.03, s * 0.94, s * 0.94), s * 0.24, s * 0.24);

            var solid = new SolidColorBrush(Colors.White);
            var soft = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));
            solid.Freeze();
            soft.Freeze();
            double tile = s * 0.25, gap = s * 0.06, start = (s - (tile * 2 + gap)) / 2, r = s * 0.06;
            for (int row = 0; row < 2; row++)
                for (int col = 0; col < 2; col++)
                {
                    var brush = row == col ? solid : soft;
                    var rect = new Rect(x + start + col * (tile + gap), y + start + row * (tile + gap), tile, tile);
                    dc.DrawRoundedRectangle(brush, null, rect, r, r);
                }
        }
    }
}
