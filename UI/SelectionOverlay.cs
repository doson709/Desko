using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Desko.Interop;

namespace Desko.UI
{
    // The rectangle drawn while right-dragging on the desktop to create a new fence.
    internal sealed class SelectionOverlay : Window
    {
        IntPtr _hwnd;

        public SelectionOverlay()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            ResizeMode = ResizeMode.NoResize;
            Left = -32000; Top = -32000; Width = 10; Height = 10;
            Content = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x40, 0x5A, 0xA8, 0xFF)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0xD0, 0x9A, 0xD0, 0xFF)),
                BorderThickness = new Thickness(1.5),
                CornerRadius = new CornerRadius(4),
            };
            SourceInitialized += (_, _) =>
            {
                _hwnd = new WindowInteropHelper(this).Handle;
                Native.AddExStyle(_hwnd, Native.WS_EX_NOACTIVATE | Native.WS_EX_TRANSPARENT | Native.WS_EX_TOOLWINDOW);
            };
        }

        public void ShowRect(Native.RECT r)
        {
            if (!IsVisible) Show();
            Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, r.Left, r.Top, Math.Max(1, r.Width), Math.Max(1, r.Height),
                Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
        }
    }

    // A plain Win32 popup menu; works even when our process is not in the foreground.
    internal static class NativePopupMenu
    {
        static HwndSource? _host;

        public static int Show(IEnumerable<(int Id, string? Text)> items)
        {
            _host ??= new HwndSource(new HwndSourceParameters("DeskoMenuHost")
            {
                Width = 0,
                Height = 0,
                WindowStyle = unchecked((int)0x80000000), // WS_POPUP, never shown
                ExtendedWindowStyle = (int)Native.WS_EX_TOOLWINDOW,
            });
            IntPtr hwnd = _host.Handle;
            IntPtr menu = Native.CreatePopupMenu();
            try
            {
                foreach (var (id, text) in items)
                {
                    if (text == null) Native.AppendMenu(menu, Native.MF_SEPARATOR, UIntPtr.Zero, null);
                    else Native.AppendMenu(menu, Native.MF_STRING, (UIntPtr)id, text);
                }
                Native.ForceForeground(hwnd);
                Native.GetCursorPos(out var pt);
                int cmd = Native.TrackPopupMenuEx(menu, Native.TPM_RETURNCMD | Native.TPM_RIGHTBUTTON, pt.X, pt.Y, hwnd, IntPtr.Zero);
                Native.PostMessage(hwnd, Native.WM_NULL, IntPtr.Zero, IntPtr.Zero);
                return cmd;
            }
            finally { Native.DestroyMenu(menu); }
        }
    }
}
