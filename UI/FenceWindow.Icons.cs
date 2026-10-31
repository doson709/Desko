using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Desko.Core;
using Desko.Interop;
using Desko.Models;
using Desko.Shell;

namespace Desko.UI
{
    // Normal fences: the real desktop icons, laid out on the fence's grid (the desktop's own icon cell size).
    // Rows that do not fit are scrolled with the wheel; their icons, like those of other tabs or of a rolled-up
    // fence, wait off screen. The manager does the actual moving (FenceManager.LayoutAll / LayoutWindow).
    internal partial class FenceWindow
    {
        List<string> _iconOrder = new(); // the active tab's icons, in display order
        int _scrollRow;
        bool _dragMode;

        // Icons show unless the fence is hidden, rolled up or a portal.
        public bool ShowsIcons => IsDesktopFence && Handle != IntPtr.Zero && IsVisible && !Collapsed;

        // Where the icons go, in screen pixels: under the title bar, inside a small margin.
        Native.RECT IconArea(Native.RECT window)
        {
            int pad = (int)Math.Round(4 * DpiScale);
            int top = window.Top + (int)Math.Round(TitleHeightDip * DpiScale) + pad;
            return new Native.RECT(window.Left + pad, top, window.Right - pad, Math.Max(top, window.Bottom - pad));
        }

        public Native.RECT BodyRect => IconArea(ScreenRect);

        // The whole area the fence claims, unfolded (also while rolled up or peeking): icons outside the fences
        // stay clear of it, so unfolding never lands on them.
        public Native.RECT RestingRect => new(Config.X, Config.Y, Config.X + Config.Width, Config.Y + Config.Height);

        (int Cols, int Rows, int X0, int Y0) IconGridOf(Native.RECT window)
        {
            var area = IconArea(window);
            var cell = IconGrid.Cell;
            int cols = Math.Max(1, area.Width / cell.X);
            int rows = Math.Max(0, area.Height / cell.Y);
            int x0 = area.Left + Math.Max(0, area.Width - cols * cell.X) / 2;
            return (cols, rows, x0, area.Top);
        }

        // Where every member of this fence (all tabs) should be right now.
        public void CollectIconMoves(List<(string, Native.POINT)> moves, Native.POINT park)
        {
            if (Handle == IntPtr.Zero) return;
            foreach (var tab in _mgr.TabsOf(Config))
            {
                if (tab.IsPortal) continue;
                if (tab != Config || !ShowsIcons || _liftParked)
                {
                    foreach (var name in tab.Members) moves.Add((name, park));
                    continue;
                }
                var (cols, rows, x0, y0) = IconGridOf(ScreenRect);
                var cell = IconGrid.Cell;
                int totalRows = (_iconOrder.Count + cols - 1) / cols;
                _scrollRow = Math.Clamp(_scrollRow, 0, Math.Max(0, totalRows - rows));
                for (int i = 0; i < _iconOrder.Count; i++)
                {
                    int row = i / cols - _scrollRow, col = i % cols;
                    moves.Add((_iconOrder[i], row >= 0 && row < rows ? IconGrid.InCell(x0 + col * cell.X, y0 + row * cell.Y) : park));
                }
                // Members whose file is gone (deleted, not yet back from the Recycle Bin) have no icon to place.
                UpdateScrollBar(totalRows, rows);
            }
            if (!ShowsIcons) ScrollLayer.Visibility = Visibility.Collapsed;
        }

        sealed record Entry(string Name, string Display, bool IsFolder, string Ext, DateTime Modified, long Size);

        // The active tab's members that exist, in the fence's sort order.
        public void RefreshIconOrder()
        {
            if (!IsDesktopFence)
            {
                _iconOrder.Clear();
                return;
            }
            var entries = new List<Entry>();
            foreach (var name in Config.Members.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string? path = DesktopShell.MemberPath(name);
                if (path == null) continue;
                if (path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
                {
                    // Recycle Bin & co. take a place only while the desktop shows them.
                    if (!DesktopIcons.Has(name)) continue;
                    entries.Add(new Entry(name, VirtualItems.DisplayName(path) ?? name, true, "", DateTime.MinValue, 0));
                    continue;
                }
                try
                {
                    FileSystemInfo fsi = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
                    entries.Add(new Entry(name, ShellInfo.GetDisplayNameCached(path, fsi.LastWriteTimeUtc, name),
                        fsi is DirectoryInfo, Path.GetExtension(name), fsi.LastWriteTimeUtc, fsi is FileInfo fi ? fi.Length : 0));
                }
                catch (Exception ex) { Log.Error(ex); }
            }

            IEnumerable<Entry> sorted = Config.SortMode switch
            {
                SortMode.Name => entries.OrderBy(e => !e.IsFolder).ThenBy(e => e.Display, Logical),
                SortMode.Type => entries.OrderBy(e => !e.IsFolder).ThenBy(e => e.Ext, StringComparer.OrdinalIgnoreCase)
                                        .ThenBy(e => e.Display, Logical),
                SortMode.Date => entries.OrderByDescending(e => e.Modified),
                SortMode.Size => entries.OrderBy(e => !e.IsFolder).ThenByDescending(e => e.Size),
                _ => CustomOrder(entries),
            };
            _iconOrder = sorted.Select(e => e.Name).ToList();

            if (Config.SortMode == SortMode.Custom)
            {
                // Keep the order list in step (new icons are added at the end).
                var order = MergeOrder(Config.Order, _iconOrder);
                if (!order.SequenceEqual(Config.Order, StringComparer.OrdinalIgnoreCase))
                {
                    Config.Order = order;
                    _mgr.SaveSoon();
                }
            }

            EmptyHint.Text = "Kéo icon vào đây";
            EmptyHint.Visibility = _iconOrder.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (!_warmQueued)
            {
                _warmQueued = true;
                Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => { _warmQueued = false; WarmProxies(); });
            }
        }

        bool _warmQueued;

        // The custom order with the shown icons in `shown`'s order. Members that are not shown right now (deleted
        // and maybe restored from the Recycle Bin later, still being copied...) keep their place in the list, so
        // they come back to the same spot.
        static List<string> MergeOrder(List<string> order, IReadOnlyList<string> shown)
        {
            var set = shown.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var result = new List<string>(order.Count + shown.Count);
            int next = 0;
            foreach (var name in order)
            {
                if (!set.Contains(name)) result.Add(name);
                else if (next < shown.Count) result.Add(shown[next++]);
            }
            while (next < shown.Count) result.Add(shown[next++]);
            return result;
        }

        IEnumerable<Entry> CustomOrder(List<Entry> entries)
        {
            var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < Config.Order.Count; i++) index.TryAdd(Config.Order[i], i);
            // New icons go to the end, in name order.
            return entries.OrderBy(e => index.TryGetValue(e.Name, out var k) ? k : int.MaxValue).ThenBy(e => e.Display, Logical);
        }

        // Index in the display order of the cell under a screen point.
        int SlotAt(Native.POINT p)
        {
            var (cols, _, x0, y0) = IconGridOf(ScreenRect);
            var cell = IconGrid.Cell;
            int col = Math.Clamp((p.X - x0) / cell.X, 0, cols - 1);
            int row = Math.Max(0, (p.Y - y0) / cell.Y) + _scrollRow;
            return Math.Min(row * cols + col, _iconOrder.Count);
        }

        // Puts `names` (new members, or members being moved around) at the cell under `at`.
        public void InsertAt(IReadOnlyCollection<string> names, Native.POINT at)
        {
            var moving = names.ToHashSet(StringComparer.OrdinalIgnoreCase);
            bool reorder = names.All(n => _iconOrder.Contains(n, StringComparer.OrdinalIgnoreCase));
            // The icons land exactly in the cell they were dropped on (the others close up around them).
            int slot = SlotAt(at);
            var display = _iconOrder.Where(n => !moving.Contains(n)).ToList();
            display.InsertRange(Math.Clamp(slot, 0, display.Count), names);
            Config.Order = MergeOrder(Config.Order.Where(n => !moving.Contains(n)).ToList(), display);
            // Rearranging by hand means the fence is in a custom order from now on.
            if (reorder) Config.SortMode = SortMode.Custom;
            _iconOrder = display;
        }

        // The member whose icon is under a screen point (its middle part: the gaps keep meaning "into the fence").
        string? MemberAt(Native.POINT p)
        {
            var (cols, rows, x0, y0) = IconGridOf(ScreenRect);
            var cell = IconGrid.Cell;
            if (p.X < x0 || p.Y < y0) return null;
            int col = (p.X - x0) / cell.X, row = (p.Y - y0) / cell.Y;
            if (col >= cols || row >= rows) return null;
            int dx = p.X - (x0 + col * cell.X + cell.X / 2);
            if (Math.Abs(dx) > cell.X * 0.3) return null;
            int index = (row + _scrollRow) * cols + col;
            return index < _iconOrder.Count ? _iconOrder[index] : null;
        }

        Native.RECT CellRectOf(string name)
        {
            int i = _iconOrder.FindIndex(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
            var (cols, _, x0, y0) = IconGridOf(ScreenRect);
            var cell = IconGrid.Cell;
            int x = x0 + i % cols * cell.X, y = y0 + (i / cols - _scrollRow) * cell.Y;
            return new Native.RECT(x, y, x + cell.X, y + cell.Y);
        }

        #region Scrolling

        public void ScrollRows(int delta)
        {
            if (!ShowsIcons) return;
            var (cols, rows, _, _) = IconGridOf(ScreenRect);
            int totalRows = (_iconOrder.Count + cols - 1) / cols;
            int next = Math.Clamp(_scrollRow + delta, 0, Math.Max(0, totalRows - rows));
            if (next == _scrollRow) return;
            _scrollRow = next;
            _mgr.LayoutWindow(this);
        }

        // Over the icon area the mouse hook scrolls (the wheel goes to the desktop there); over the title bar,
        // edges or scroll bar the wheel reaches this window.
        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            base.OnMouseWheel(e);
            if (!IsDesktopFence || e.Handled) return;
            ScrollRows(e.Delta > 0 ? -1 : 1);
            e.Handled = true;
        }

        int _scrollTotal, _scrollVisible;

        void UpdateScrollBar(int totalRows, int visibleRows)
        {
            _scrollTotal = totalRows;
            _scrollVisible = visibleRows;
            bool show = totalRows > visibleRows && visibleRows > 0;
            ScrollLayer.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (!show) return;
            double track = ScrollLayer.ActualHeight;
            if (track <= 0) track = Math.Max(0, ActualHeight - TitleHeightDip - 12);
            ScrollThumb.Height = Math.Max(16, track * visibleRows / totalRows);
            Canvas.SetTop(ScrollThumb, (track - ScrollThumb.Height) * _scrollRow / Math.Max(1, totalRows - visibleRows));
        }

        double _thumbStartY;
        int _thumbStartRow;

        void ScrollThumb_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _thumbStartY = e.GetPosition(ScrollLayer).Y;
            _thumbStartRow = _scrollRow;
            ScrollThumb.CaptureMouse();
            e.Handled = true;
        }

        void ScrollThumb_MouseMove(object sender, MouseEventArgs e)
        {
            if (!ScrollThumb.IsMouseCaptured || _scrollTotal <= _scrollVisible) return;
            double track = Math.Max(1, ScrollLayer.ActualHeight - ScrollThumb.Height);
            double perRow = track / (_scrollTotal - _scrollVisible);
            int row = _thumbStartRow + (int)Math.Round((e.GetPosition(ScrollLayer).Y - _thumbStartY) / perRow);
            ScrollRows(row - _scrollRow);
        }

        void ScrollThumb_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            ScrollThumb.ReleaseMouseCapture();
            e.Handled = true;
        }

        #endregion

        #region Taking drops

        // While a drag passes over a normal fence, its icon area takes the mouse (and so the drop) instead of
        // letting it through to the desktop: dropping into a fence then puts the item exactly where it was
        // dropped, lights the fence up, and still reaches folders inside the fence (see the drop handlers).
        public void SetDragMode(bool on)
        {
            on &= IsDesktopFence && IsVisible;
            if (_dragMode == on) return;
            _dragMode = on;
            UpdateHitArea();
            if (on && Config.RolledUp && !_peeking)
            {
                _peeking = true;
                ApplyBounds(animate: true);
            }
            else if (!on && _peeking) _peekTimer.Start();
        }

        void UpdateHitArea()
        {
            Root.Background = !IsDesktopFence || _dragMode ? HitBrush : Brushes.Transparent;
            if (!IsDesktopFence) ScrollLayer.Visibility = Visibility.Collapsed;
        }

        void SetSpot(string? name)
        {
            Native.RECT? spot = name == null || !IsDesktopFence ? null : CellRectOf(name);
            if (Nullable.Equals(spot, _spot)) return;
            _spot = spot;
            UpdateBackdrop();
        }

        #endregion

        #region Lifting (moving and resizing)

        // Explorer moves its icons a frame or two after the fence (each move is a call into Explorer, which then
        // repaints), so they would trail behind a dragged fence. Instead, while the fence is dragged or resized,
        // this window draws the icons itself (they move with it, frame for frame) and the real ones wait off
        // screen; once it is let go they are put in their new cells.
        // The stand-ins are cheap to draw (ProxyTemplate: no fade-in, no blurred shadow) and are built ahead, as
        // soon as the mouse comes onto the title bar or an edge, so pressing it only has to show them. The real
        // icons are parked as soon as the stand-ins are on screen, and the fence starts to move only once Explorer
        // has had a moment to repaint without them (WaitForLift); should it move first anyway, they are parked
        // right then (a blink is better than icons shown twice).
        bool _lifted, _liftParked, _liftSettled;
        int _liftGeneration;
        long _liftParkedAt;
        object? _proxyKey; // what the stand-ins were built for
        System.Diagnostics.Stopwatch? _liftClock;
        readonly Dictionary<string, FileItem> _proxyItems = new(StringComparer.OrdinalIgnoreCase);

        void PrepareLift()
        {
            // Not while the stand-ins of the last lift are still up, waiting for Explorer to paint the real icons.
            if (_lifted || IconProxy.Visibility == Visibility.Visible || !ShowsIcons || _iconOrder.Count == 0) return;
            EnsureProxy();
            IconProxy.Visibility = Visibility.Hidden; // laid out, not drawn
        }

        void EnsureProxy()
        {
            UpdateCellSize();
            var r = ScreenRect;
            var key = (_iconOrder, _scrollRow, r.Width, r.Height, IconGrid.Cell.X, IconGrid.Cell.Y, LabelWidth);
            if (IconProxy.Children.Count > 0 && Equals(_proxyKey, key)) return;
            BuildProxy();
            _proxyKey = key;
        }

        // moving: the fence is dragged by its title, so its icons keep their cells and a picture of them (see
        // ScreenSnapshot) can stand in; when resizing they re-flow, so they are drawn.
        void BeginLift(bool moving = false)
        {
            if (_lifted || !ShowsIcons || _iconOrder.Count == 0) return;
            _lifted = true;
            _liftClock = System.Diagnostics.Stopwatch.StartNew();
            bool ready = IconProxy.Children.Count > 0;
            var window = ScreenRect;
            var area = IconArea(window);
            if (moving && ScreenSnapshot.Take(area) is { } picture)
            {
                IconProxy.Children.Clear();
                _proxyKey = null;
                double s = DpiScale;
                var image = new Image { Source = picture, Width = area.Width / s, Height = area.Height / s, Stretch = Stretch.Fill };
                RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
                Canvas.SetLeft(image, (area.Left - window.Left) / s);
                Canvas.SetTop(image, (area.Top - window.Top) / s);
                IconProxy.Children.Add(image);
                Log.Trace($"lift begin \"{Config.Title}\": a picture of the icons, {_liftClock.ElapsedMilliseconds} ms");
            }
            else
            {
                EnsureProxy();
                Log.Trace($"lift begin \"{Config.Title}\": {IconProxy.Children.Count} stand-ins ({(ready ? "built ahead" : "built now")}), {_liftClock.ElapsedMilliseconds} ms");
            }
            IconProxy.Visibility = Visibility.Visible;
            _liftSettled = false;
            int generation = ++_liftGeneration, frames = 0;
            EventHandler? wait = null;
            wait = (_, _) =>
            {
                if (generation != _liftGeneration || !_lifted) { CompositionTarget.Rendering -= wait; return; }
                if (++frames == 2) // drawn at the first frame, on screen by the second
                {
                    ParkLifted("stand-ins on screen");
                    DesktopIcons.PaintNow(); // returns once Explorer has painted the desktop without them
                    Log.Trace($"lift: desktop repainted at {_liftClock!.ElapsedMilliseconds} ms");
                    return;
                }
                CompositionTarget.Rendering -= wait; // that paint is on screen by now
                _liftSettled = true;
            };
            CompositionTarget.Rendering += wait;
        }

        // Lets the dispatcher run (the stand-ins get drawn, the real icons parked) until the lift has settled, for
        // at most 250 ms. Windows stops this window from drawing from the moment DragMove is called until the mouse
        // has moved, so it must not be called before. False if the button was let go meanwhile: a click.
        bool WaitForLift(UIElement pressed)
        {
            if (_lifted && !_liftSettled)
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var frame = new DispatcherFrame();
                pressed.CaptureMouse();
                MouseButtonEventHandler up = (_, _) => frame.Continue = false;
                pressed.PreviewMouseLeftButtonUp += up;
                var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(5), DispatcherPriority.Input, (_, _) =>
                {
                    if (_liftSettled || !_lifted || Mouse.LeftButton != MouseButtonState.Pressed || clock.ElapsedMilliseconds > 250)
                        frame.Continue = false;
                }, Dispatcher);
                Dispatcher.PushFrame(frame);
                timer.Stop();
                pressed.PreviewMouseLeftButtonUp -= up;
                pressed.ReleaseMouseCapture();
                Log.Trace($"lift: waited {clock.ElapsedMilliseconds} ms (settled={_liftSettled}, button {Mouse.LeftButton})");
            }
            return Mouse.LeftButton == MouseButtonState.Pressed;
        }

        // Resizing is done by hand: its steps wait the same way (they are anchored where the edge was pressed).
        bool LiftSettling => _lifted && !_liftSettled && _liftClock is { ElapsedMilliseconds: < 250 };

        void ParkLifted(string why)
        {
            if (!_lifted || _liftParked) return;
            _liftParked = true;
            _mgr.LayoutWindow(this);
            _liftParkedAt = _liftClock?.ElapsedMilliseconds ?? 0;
            Log.Trace($"lift: real icons parked ({why}) at {_liftClock?.ElapsedMilliseconds} ms");
        }

        void EndLift()
        {
            if (!_lifted) return;
            Log.Trace($"lift end \"{Config.Title}\"");
            _lifted = false;
            _liftParked = false;
            _liftSettled = false;
            UpdateBackdrop();         // painted for its new place
            _mgr.LayoutWindow(this);  // the real icons come back, in their new cells
            // Explorer paints them a moment later; the stand-ins stay until then (longer than it takes: an icon the
            // list view could not be asked to move goes through Explorer's view, which shows it about 0.3 s late).
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                if (_lifted) return;
                IconProxy.Visibility = Visibility.Collapsed;
                IconProxy.Children.Clear();
                _proxyKey = null;
            };
            timer.Start();
        }

        // Stand-ins for the icons now in view, in exactly the cells the real ones use.
        void BuildProxy()
        {
            IconProxy.Children.Clear();
            var window = ScreenRect;
            var (cols, rows, x0, y0) = IconGridOf(window);
            var cell = IconGrid.Cell;
            double s = DpiScale;
            var template = (DataTemplate)Resources["ProxyTemplate"];
            for (int i = _scrollRow * cols; i < _iconOrder.Count; i++)
            {
                int row = i / cols - _scrollRow, col = i % cols;
                if (row >= rows) break;
                var host = new ContentPresenter
                {
                    Content = ProxyItem(_iconOrder[i]),
                    ContentTemplate = template,
                    Width = CellWidth,
                    Height = CellHeight,
                };
                Canvas.SetLeft(host, (x0 + col * cell.X - window.Left) / s);
                Canvas.SetTop(host, (y0 + row * cell.Y - window.Top) / s);
                IconProxy.Children.Add(host);
            }
        }

        // What the stand-in of a member shows: the same picture and name the desktop shows.
        FileItem ProxyItem(string name)
        {
            int px = (int)Math.Round(_mgr.DesktopIconSize * DpiScale);
            if (_proxyItems.TryGetValue(name, out var item) && item.IconPx == px) return item;
            string path = DesktopShell.MemberPath(name) ?? Path.Combine(DesktopShell.DesktopPath, name);
            bool shell = path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase);
            DateTime modified = DateTime.MinValue;
            try { if (!shell) modified = File.GetLastWriteTimeUtc(path); } catch (Exception) { }
            item = new FileItem(path)
            {
                Name = shell ? VirtualItems.DisplayName(path) ?? name : ShellInfo.GetDisplayNameCached(path, modified, name),
                Modified = modified,
                IconSize = _mgr.DesktopIconSize,
                ItemWidth = LabelWidth,
            };
            _proxyItems[name] = item;
            item.IconPx = px;
            IconLoader.Request(path, px, modified.Ticks, img => Dispatcher.BeginInvoke(() => { if (item.IconPx == px) item.Icon = img; }));
            return item;
        }

        // Pictures for the stand-ins, loaded ahead in the background so a drag never waits for them.
        void WarmProxies()
        {
            foreach (var name in _proxyItems.Keys.Where(n => !_iconOrder.Contains(n, StringComparer.OrdinalIgnoreCase)).ToList())
                _proxyItems.Remove(name);
            foreach (var name in _iconOrder)
            {
                var item = ProxyItem(name);
                item.IconSize = _mgr.DesktopIconSize;
                item.ItemWidth = LabelWidth;
            }
        }

        #endregion

        #region For the test channel

        public IReadOnlyList<string> IconOrder => _iconOrder;
        public int ScrollRow => _scrollRow;
        public bool DragModeOn => _dragMode;

        public double BenchBackdrop(int frames) => _backdrop?.Bench(frames) ?? 0;

        public int ProxyCount => IconProxy.Visibility == Visibility.Visible ? IconProxy.Children.Count : 0;

        public void TestLift(string what)
        {
            if (what == "ready") PrepareLift();
            else if (what == "on") BeginLift();
            else if (what == "move") BeginLift(moving: true);
            else EndLift();
        }

        public void TestPeek(bool on)
        {
            _peeking = on;
            ApplyBounds();
        }

        // Saves what this window and its backdrop draw (our own pixels only) as PNG files.
        public void DumpVisuals(string folder)
        {
            Directory.CreateDirectory(folder);
            string name = string.Concat(Config.Title.Where(char.IsLetterOrDigit));
            double s = DpiScale;
            var target = new System.Windows.Media.Imaging.RenderTargetBitmap(
                Math.Max(1, (int)Math.Round(Root.ActualWidth * s)), Math.Max(1, (int)Math.Round(Root.ActualHeight * s)),
                96 * s, 96 * s, PixelFormats.Pbgra32);
            target.Render(Root);
            var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
            png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(target));
            using (var file = File.Create(Path.Combine(folder, name + "-window.png"))) png.Save(file);
            _backdrop?.Dump(Path.Combine(folder, name + "-backdrop.png"));
        }

        #endregion
    }
}
