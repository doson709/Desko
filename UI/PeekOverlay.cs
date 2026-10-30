using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Desko.Interop;

namespace Desko.UI
{
    // "Peek": a dim layer over every other window, with the fences lifted above it.
    // A click on it, Esc, or switching to another app ends the peek.
    internal sealed class PeekOverlay : Window
    {
        readonly Action _end;
        IntPtr _hwnd;

        public PeekOverlay(Action end)
        {
            _end = end;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = new SolidColorBrush(Color.FromArgb(0x70, 0, 0, 0));
            ShowInTaskbar = false;
            Topmost = true;
            ResizeMode = ResizeMode.NoResize;
            Opacity = 0;
            Left = -32000; Top = -32000; Width = 10; Height = 10;
            SourceInitialized += (_, _) =>
            {
                _hwnd = new WindowInteropHelper(this).Handle;
                Native.AddExStyle(_hwnd, Native.WS_EX_TOOLWINDOW);
            };
            MouseLeftButtonDown += (_, _) => _end();
            MouseRightButtonDown += (_, _) => _end();
            KeyDown += (_, e) => { if (e.Key == Key.Escape) _end(); };
            Deactivated += (_, _) => Dispatcher.BeginInvoke(() =>
            {
                // Clicks on the (lifted) fences belong to the peek; anything else ends it.
                Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out uint pid);
                if (pid != (uint)Environment.ProcessId) _end();
            });
        }

        public void ShowOverlay()
        {
            Show();
            // Cover the whole virtual screen (all monitors), in physical pixels. Done after Show():
            // WPF applies its own Left/Top/Width/Height when the window first appears.
            int x = Native.GetSystemMetrics(76), y = Native.GetSystemMetrics(77);
            int w = Native.GetSystemMetrics(78), h = Native.GetSystemMetrics(79);
            Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, x, y, w, h, Native.SWP_NOACTIVATE);
            Native.ForceForeground(_hwnd);
            Activate();
            BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(160)));
        }
    }
}
