using System;
using System.Collections.Generic;
using Desko.Interop;

namespace Desko.Core
{
    // Snaps fence rectangles (physical pixels) to screen edges, other fences, then the grid.
    internal static class Snapper
    {
        public static void SnapMove(ref Native.RECT r, IReadOnlyList<Native.RECT> others, Native.RECT work,
                                    int threshold, int grid, int gap)
        {
            var xs = Targets(others, work, gap, horizontal: true);
            var ys = Targets(others, work, gap, horizontal: false);
            int dx = Best(xs, threshold, r.Left, r.Right) ?? GridDelta(r.Left, work.Left, grid);
            int dy = Best(ys, threshold, r.Top, r.Bottom) ?? GridDelta(r.Top, work.Top, grid);
            r.Left += dx; r.Right += dx;
            r.Top += dy; r.Bottom += dy;
        }

        public static void SnapSize(ref Native.RECT r, int edge, IReadOnlyList<Native.RECT> others, Native.RECT work,
                                    int threshold, int grid, int gap, int minW, int minH)
        {
            bool left = edge is 1 or 4 or 7, right = edge is 2 or 5 or 8;
            bool top = edge is 3 or 4 or 5, bottom = edge is 6 or 7 or 8;
            var xs = Targets(others, work, gap, horizontal: true);
            var ys = Targets(others, work, gap, horizontal: false);

            if (left) r.Left += Best(xs, threshold, r.Left) ?? GridDelta(r.Left, work.Left, grid);
            if (right) r.Right += Best(xs, threshold, r.Right) ?? GridDelta(r.Right, work.Left, grid);
            if (top) r.Top += Best(ys, threshold, r.Top) ?? GridDelta(r.Top, work.Top, grid);
            if (bottom) r.Bottom += Best(ys, threshold, r.Bottom) ?? GridDelta(r.Bottom, work.Top, grid);

            if (r.Width < minW) { if (left) r.Left = r.Right - minW; else r.Right = r.Left + minW; }
            if (r.Height < minH) { if (top) r.Top = r.Bottom - minH; else r.Bottom = r.Top + minH; }
        }

        static List<int> Targets(IReadOnlyList<Native.RECT> others, Native.RECT work, int gap, bool horizontal)
        {
            var list = new List<int>();
            if (horizontal) { list.Add(work.Left); list.Add(work.Right); }
            else { list.Add(work.Top); list.Add(work.Bottom); }
            foreach (var o in others)
            {
                int a = horizontal ? o.Left : o.Top, b = horizontal ? o.Right : o.Bottom;
                list.Add(a); list.Add(b); list.Add(a - gap); list.Add(b + gap);
            }
            return list;
        }

        static int? Best(List<int> targets, int threshold, params int[] edges)
        {
            int? best = null;
            foreach (int e in edges)
                foreach (int t in targets)
                {
                    int d = t - e;
                    if (Math.Abs(d) <= threshold && (best == null || Math.Abs(d) < Math.Abs(best.Value))) best = d;
                }
            return best;
        }

        static int GridDelta(int value, int origin, int grid) =>
            grid <= 1 ? 0 : (int)Math.Round((value - origin) / (double)grid) * grid + origin - value;
    }
}
