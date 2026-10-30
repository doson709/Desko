using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Desko.Interop;

namespace Desko.UI
{
    // Small label that follows the cursor (e.g. "4 × 2" while resizing a fence). Topmost, never takes focus or clicks.
    internal sealed class CursorHint : Window
    {
        static readonly Brush NormalBg = Frozen(Color.FromArgb(0xE6, 0x20, 0x20, 0x20));
        static readonly Brush NormalBorder = Frozen(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF));
        static readonly Brush ExactBg = Frozen(Color.FromArgb(0xF0, 0x1E, 0x6F, 0xD9));
        static readonly Brush ExactBorder = Frozen(Color.FromArgb(0xFF, 0x9C, 0xCC, 0xFF));

        readonly TextBlock _text;
        readonly Border _box;
        IntPtr _hwnd;

        public CursorHint()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.WidthAndHeight;
            Left = -32000; Top = -32000;

            _text = new TextBlock
            {
                Foreground = Brushes.White,
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
            };
            _box = new Border
            {
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 3, 8, 3),
                BorderThickness = new Thickness(1),
                Child = _text,
            };
            Content = _box;
            SourceInitialized += (_, _) =>
            {
                _hwnd = new WindowInteropHelper(this).Handle;
                Native.AddExStyle(_hwnd, Native.WS_EX_NOACTIVATE | Native.WS_EX_TRANSPARENT | Native.WS_EX_TOOLWINDOW);
            };
        }

        public void ShowAt(string text, bool highlight)
        {
            _text.Text = text;
            _box.Background = highlight ? ExactBg : NormalBg;
            _box.BorderBrush = highlight ? ExactBorder : NormalBorder;
            if (!IsVisible) Show();
            UpdateLayout();

            Native.GetCursorPos(out var pt);
            double s = Native.ScaleForPoint(pt);
            int w = (int)(ActualWidth * s), h = (int)(ActualHeight * s);
            int x = pt.X + (int)(16 * s), y = pt.Y + (int)(20 * s);
            var work = Native.WorkAreaFromPoint(pt);
            if (x + w > work.Right) x = pt.X - w - (int)(8 * s);
            if (y + h > work.Bottom) y = pt.Y - h - (int)(8 * s);
            Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, x, y, 0, 0,
                Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
        }

        static Brush Frozen(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }
    }
}
