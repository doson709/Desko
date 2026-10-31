using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Desko.Interop;
using Desko.Models;

namespace Desko.UI
{
    // The wallpaper as Windows draws it, one image per monitor. Fences live under the desktop icons, where
    // Windows offers no blur, so they paint the wallpaper beneath them themselves. Blurred copies are kept at
    // a quarter of the resolution (a blurred image loses nothing when scaled back up): about 0.5 MB a monitor.
    // Only used from the backdrop thread (see FenceBackdrop).
    internal static class Wallpaper
    {
        [ComImport, Guid("B92B56A9-8B55-4E14-9A89-0199BBB6F93B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IDesktopWallpaper
        {
            void SetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string? monitorId, [MarshalAs(UnmanagedType.LPWStr)] string wallpaper);
            [return: MarshalAs(UnmanagedType.LPWStr)] string GetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string? monitorId);
            [return: MarshalAs(UnmanagedType.LPWStr)] string GetMonitorDevicePathAt(uint index);
            uint GetMonitorDevicePathCount();
            Native.RECT GetMonitorRECT([MarshalAs(UnmanagedType.LPWStr)] string monitorId);
            void SetBackgroundColor(uint color);
            uint GetBackgroundColor();
            void SetPosition(int position);
            int GetPosition();
        }

        const int PosCenter = 0, PosTile = 1, PosStretch = 2, PosFit = 3, PosFill = 4, PosSpan = 5;
        const int Downscale = 4;
        const double BlurRadiusPx = 34; // at full resolution

        sealed record Screen(Native.RECT Rect, string Image, BitmapSource? Blurred, BitmapSource? Sharp);

        static List<Screen>? _screens;
        static Color _background = Colors.Black;
        static bool _sharpWanted;
        static string _signature = "";

        // Called on wallpaper / display changes; everything is rebuilt on the next draw.
        public static void Invalidate() => _screens = null;

        // Slideshows change the picture without telling anyone; a cheap comparison catches that.
        public static bool CheckForChange()
        {
            if (_screens == null || Signature() == _signature) return false;
            Invalidate();
            return true;
        }

        // Paints the wallpaper that lies under `area` (screen pixels), drawing in pixel units with (0,0) at area's corner.
        public static void Draw(DrawingContext dc, Native.RECT area, bool blurred)
        {
            if (!blurred && !_sharpWanted)
            {
                _sharpWanted = true;
                _screens = null; // build the full-resolution copies from now on
            }
            var screens = _screens ??= Build();
            dc.DrawRectangle(Frozen(new SolidColorBrush(_background)), null, new Rect(0, 0, area.Width, area.Height));
            foreach (var s in screens)
            {
                var image = blurred ? s.Blurred : s.Sharp ?? s.Blurred;
                if (image == null) continue;
                dc.DrawImage(image, new Rect(s.Rect.Left - area.Left, s.Rect.Top - area.Top, s.Rect.Width, s.Rect.Height));
            }
        }

        static string Signature()
        {
            try
            {
                var (monitors, _, _) = Query();
                return string.Join("|", monitors.Select(m => $"{m.Image}@{Stamp(m.Image)}"));
            }
            catch (Exception) { return ""; }
        }

        static string Stamp(string path)
        {
            try { return File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks.ToString() : ""; }
            catch (Exception) { return ""; }
        }

        static (List<(Native.RECT Rect, string Image)> Monitors, int Position, Color Background) Query()
        {
            var monitors = new List<(Native.RECT, string)>();
            int position = PosFill;
            var background = Colors.Black;
            try
            {
                var type = Type.GetTypeFromCLSID(new Guid("C2CF3110-460E-4FC1-B9D0-8A1C0C9CC4BD"));
                if (type != null && Activator.CreateInstance(type) is IDesktopWallpaper wp)
                {
                    try
                    {
                        position = wp.GetPosition();
                        uint c = wp.GetBackgroundColor();
                        background = Color.FromRgb((byte)c, (byte)(c >> 8), (byte)(c >> 16));
                        uint count = wp.GetMonitorDevicePathCount();
                        for (uint i = 0; i < count; i++)
                        {
                            string id = wp.GetMonitorDevicePathAt(i);
                            var rect = wp.GetMonitorRECT(id);
                            if (rect.Width <= 0 || rect.Height <= 0) continue; // monitor detached
                            string image = "";
                            try { image = wp.GetWallpaper(id) ?? ""; } catch (COMException) { }
                            monitors.Add((rect, image));
                        }
                    }
                    finally { Marshal.ReleaseComObject(wp); }
                }
            }
            catch (Exception ex) { Log.Error(ex); }

            // The picture Windows actually shows is also cached here; used when the original is gone or unknown
            // (slideshows, Spotlight, a file deleted after it was set).
            string themes = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Themes");
            for (int i = 0; i < monitors.Count; i++)
            {
                if (File.Exists(monitors[i].Item2)) continue;
                string perMonitor = Path.Combine(themes, $"Transcoded_{i:000}");
                string shared = Path.Combine(themes, "TranscodedWallpaper");
                monitors[i] = (monitors[i].Item1, File.Exists(perMonitor) ? perMonitor : File.Exists(shared) ? shared : "");
            }
            if (monitors.Count == 0)
            {
                // No IDesktopWallpaper: one entry per monitor from Win32.
                foreach (var r in Native.MonitorRects())
                    monitors.Add((r, Path.Combine(themes, "TranscodedWallpaper")));
            }
            return (monitors, position, background);
        }

        static List<Screen> Build()
        {
            var result = new List<Screen>();
            try
            {
                var (monitors, position, background) = Query();
                _background = background;
                _signature = string.Join("|", monitors.Select(m => $"{m.Image}@{Stamp(m.Image)}"));

                // "Span" stretches one picture over the bounding box of every monitor.
                var span = monitors.Count == 0 ? default
                    : new Native.RECT(monitors.Min(m => m.Rect.Left), monitors.Min(m => m.Rect.Top),
                                      monitors.Max(m => m.Rect.Right), monitors.Max(m => m.Rect.Bottom));
                foreach (var (rect, image) in monitors)
                {
                    // Decoded no larger than it is shown: a 4K picture would otherwise cost ~36 MB for a moment.
                    var box = position == PosSpan ? span : rect;
                    var picture = Load(image, position, box, _sharpWanted ? 1 : 2);
                    var blurred = Render(rect, picture, position, background, span, Downscale, BlurRadiusPx / Downscale);
                    var sharp = _sharpWanted ? Render(rect, picture, position, background, span, 1, 0) : null;
                    result.Add(new Screen(rect, image, blurred, sharp));
                }
            }
            catch (Exception ex) { Log.Error(ex); }
            return result;
        }

        // The picture at the size it covers `box` with (divided by `reduce`); "Center" and "Tile" show it at its own
        // size, so those keep every pixel.
        static BitmapSource? Load(string path, int position, Native.RECT box, int reduce)
        {
            if (path.Length == 0 || !File.Exists(path)) return null;
            try
            {
                byte[] bytes = File.ReadAllBytes(path);
                int width = 0;
                if (position is not (PosCenter or PosTile))
                {
                    var frame = BitmapDecoder.Create(new MemoryStream(bytes), BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
                    double iw = frame.PixelWidth, ih = frame.PixelHeight;
                    double f = position switch
                    {
                        PosFit => Math.Min(box.Width / iw, box.Height / ih),
                        PosStretch => box.Width / iw,
                        _ => Math.Max(box.Width / iw, box.Height / ih),
                    };
                    int shown = (int)Math.Ceiling(iw * f / reduce);
                    if (shown > 0 && shown < iw) width = shown;
                }
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                if (width > 0) image.DecodePixelWidth = width;
                image.StreamSource = new MemoryStream(bytes);
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch (Exception ex)
            {
                Log.Error(ex);
                return null;
            }
        }

        // The monitor's part of the desktop background, at 1/scale resolution, optionally blurred.
        static BitmapSource? Render(Native.RECT monitor, BitmapSource? picture, int position, Color background,
                                    Native.RECT span, int scale, double blur)
        {
            int w = Math.Max(1, monitor.Width / scale), h = Math.Max(1, monitor.Height / scale);
            // Blur pulls in transparent pixels at the edges; drawing a margin of picture around fixes the dark rim.
            double margin = blur * 3;
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(Frozen(new SolidColorBrush(background)), null, new Rect(-margin, -margin, w + 2 * margin, h + 2 * margin));
                if (picture != null)
                {
                    double iw = picture.PixelWidth, ih = picture.PixelHeight;
                    var box = position == PosSpan ? span : monitor;
                    double bw = box.Width / (double)scale, bh = box.Height / (double)scale;
                    double bx = (box.Left - monitor.Left) / (double)scale, by = (box.Top - monitor.Top) / (double)scale;
                    Rect dest;
                    switch (position)
                    {
                        case PosStretch:
                            dest = new Rect(bx, by, bw, bh);
                            break;
                        case PosFit:
                        {
                            double f = Math.Min(bw / iw, bh / ih);
                            dest = new Rect(bx + (bw - iw * f) / 2, by + (bh - ih * f) / 2, iw * f, ih * f);
                            break;
                        }
                        case PosCenter:
                            dest = new Rect(bx + (bw - iw / scale) / 2, by + (bh - ih / scale) / 2, iw / scale, ih / scale);
                            break;
                        case PosTile:
                        {
                            var brush = new ImageBrush(picture)
                            {
                                TileMode = TileMode.Tile,
                                Viewport = new Rect(0, 0, iw / scale, ih / scale),
                                ViewportUnits = BrushMappingMode.Absolute,
                                Stretch = Stretch.Fill,
                            };
                            brush.Freeze();
                            dc.DrawRectangle(brush, null, new Rect(-margin, -margin, w + 2 * margin, h + 2 * margin));
                            dest = Rect.Empty;
                            break;
                        }
                        default: // Fill, Span
                        {
                            double f = Math.Max(bw / iw, bh / ih);
                            dest = new Rect(bx + (bw - iw * f) / 2, by + (bh - ih * f) / 2, iw * f, ih * f);
                            break;
                        }
                    }
                    if (!dest.IsEmpty)
                    {
                        // Stretch the picture a little past the monitor edges so the blur has real pixels to use.
                        if (blur > 0 && position is PosFill or PosSpan or PosStretch)
                            dest.Inflate(margin, margin);
                        dc.DrawImage(picture, dest);
                    }
                }
            }
            RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);

            var source = new VisualHost(visual);
            if (blur > 0) source.Effect = new BlurEffect { Radius = blur, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Quality };
            source.Measure(new Size(w, h));
            source.Arrange(new Rect(0, 0, w, h));
            var target = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            target.Render(source);
            target.Freeze();
            return target;
        }

        static T Frozen<T>(T freezable) where T : Freezable
        {
            freezable.Freeze();
            return freezable;
        }

        // Lets a DrawingVisual take part in layout so an Effect can be applied to it.
        sealed class VisualHost : FrameworkElement
        {
            readonly Visual _child;
            public VisualHost(Visual child)
            {
                _child = child;
                AddVisualChild(child);
            }
            protected override int VisualChildrenCount => 1;
            protected override Visual GetVisualChild(int index) => _child;
        }
    }
}
