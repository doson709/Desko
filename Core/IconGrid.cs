using System;
using System.Collections.Generic;
using System.Linq;
using Desko.Interop;
using Desko.Shell;

namespace Desko.Core
{
    // The desktop's icon grid. Windows' own "Align icons to grid" has to be off (it would snap the icons inside
    // fences too and refuses the off-screen parking spot), so loose icons are aligned here instead.
    // Explorer gives an icon's position as the top-left corner of its picture, which sits centred in its cell
    // (the label hangs below it), so positions and cells differ by Offset.
    internal static class IconGrid
    {
        // Size of one icon cell (from Explorer; changes with the desktop icon size).
        public static Native.POINT Cell = new(76, 91);
        // Where the picture sits inside its cell.
        public static Native.POINT Offset = new(18, 2);
        // How Explorer draws an icon around its position (measured with LVM_GETITEMRECT): the picture a few pixels
        // below it, the label under the picture (as wide as the cell at most, centred), in the icon title font
        // (which follows the "text size" setting). Stand-ins and folder portals draw their items the same way.
        public static int PictureDrop = 5, LabelTop = 48;
        public static string LabelFontFamily = "Segoe UI";
        public static double LabelFontPx = 12;

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        struct LOGFONT
        {
            public int Height, Width, Escapement, Orientation, Weight;
            public byte Italic, Underline, StrikeOut, CharSet, OutPrecision, ClipPrecision, Quality, PitchAndFamily;
            [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 32)] public string FaceName;
        }
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        static extern bool SystemParametersInfo(uint action, uint size, ref LOGFONT font, uint flags);
        const uint SPI_GETICONTITLELOGFONT = 0x1F;

        // Picks up the desktop's current icon size. Returns true if the grid changed.
        public static bool Refresh()
        {
            if (DesktopIcons.Spacing is not Native.POINT cell) return false;
            int icon = DesktopIcons.GetDesktopIconSize() ?? 48;
            double scale = Native.GetDpiForSystem() / 96.0;
            var offset = new Native.POINT(Math.Max(0, (cell.X - (int)Math.Round(icon * scale)) / 2), (int)Math.Round(2 * scale));
            bool changed = cell.X != Cell.X || cell.Y != Cell.Y || offset.X != Offset.X || offset.Y != Offset.Y;
            Cell = cell;
            Offset = offset;
            PictureDrop = (int)Math.Round(5 * scale);
            LabelTop = (int)Math.Round(icon * scale + 8 * scale);
            var font = new LOGFONT();
            if (SystemParametersInfo(SPI_GETICONTITLELOGFONT, (uint)System.Runtime.InteropServices.Marshal.SizeOf<LOGFONT>(), ref font, 0) && font.Height != 0)
            {
                LabelFontFamily = string.IsNullOrEmpty(font.FaceName) ? "Segoe UI" : font.FaceName;
                LabelFontPx = Math.Abs(font.Height);
            }
            return changed;
        }

        // The cell an icon at this position occupies.
        public static Native.RECT CellRect(Native.POINT position) =>
            new(position.X - Offset.X, position.Y - Offset.Y, position.X - Offset.X + Cell.X, position.Y - Offset.Y + Cell.Y);

        public static Native.POINT CellCenter(Native.POINT position) =>
            new(position.X - Offset.X + Cell.X / 2, position.Y - Offset.Y + Cell.Y / 2);

        // The icon position that puts an icon in the cell with this top-left corner.
        public static Native.POINT InCell(int cellX, int cellY) => new(cellX + Offset.X, cellY + Offset.Y);

        static Native.RECT WorkFor(Native.POINT cellCorner) =>
            Native.WorkAreaFromPoint(new Native.POINT(cellCorner.X + Cell.X / 2, cellCorner.Y + Cell.Y / 2));

        // The grid position nearest to an icon position (Explorer's grid starts at the work area's corner).
        public static Native.POINT Snap(Native.POINT position)
        {
            var corner = new Native.POINT(position.X - Offset.X, position.Y - Offset.Y);
            var work = WorkFor(corner);
            int cols = Math.Max(1, work.Width / Cell.X), rows = Math.Max(1, work.Height / Cell.Y);
            int col = Math.Clamp((int)Math.Round((corner.X - work.Left) / (double)Cell.X), 0, cols - 1);
            int row = Math.Clamp((int)Math.Round((corner.Y - work.Top) / (double)Cell.Y), 0, rows - 1);
            return InCell(work.Left + col * Cell.X, work.Top + row * Cell.Y);
        }

        public static bool IsAligned(Native.POINT position)
        {
            var s = Snap(position);
            return s.X == position.X && s.Y == position.Y;
        }

        // Icon positions in the free grid cells nearest to `near` (an icon position; its monitor first), skipping
        // cells that overlap `blocked` areas (fences) or `icons` (cell rectangles). Each returned cell is taken, so
        // asking for several gives distinct cells.
        public static List<Native.POINT> FreeCells(Native.POINT near, int count, IEnumerable<Native.RECT> blocked, IEnumerable<Native.RECT> icons)
        {
            var result = new List<Native.POINT>();
            if (count <= 0) return result;
            var taken = blocked.Concat(icons).ToList();
            var nearCorner = new Native.POINT(near.X - Offset.X, near.Y - Offset.Y);
            var home = WorkFor(nearCorner);
            foreach (var work in Native.WorkAreas().OrderBy(a => a.Equals(home) ? 0 : 1))
            {
                var cells = new List<Native.POINT>();
                for (int x = work.Left; x + Cell.X <= work.Right; x += Cell.X)
                    for (int y = work.Top; y + Cell.Y <= work.Bottom; y += Cell.Y)
                        cells.Add(new Native.POINT(x, y));
                foreach (var c in cells.OrderBy(c => Distance(c, nearCorner)))
                {
                    var r = new Native.RECT(c.X, c.Y, c.X + Cell.X, c.Y + Cell.Y);
                    if (taken.Any(t => Overlaps(t, r))) continue;
                    result.Add(InCell(c.X, c.Y));
                    taken.Add(r);
                    if (result.Count == count) return result;
                }
            }
            // A completely full desktop: stack the rest at the corner of the main work area.
            while (result.Count < count) result.Add(InCell(home.Left, home.Top));
            return result;
        }

        static double Distance(Native.POINT a, Native.POINT b) => Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2);

        public static bool Overlaps(Native.RECT a, Native.RECT b) =>
            a.Left < b.Right && b.Left < a.Right && a.Top < b.Bottom && b.Top < a.Bottom;
    }
}
