using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Desko.Core;
using Desko.Interop;
using Desko.Models;

namespace Desko.UI
{
    // The part of a fence that lies under the desktop icons: a child window of Explorer's desktop, placed between
    // the icon layer and the wallpaper. Windows cannot blur there, so it paints the wallpaper itself (see
    // Wallpaper) with the fence tint on top. On Windows 11 24H2+ only plain layered windows show up in that
    // layer, so this is a bare Win32 window that is handed a finished bitmap.
    // Every backdrop lives on one thread of its own: a child of another process's window shares input with
    // that process, and Explorer must never have to wait for Desko's busy UI thread.
    internal sealed class FenceBackdrop
    {
        // Noise: the fine grain of Windows' Acrylic material ("Acrylic" backdrop); Blur: the wallpaper blurred
        // ("Acrylic" and "Blur") or sharp ("Clear").
        public sealed record Look(Color Tint, Color TitleTint, Color Border, Color Accent, int TitlePx, int Radius,
                                  bool Blur, bool Noise, bool Glow, Native.RECT? Spot);

        static readonly Brush Grain = MakeGrain();

        // A tile of faint random speckles, like the noise layer of Windows' Acrylic.
        static Brush MakeGrain()
        {
            const int size = 96;
            var random = new Random(1234);
            var pixels = new byte[size * size * 4];
            for (int i = 0; i < size * size; i++)
            {
                byte alpha = (byte)random.Next(0, 18);           // a few percent at most
                byte value = (byte)(random.Next(2) == 0 ? 0 : alpha); // premultiplied: black or white speckle
                pixels[i * 4] = pixels[i * 4 + 1] = pixels[i * 4 + 2] = value;
                pixels[i * 4 + 3] = alpha;
            }
            var tile = BitmapSource.Create(size, size, 96, 96, PixelFormats.Pbgra32, null, pixels, size * 4);
            tile.Freeze();
            var brush = new ImageBrush(tile)
            {
                TileMode = TileMode.Tile,
                Viewport = new Rect(0, 0, size, size),
                ViewportUnits = BrushMappingMode.Absolute,
                Stretch = Stretch.None,
            };
            brush.Freeze();
            return brush;
        }

        static Dispatcher? _dispatcher;
        static readonly List<FenceBackdrop> All = new();
        static readonly WndProcDelegate Proc = WndProc;
        static ushort _class;
        static IntPtr _host, _insertAfter;

        readonly object _gate = new();
        Native.RECT _rect;
        Look? _look;
        bool _visible = true, _queued;
        byte _alpha = 255;

        // Owned by the backdrop thread.
        IntPtr _hwnd;
        int _width, _height;
        byte[]? _pixels;
        Native.RECT _shownRect;
        Look? _shownLook;

        public FenceBackdrop() => Post(() => All.Add(this));

        // New position and/or look; the latest one wins if several arrive before the thread gets to them.
        public void Update(Native.RECT screen, Look look)
        {
            lock (_gate)
            {
                _rect = screen;
                _look = look;
                if (_queued) return;
                _queued = true;
            }
            Post(Apply, DispatcherPriority.Render);
        }

        // Moves the window at once, from the calling thread, keeping what it shows (a fence being dragged: the
        // blurred wallpaper barely changes over a few pixels). The next Update paints it for its new place.
        public void MoveTo(Native.RECT screen)
        {
            IntPtr hwnd = Volatile.Read(ref _hwnd), host = _host;
            if (hwnd == IntPtr.Zero || host == IntPtr.Zero) return;
            lock (_gate) _rect = screen;
            var origin = new Native.POINT(0, 0);
            ClientToScreen(host, ref origin);
            Native.SetWindowPos(hwnd, IntPtr.Zero, screen.Left - origin.X, screen.Top - origin.Y, 0, 0,
                Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        }

        public void SetVisible(bool visible)
        {
            lock (_gate) _visible = visible;
            Post(() =>
            {
                if (_hwnd != IntPtr.Zero) ShowWindow(_hwnd, visible && _width > 0 ? SW_SHOWNA : SW_HIDE);
            });
        }

        // Fades together with the fence's title bar (0-255).
        public void SetAlpha(byte alpha)
        {
            lock (_gate) _alpha = alpha;
            Post(() => { if (_hwnd != IntPtr.Zero) SetLayeredWindowAttributes(_hwnd, 0, alpha, LWA_ALPHA); });
        }

        // Test channel: average time to paint this backdrop at its current place (ms).
        public double Bench(int frames) => Dispatcher.Invoke(() =>
        {
            if (_shownLook == null) return 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < frames; i++)
            {
                Render(_shownRect, _shownLook);
                Paint();
            }
            return sw.Elapsed.TotalMilliseconds / frames;
        });

        // Test channel: what this backdrop currently shows, as a PNG file.
        public void Dump(string path) => Post(() =>
        {
            if (_pixels == null) return;
            var image = BitmapSource.Create(_width, _height, 96, 96, PixelFormats.Pbgra32, null, _pixels, _width * 4);
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(image));
            using var file = System.IO.File.Create(path);
            png.Save(file);
        });

        public void Dispose() => Post(() =>
        {
            All.Remove(this);
            if (_hwnd != IntPtr.Zero) DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
            _pixels = null;
            _target = null;
        });

        // Explorer restarted (its desktop windows, and our children of them, are gone) or the wallpaper changed.
        public static void RecreateAll() => Post(() =>
        {
            _host = IntPtr.Zero;
            foreach (var b in All)
            {
                if (b._hwnd != IntPtr.Zero && IsWindow(b._hwnd)) DestroyWindow(b._hwnd);
                b._hwnd = IntPtr.Zero;
                b._shownLook = null;
                b.Apply();
            }
        });

        public static void InvalidateWallpaper() => Post(() =>
        {
            Wallpaper.Invalidate();
            foreach (var b in All)
            {
                b._shownLook = null;
                b.Apply();
            }
        });

        public static void CheckWallpaper() => Post(() =>
        {
            if (!Wallpaper.CheckForChange()) return;
            foreach (var b in All)
            {
                b._shownLook = null;
                b.Apply();
            }
        });

        static void Post(Action action, DispatcherPriority priority = DispatcherPriority.Normal)
        {
            var d = Dispatcher;
            d.BeginInvoke(priority, () =>
            {
                try { action(); }
                catch (Exception ex) { Log.Error(ex); }
            });
        }

        static Dispatcher Dispatcher
        {
            get
            {
                if (_dispatcher != null) return _dispatcher;
                using var ready = new ManualResetEventSlim();
                var thread = new Thread(() =>
                {
                    _dispatcher = Dispatcher.CurrentDispatcher;
                    ready.Set();
                    Dispatcher.Run();
                })
                { IsBackground = true, Name = "Desko backdrops" };
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
                ready.Wait();
                return _dispatcher!;
            }
        }

        void Apply()
        {
            Native.RECT rect;
            Look? look;
            bool visible;
            byte alpha;
            lock (_gate)
            {
                _queued = false;
                rect = _rect;
                look = _look;
                visible = _visible;
                alpha = _alpha;
            }
            if (look == null || rect.Width <= 0 || rect.Height <= 0) return;

            if (_hwnd == IntPtr.Zero || !IsWindow(_hwnd))
            {
                _hwnd = Create(alpha);
                _width = _height = 0;
                _shownLook = null;
                if (_hwnd == IntPtr.Zero) return;
            }

            bool resized = rect.Width != _width || rect.Height != _height;
            bool moved = resized || rect.Left != _shownRect.Left || rect.Top != _shownRect.Top;
            // The wallpaper under it changes with every move, so moving means painting again too.
            if (moved || !Equals(look, _shownLook)) Render(rect, look);

            var origin = new Native.POINT(0, 0);
            ClientToScreen(_host, ref origin);
            uint flags = Native.SWP_NOACTIVATE | (visible ? Native.SWP_SHOWWINDOW : 0);
            Native.SetWindowPos(_hwnd, _insertAfter, rect.Left - origin.X, rect.Top - origin.Y, rect.Width, rect.Height, flags);
            if (resized || look.Radius != _shownLook?.Radius)
            {
                int d = look.Radius * 2;
                SetWindowRgn(_hwnd, CreateRoundRectRgn(0, 0, rect.Width + 1, rect.Height + 1, d, d), true);
            }
            _width = rect.Width;
            _height = rect.Height;
            _shownRect = rect;
            _shownLook = look;
            Paint();
        }

        void Render(Native.RECT rect, Look look)
        {
            int w = rect.Width, h = rect.Height;
            var full = new Rect(0, 0, w, h);
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                Wallpaper.Draw(dc, rect, look.Blur);
                dc.DrawRectangle(Brush(look.Tint), null, full);
                if (look.Noise) dc.DrawRectangle(Grain, null, full);
                if (look.TitlePx > 0) dc.DrawRectangle(Brush(look.TitleTint), null, new Rect(0, 0, w, Math.Min(h, look.TitlePx)));
                if (look.Glow) dc.DrawRectangle(Brush(Theme.WithAlpha(look.Accent, 0x2A)), null, full);
                if (look.Spot is Native.RECT s)
                {
                    var spot = new Rect(s.Left - rect.Left, s.Top - rect.Top, s.Width, s.Height);
                    dc.DrawRoundedRectangle(Brush(Theme.WithAlpha(look.Accent, 0x66)), Pen(look.Accent, 1),
                        new Rect(spot.X + 0.5, spot.Y + 0.5, spot.Width - 1, spot.Height - 1), 4, 4);
                }
                // Inner border; with the glow, an accent frame in its place.
                double t = look.Glow ? 2 : 1;
                dc.DrawRoundedRectangle(null, Pen(look.Glow ? look.Accent : look.Border, t),
                    new Rect(t / 2, t / 2, w - t, h - t), look.Radius, look.Radius);
            }
            // Reused while the size stays the same: a fence being dragged repaints every frame.
            if (_target == null || _target.PixelWidth != w || _target.PixelHeight != h)
                _target = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            else _target.Clear();
            _target.Render(visual);
            if (_pixels == null || _pixels.Length != w * h * 4) _pixels = new byte[w * h * 4];
            _target.CopyPixels(_pixels, w * 4, 0);
        }

        RenderTargetBitmap? _target;

        void Paint()
        {
            if (_pixels == null || _hwnd == IntPtr.Zero) return;
            IntPtr dc = GetDC(_hwnd);
            try { Blit(dc); }
            finally { ReleaseDC(_hwnd, dc); }
        }

        void Blit(IntPtr dc)
        {
            if (_pixels == null) return;
            var header = new BITMAPINFOHEADER
            {
                Size = Marshal.SizeOf<BITMAPINFOHEADER>(),
                Width = _width,
                Height = -_height, // top-down
                Planes = 1,
                BitCount = 32,
            };
            SetDIBitsToDevice(dc, 0, 0, (uint)_width, (uint)_height, 0, 0, 0, (uint)_height, _pixels, ref header, 0);
        }

        static Brush Brush(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        static Pen Pen(Color c, double thickness)
        {
            var p = new Pen(Brush(c), thickness);
            p.Freeze();
            return p;
        }

        #region Window

        IntPtr Create(byte alpha)
        {
            if (_host == IntPtr.Zero || !IsWindow(_host)) FindHost();
            if (_host == IntPtr.Zero) return IntPtr.Zero;
            if (_class == 0)
            {
                var wc = new WNDCLASSEX
                {
                    cbSize = Marshal.SizeOf<WNDCLASSEX>(),
                    lpfnWndProc = Marshal.GetFunctionPointerForDelegate(Proc),
                    hInstance = Native.GetModuleHandle(null),
                    lpszClassName = "DeskoFenceBackdrop",
                };
                _class = RegisterClassEx(ref wc);
                if (_class == 0) return IntPtr.Zero;
            }
            var hwnd = CreateWindowEx(WS_EX_LAYERED | WS_EX_NOACTIVATE | (int)Native.WS_EX_TOOLWINDOW,
                "DeskoFenceBackdrop", "", WS_CHILD | WS_CLIPSIBLINGS, 0, 0, 1, 1, _host, IntPtr.Zero, Native.GetModuleHandle(null), IntPtr.Zero);
            if (hwnd != IntPtr.Zero) SetLayeredWindowAttributes(hwnd, 0, alpha, LWA_ALPHA);
            return hwnd;
        }

        // Where the layer between the icons and the wallpaper is.
        //  - Windows 11 24H2+: the icons (SHELLDLL_DefView) are a child of Progman, the wallpaper a WorkerW below
        //    them: our windows go into Progman right under the DefView.
        //  - Earlier: asking Progman to split off a WorkerW (0x052C, as live-wallpaper apps do) puts the icons in
        //    one top-level WorkerW and the wallpaper in the next one down: our windows go into that one.
        static void FindHost()
        {
            _host = _insertAfter = IntPtr.Zero;
            var progman = DesktopShell.Progman;
            if (progman == IntPtr.Zero) return;
            SendMessageTimeout(progman, 0x052C, new IntPtr(0xD), new IntPtr(1), 0, 1000, out _);

            var defView = Native.FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (defView != IntPtr.Zero)
            {
                _host = progman;
                _insertAfter = defView;
                return;
            }
            IntPtr iconsWorker = IntPtr.Zero;
            Native.EnumWindows((h, _) =>
            {
                if (Native.ClassOf(h) == "WorkerW" && Native.FindWindowEx(h, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero)
                {
                    iconsWorker = h;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            var below = iconsWorker == IntPtr.Zero ? IntPtr.Zero : GetWindow(iconsWorker, GW_HWNDNEXT);
            _host = below != IntPtr.Zero && Native.ClassOf(below) == "WorkerW" ? below : progman;
            _insertAfter = Native.HWND_TOP;
        }

        static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam)
        {
            switch (msg)
            {
                case WM_NCHITTEST:
                    return new IntPtr(HTTRANSPARENT);
                case WM_ERASEBKGND:
                    return new IntPtr(1);
                case WM_PAINT:
                {
                    var ps = new PAINTSTRUCT();
                    IntPtr dc = BeginPaint(hwnd, ref ps);
                    foreach (var b in All)
                        if (b._hwnd == hwnd) { b.Blit(dc); break; }
                    EndPaint(hwnd, ref ps);
                    return IntPtr.Zero;
                }
            }
            return DefWindowProc(hwnd, msg, wParam, lParam);
        }

        #endregion

        #region Win32

        delegate IntPtr WndProcDelegate(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

        const int WS_CHILD = 0x40000000, WS_CLIPSIBLINGS = 0x04000000;
        const int WS_EX_LAYERED = 0x00080000, WS_EX_NOACTIVATE = 0x08000000;
        const int WM_PAINT = 0x000F, WM_ERASEBKGND = 0x0014, WM_NCHITTEST = 0x0084, HTTRANSPARENT = -1;
        const int SW_HIDE = 0, SW_SHOWNA = 8;
        const uint LWA_ALPHA = 0x2, GW_HWNDNEXT = 2;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WNDCLASSEX
        {
            public int cbSize;
            public int style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra, cbWndExtra;
            public IntPtr hInstance, hIcon, hCursor, hbrBackground;
            public string? lpszMenuName;
            public string lpszClassName;
            public IntPtr hIconSm;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct PAINTSTRUCT
        {
            public IntPtr hdc;
            public bool fErase;
            public Native.RECT rcPaint;
            public bool fRestore, fIncUpdate;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] rgbReserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct BITMAPINFOHEADER
        {
            public int Size, Width, Height;
            public short Planes, BitCount;
            public int Compression, SizeImage, XPelsPerMeter, YPelsPerMeter, ClrUsed, ClrImportant;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr CreateWindowEx(int exStyle, string cls, string name, int style, int x, int y, int w, int h,
                                            IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
        [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd, int cmd);
        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
        [DllImport("user32.dll")] static extern IntPtr DefWindowProc(IntPtr hwnd, int msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint key, byte alpha, uint flags);
        [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr hwnd, ref Native.POINT pt);
        [DllImport("user32.dll")] static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);
        [DllImport("gdi32.dll")] static extern IntPtr CreateRoundRectRgn(int l, int t, int r, int b, int w, int h);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
        [DllImport("user32.dll")] static extern IntPtr BeginPaint(IntPtr hwnd, ref PAINTSTRUCT ps);
        [DllImport("user32.dll")] static extern bool EndPaint(IntPtr hwnd, ref PAINTSTRUCT ps);
        [DllImport("gdi32.dll")]
        static extern int SetDIBitsToDevice(IntPtr dc, int x, int y, uint w, uint h, int srcX, int srcY, uint startScan, uint lines,
                                            byte[] bits, ref BITMAPINFOHEADER header, uint usage);
        [DllImport("user32.dll")]
        static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);

        #endregion
    }
}
