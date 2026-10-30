using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Desko.Models;

namespace Desko.Shell
{
    internal static class ShellInfo
    {
        const uint SHGFI_DISPLAYNAME = 0x200;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SHGetFileInfo(string path, uint attributes, ref SHFILEINFO info, uint size, uint flags);

        static readonly Dictionary<string, (DateTime Stamp, string Name)> NameCache = new(StringComparer.OrdinalIgnoreCase);

        // Fences refresh often; asking the shell for every item's name each time is the slow part.
        public static string GetDisplayNameCached(string path, DateTime stamp, string fallback)
        {
            if (NameCache.TryGetValue(path, out var hit) && hit.Stamp == stamp) return hit.Name;
            string name = GetDisplayName(path) ?? fallback;
            NameCache[path] = (stamp, name);
            return name;
        }

        // The name exactly as Explorer shows it (hidden extensions, localized names, no ".lnk").
        public static string? GetDisplayName(string path)
        {
            var info = new SHFILEINFO();
            var ok = SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_DISPLAYNAME);
            return ok == IntPtr.Zero || string.IsNullOrEmpty(info.szDisplayName) ? null : info.szDisplayName;
        }
    }

    internal static class ShellIcons
    {
        [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellItemImageFactory
        {
            [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr hbitmap);
        }

        [StructLayout(LayoutKind.Sequential)]
        struct SIZE { public int cx, cy; }

        [StructLayout(LayoutKind.Sequential)]
        struct BITMAP
        {
            public int bmType, bmWidth, bmHeight, bmWidthBytes;
            public ushort bmPlanes, bmBitsPixel;
            public IntPtr bmBits;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct BITMAPINFOHEADER
        {
            public uint biSize;
            public int biWidth, biHeight;
            public ushort biPlanes, biBitCount;
            public uint biCompression, biSizeImage;
            public int biXPelsPerMeter, biYPelsPerMeter;
            public uint biClrUsed, biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct DIBSECTION
        {
            public BITMAP dsBm;
            public BITMAPINFOHEADER dsBmih;
            public uint dsBitfields0, dsBitfields1, dsBitfields2;
            public IntPtr dshSection;
            public uint dsOffset;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern int SHCreateItemFromParsingName(string path, IntPtr bindCtx, ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory? factory);

        [DllImport("gdi32.dll", EntryPoint = "GetObjectW")]
        static extern int GetObject(IntPtr h, int size, ref DIBSECTION ds);

        [DllImport("gdi32.dll")]
        static extern bool DeleteObject(IntPtr h);

        // Same image Explorer uses on the desktop: thumbnails for pictures, icons for the rest.
        public static BitmapSource? Load(string path, int size)
        {
            IntPtr hbmp = IntPtr.Zero;
            try
            {
                var iid = typeof(IShellItemImageFactory).GUID;
                if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var factory) != 0 || factory == null)
                    return null;
                int hr;
                try { hr = factory.GetImage(new SIZE { cx = size, cy = size }, 0, out hbmp); }
                finally { Marshal.ReleaseComObject(factory); }
                return hr == 0 && hbmp != IntPtr.Zero ? ToBitmapSource(hbmp) : null;
            }
            catch (Exception ex)
            {
                Log.Error(ex);
                return null;
            }
            finally
            {
                if (hbmp != IntPtr.Zero) DeleteObject(hbmp);
            }
        }

        // Imaging.CreateBitmapSourceFromHBitmap drops the alpha channel, so copy the DIB bits by hand.
        static BitmapSource ToBitmapSource(IntPtr hbmp)
        {
            var ds = new DIBSECTION();
            if (GetObject(hbmp, Marshal.SizeOf<DIBSECTION>(), ref ds) == 0 || ds.dsBm.bmBits == IntPtr.Zero || ds.dsBm.bmBitsPixel != 32)
            {
                var fallback = Imaging.CreateBitmapSourceFromHBitmap(hbmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                fallback.Freeze();
                return fallback;
            }

            int w = ds.dsBm.bmWidth, h = ds.dsBm.bmHeight, stride = ds.dsBm.bmWidthBytes;
            bool bottomUp = ds.dsBmih.biHeight > 0;
            var buffer = new byte[stride * h];
            for (int y = 0; y < h; y++)
            {
                int src = bottomUp ? h - 1 - y : y;
                Marshal.Copy(ds.dsBm.bmBits + src * stride, buffer, y * stride, stride);
            }

            bool hasAlpha = false;
            for (int i = 3; i < buffer.Length; i += 4)
                if (buffer[i] != 0) { hasAlpha = true; break; }
            if (!hasAlpha)
                for (int i = 3; i < buffer.Length; i += 4) buffer[i] = 255;

            var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, buffer, stride);
            bmp.Freeze();
            return bmp;
        }
    }

    // Icons come from, in order: memory, the on-disk cache (read in parallel on the thread pool, so a
    // fence's icons appear together with it at startup), and finally the shell itself. Thumbnail
    // extraction can be slow (video files, network shares), so that last step runs on its own STA thread.
    internal static class IconLoader
    {
        sealed record Job(string Path, int Size, string Key, Action<BitmapSource?> Done);

        static readonly BlockingCollection<Job> Queue = new();
        static readonly ConcurrentDictionary<string, BitmapSource> Cache = new(StringComparer.OrdinalIgnoreCase);
        // Latest cache key per file and size, so an edited file's old icon is dropped instead of piling up.
        static readonly ConcurrentDictionary<string, string> LatestKey = new(StringComparer.OrdinalIgnoreCase);
        static readonly object Gate = new();
        static Thread? _worker;

        public static void Request(string path, int size, long stamp, Action<BitmapSource?> done)
        {
            string key = $"{path}|{size}|{stamp}";
            if (Cache.TryGetValue(key, out var hit)) { done(hit); return; }
            ThreadPool.QueueUserWorkItem(_ =>
            {
                if (IconDiskCache.TryLoad(key) is BitmapSource cached)
                {
                    Remember(path, size, key, cached);
                    try { done(cached); } catch (Exception ex) { Log.Error(ex); }
                    return;
                }
                EnsureWorker();
                Queue.Add(new Job(path, size, key, done));
            });
        }

        static void Remember(string path, int size, string key, BitmapSource img)
        {
            Cache[key] = img;
            string slot = $"{path}|{size}";
            if (LatestKey.TryGetValue(slot, out var previous) && previous != key) Cache.TryRemove(previous, out _);
            LatestKey[slot] = key;
        }

        static void EnsureWorker()
        {
            lock (Gate)
            {
                if (_worker != null) return;
                _worker = new Thread(Run) { IsBackground = true, Name = "Desko icons" };
                _worker.SetApartmentState(ApartmentState.STA);
                _worker.Start();
            }
        }

        static void Run()
        {
            foreach (var job in Queue.GetConsumingEnumerable())
            {
                if (!Cache.TryGetValue(job.Key, out var img))
                {
                    img = ShellIcons.Load(job.Path, job.Size);
                    if (img != null)
                    {
                        Remember(job.Path, job.Size, job.Key, img);
                        var key = job.Key;
                        var save = img;
                        ThreadPool.QueueUserWorkItem(_ => IconDiskCache.Save(key, save));
                    }
                }
                try { job.Done(img); } catch (Exception ex) { Log.Error(ex); }
            }
        }
    }

    // Rendered icons kept as raw premultiplied BGRA (no image codec on the way back in).
    // Layout: "DFI1", width, height, stride (int32 each), then the pixels.
    internal static class IconDiskCache
    {
        const int Magic = 0x31494644; // "DFI1"
        static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);   // re-ask the shell now and then (app updates change icons)
        static readonly TimeSpan PurgeAge = TimeSpan.FromDays(30); // entries nobody asked for in a month are deleted

        static readonly string Dir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Desko", "iconcache");

        static string FileFor(string key)
        {
            var hash = System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(key.ToLowerInvariant()));
            return System.IO.Path.Combine(Dir, Convert.ToHexString(hash) + ".bin");
        }

        public static BitmapSource? TryLoad(string key)
        {
            try
            {
                var file = new System.IO.FileInfo(FileFor(key));
                if (!file.Exists || DateTime.UtcNow - file.LastWriteTimeUtc > MaxAge) return null;
                byte[] data = System.IO.File.ReadAllBytes(file.FullName);
                if (data.Length < 16 || BitConverter.ToInt32(data, 0) != Magic) return null;
                int w = BitConverter.ToInt32(data, 4), h = BitConverter.ToInt32(data, 8), stride = BitConverter.ToInt32(data, 12);
                if (w <= 0 || h <= 0 || stride < w * 4 || data.Length < 16 + stride * h) return null;
                var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, data[16..(16 + stride * h)], stride);
                bmp.Freeze();
                return bmp;
            }
            catch (Exception) { return null; }
        }

        public static void Save(string key, BitmapSource bmp)
        {
            try
            {
                BitmapSource src = bmp.Format == PixelFormats.Pbgra32 ? bmp : new FormatConvertedBitmap(bmp, PixelFormats.Pbgra32, null, 0);
                int w = src.PixelWidth, h = src.PixelHeight, stride = w * 4;
                var data = new byte[16 + stride * h];
                BitConverter.TryWriteBytes(data.AsSpan(0), Magic);
                BitConverter.TryWriteBytes(data.AsSpan(4), w);
                BitConverter.TryWriteBytes(data.AsSpan(8), h);
                BitConverter.TryWriteBytes(data.AsSpan(12), stride);
                src.CopyPixels(new Int32Rect(0, 0, w, h), data, stride, 16);
                System.IO.Directory.CreateDirectory(Dir);
                string path = FileFor(key), tmp = path + ".tmp";
                System.IO.File.WriteAllBytes(tmp, data);
                System.IO.File.Move(tmp, path, true);
            }
            catch (Exception) { }
        }

        public static void Purge()
        {
            try
            {
                if (!System.IO.Directory.Exists(Dir)) return;
                foreach (var f in new System.IO.DirectoryInfo(Dir).EnumerateFiles())
                    if (DateTime.UtcNow - f.LastWriteTimeUtc > PurgeAge) f.Delete();
            }
            catch (Exception) { }
        }
    }
}
