using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Desko.Core;
using Desko.Interop;

namespace Desko.UI
{
    // Notification-area icon on the raw Shell_NotifyIcon API with a native menu: no WinForms to load,
    // and the menu looks like every other tray menu (rounded, follows light/dark).
    internal sealed class TrayIcon : IDisposable
    {
        const int WM_APP_TRAY = 0x8001;
        const int WM_CONTEXTMENU = 0x007B, NIN_SELECT = 0x0400, NIN_KEYSELECT = 0x0401;
        const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
        const uint NIF_MESSAGE = 0x1, NIF_ICON = 0x2, NIF_TIP = 0x4, NIF_INFO = 0x10, NIF_SHOWTIP = 0x80;
        const uint NIIF_INFO = 0x1, NOTIFYICON_VERSION_4 = 4;
        const uint MF_STRING = 0x0, MF_CHECKED = 0x8, MF_POPUP = 0x10, MF_SEPARATOR = 0x800;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct NOTIFYICONDATA
        {
            public int cbSize;
            public IntPtr hWnd;
            public uint uID, uFlags, uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
            public uint dwState, dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
            public uint uVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
            public uint dwInfoFlags;
            public Guid guidItem;
            public IntPtr hBalloonIcon;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern bool Shell_NotifyIcon(uint msg, ref NOTIFYICONDATA data);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool AppendMenu(IntPtr menu, uint flags, UIntPtr id, string? text);
        [DllImport("user32.dll")] static extern IntPtr CreateIconFromResourceEx(byte[] data, uint size, bool icon, uint version, int cx, int cy, uint flags);
        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr icon);

        readonly FenceManager _mgr;
        readonly HwndSource _host;
        readonly IntPtr _icon;
        readonly uint _taskbarCreated = Native.RegisterWindowMessage("TaskbarCreated");
        readonly List<(string Text, Func<bool> Get, Action<bool> Set)> _toggles = new();

        public TrayIcon(FenceManager mgr)
        {
            _mgr = mgr;
            var cfg = mgr.Config;
            _toggles.Add(("Nhấp đúp desktop để ẩn / hiện fence", () => cfg.DoubleClickToHide, v => cfg.DoubleClickToHide = v));
            _toggles.Add(("Ẩn luôn icon desktop khi ẩn fence", () => cfg.HideIconsToo, v => cfg.HideIconsToo = v));
            _toggles.Add(("Kéo chuột phải trên desktop để tạo fence", () => cfg.RightDragToCreate, v => cfg.RightDragToCreate = v));
            _toggles.Add(("Tự động xếp file mới trên desktop theo quy tắc", () => cfg.AutoApplyRules, v =>
            {
                cfg.AutoApplyRules = v;
                if (v) _mgr.TidyDesktop(null, quiet: false);
            }));
            _toggles.Add(("Mở file bằng một cú nhấp", () => cfg.SingleClickOpen, v => cfg.SingleClickOpen = v));
            _toggles.Add(("Bắt dính lưới khi di chuyển", () => cfg.SnapToGrid, v => cfg.SnapToGrid = v));
            _toggles.Add(("Nền kính mờ Acrylic", () => cfg.Backdrop == "Acrylic", _ => cfg.Backdrop = "Acrylic"));
            _toggles.Add(("Nền làm mờ (Blur)", () => cfg.Backdrop == "Blur", _ => cfg.Backdrop = "Blur"));
            _toggles.Add(("Nền trong suốt", () => cfg.Backdrop == "Clear", _ => cfg.Backdrop = "Clear"));
            // The cached answer: asking Task Scheduler here would stall the UI.
            _toggles.Add(("Khởi động cùng Windows", () => AutoStart.Known ?? false, v => AutoStart.Enabled = v));

            // A hidden top-level window (not message-only: those miss the TaskbarCreated broadcast).
            _host = new HwndSource(new HwndSourceParameters("DeskoTray") { Width = 0, Height = 0, WindowStyle = 0 });
            _host.AddHook(WndProc);
            _icon = CreateAppIcon();
            Add();
        }

        NOTIFYICONDATA Data(uint flags) => new()
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _host.Handle,
            uID = 1,
            uFlags = flags,
            uCallbackMessage = WM_APP_TRAY,
            hIcon = _icon,
            szTip = "Desko",
            szInfo = "",
            szInfoTitle = "",
        };

        void Add()
        {
            var data = Data(NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP);
            Shell_NotifyIcon(NIM_ADD, ref data);
            data.uVersion = NOTIFYICON_VERSION_4;
            Shell_NotifyIcon(NIM_SETVERSION, ref data);
        }

        public void ShowTip(string title, string text)
        {
            var data = Data(NIF_INFO);
            data.szInfoTitle = title;
            data.szInfo = text;
            data.dwInfoFlags = NIIF_INFO;
            Shell_NotifyIcon(NIM_MODIFY, ref data);
        }

        IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_APP_TRAY)
            {
                switch (lParam.ToInt32() & 0xFFFF)
                {
                    case NIN_SELECT:
                    case NIN_KEYSELECT:
                        _mgr.ToggleHidden();
                        break;
                    case WM_CONTEXTMENU:
                        ShowMenu();
                        break;
                }
                handled = true;
            }
            else if (msg == (int)_taskbarCreated) Add(); // Explorer restarted: the icon must be re-added
            return IntPtr.Zero;
        }

        void ShowMenu()
        {
            const int CmdNew = 1, CmdToggle = 2, CmdTidy = 3, CmdSettings = 4, CmdUndo = 5, CmdSearch = 6, CmdPeek = 7, CmdExit = 9, CmdToggleBase = 100;
            IntPtr menu = Native.CreatePopupMenu(), settings = Native.CreatePopupMenu();
            try
            {
                for (int i = 0; i < _toggles.Count; i++)
                {
                    if (i == _toggles.Count - 1) AppendMenu(settings, MF_SEPARATOR, UIntPtr.Zero, null);
                    AppendMenu(settings, MF_STRING | (_toggles[i].Get() ? MF_CHECKED : 0), (UIntPtr)(CmdToggleBase + i), _toggles[i].Text);
                }
                if (_mgr.UndoDescription is string undo)
                {
                    AppendMenu(menu, MF_STRING, (UIntPtr)CmdUndo, $"Hoàn tác: {undo}");
                    AppendMenu(menu, MF_SEPARATOR, UIntPtr.Zero, null);
                }
                AppendMenu(menu, MF_STRING, (UIntPtr)CmdNew, "Tạo fence mới");
                AppendMenu(menu, MF_STRING, (UIntPtr)CmdToggle, "Ẩn / hiện tất cả fence");
                AppendMenu(menu, MF_STRING, (UIntPtr)CmdTidy, "Dọn desktop theo quy tắc");
                AppendMenu(menu, MF_STRING, (UIntPtr)CmdSearch, "Tìm trong các fence\tCtrl+Alt+F");
                AppendMenu(menu, MF_STRING, (UIntPtr)CmdPeek, "Nhìn trộm fence\tCtrl+Alt+D");
                AppendMenu(menu, MF_SEPARATOR, UIntPtr.Zero, null);
                AppendMenu(menu, MF_POPUP, (UIntPtr)(ulong)settings, "Cài đặt nhanh");
                AppendMenu(menu, MF_STRING, (UIntPtr)CmdSettings, "Cài đặt…");
                AppendMenu(menu, MF_SEPARATOR, UIntPtr.Zero, null);
                AppendMenu(menu, MF_STRING, (UIntPtr)CmdExit, "Thoát");

                // Standard tray-menu dance: foreground first, or the menu won't close on an outside click.
                Native.SetForegroundWindow(_host.Handle);
                Native.GetCursorPos(out var pt);
                int cmd = Native.TrackPopupMenuEx(menu, Native.TPM_RETURNCMD | Native.TPM_RIGHTBUTTON, pt.X, pt.Y, _host.Handle, IntPtr.Zero);
                Native.PostMessage(_host.Handle, Native.WM_NULL, IntPtr.Zero, IntPtr.Zero);

                switch (cmd)
                {
                    case CmdNew: _mgr.CreateFenceAtCursor(); break;
                    case CmdToggle: _mgr.ToggleHidden(); break;
                    case CmdTidy:
                        int n = _mgr.TidyDesktop(null, quiet: false);
                        ShowTip("Desko", n > 0 ? $"Đã xếp {n} mục vào fence." : "Không có mục nào khớp quy tắc của các fence.");
                        break;
                    case CmdSettings: _mgr.ShowSettings(); break;
                    case CmdUndo: _mgr.Undo(); break;
                    case CmdSearch: _mgr.ShowGlobalSearch(); break;
                    case CmdPeek: _mgr.BeginPeek(); break;
                    case CmdExit: Application.Current.Shutdown(); break;
                    case >= CmdToggleBase when cmd - CmdToggleBase < _toggles.Count:
                        var toggle = _toggles[cmd - CmdToggleBase];
                        toggle.Set(!toggle.Get());
                        _mgr.ApplySettings();
                        _mgr.SaveSoon();
                        break;
                }
            }
            finally
            {
                Native.DestroyMenu(menu); // also destroys the attached submenu
            }
        }

        // The Desko logo, drawn with WPF and turned into an HICON via PNG (no System.Drawing needed).
        static IntPtr CreateAppIcon()
        {
            int size = Native.GetSystemMetrics(50 /* SM_CXSMICON */) * 2;
            if (size <= 0) size = 32;
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen()) BrandLogo.Draw(dc, size);
            var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            byte[] png = ms.ToArray();
            return CreateIconFromResourceEx(png, (uint)png.Length, true, 0x00030000, size, size, 0);
        }

        public void Dispose()
        {
            var data = Data(0);
            Shell_NotifyIcon(NIM_DELETE, ref data);
            if (_icon != IntPtr.Zero) DestroyIcon(_icon);
            _host.Dispose();
        }
    }
}
