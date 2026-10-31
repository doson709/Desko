using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Desko.Interop;

namespace Desko.UI
{
    // Small topmost dialogs. Fences live at the bottom of the z-order, so dialogs must not be owned by them.
    internal static class Dialogs
    {
        public static string? Prompt(string title, string message, string initial, int selectLength = -1)
        {
            var box = new TextBox { Text = initial, Margin = new Thickness(0, 10, 0, 0), Padding = new Thickness(4, 3, 4, 3) };
            string? result = null;
            var w = Build(title, message, box, () => result = box.Text, "OK", cancel: true);
            w.Loaded += (_, _) =>
            {
                box.Focus();
                if (selectLength >= 0) box.Select(0, Math.Min(selectLength, box.Text.Length));
                else box.SelectAll();
            };
            w.ShowDialog();
            return result;
        }

        // Palette + hex box; returns "#RRGGBB" or null when cancelled.
        public static string? PickColor(string title, string current)
        {
            string[] palette =
            {
                "#000000", "#1F1F1F", "#2B2B2B", "#3C3C3C", "#5A5A5A", "#7A7A7A",
                "#0B2E59", "#1E4E8C", "#2C6BC2", "#0F6B6B", "#138A8A", "#1F6B32",
                "#2E8B45", "#4B2A7B", "#6A3FB0", "#8A2A5E", "#B03A78", "#7A1F2B",
                "#A8323F", "#8A4A12", "#B8651C", "#7A6A12", "#4A4A1F", "#3B2A1F",
            };
            var hex = new TextBox { Text = current, Width = 110, Margin = new Thickness(8, 0, 0, 0), VerticalContentAlignment = VerticalAlignment.Center };
            var preview = new Border { Width = 34, Height = 26, CornerRadius = new CornerRadius(4), BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1) };
            void UpdatePreview()
            {
                try { preview.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex.Text)); }
                catch (FormatException) { }
            }
            hex.TextChanged += (_, _) => UpdatePreview();
            UpdatePreview();

            var grid = new System.Windows.Controls.Primitives.UniformGrid { Columns = 6, Margin = new Thickness(0, 10, 0, 10) };
            foreach (var c in palette)
            {
                var swatch = new Button
                {
                    Width = 46, Height = 30, Margin = new Thickness(3),
                    Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(c)),
                    BorderBrush = Brushes.Transparent, ToolTip = c,
                };
                swatch.Click += (_, _) => hex.Text = c;
                grid.Children.Add(swatch);
            }
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(preview);
            row.Children.Add(hex);
            var content = new StackPanel();
            content.Children.Add(grid);
            content.Children.Add(row);

            string? result = null;
            var w = Build(title, "Chọn một màu hoặc nhập mã màu (ví dụ #1E4E8C):", content, () =>
            {
                try
                {
                    var c = (Color)ColorConverter.ConvertFromString(hex.Text);
                    result = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
                }
                catch (FormatException) { }
            }, "OK", cancel: true);
            w.ShowDialog();
            return result;
        }

        // Set only through the test channel (Core/TestChannel), so tests never wait on a dialog.
        public static bool? TestAnswer;

        public static bool Confirm(string title, string message)
        {
            if (TestAnswer is bool answer) return answer;
            bool ok = false;
            Build(title, message, null, () => ok = true, "Đồng ý", cancel: true).ShowDialog();
            return ok;
        }

        public static void Info(string title, string message) =>
            Build(title, message, null, () => { }, "OK", cancel: false).ShowDialog();

        static Window Build(string title, string message, UIElement? extra, Action onOk, string okText, bool cancel)
        {
            var w = new Window
            {
                Title = title,
                Width = 440,
                SizeToContent = SizeToContent.Height,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Topmost = true,
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 13,
            };
            var panel = new StackPanel { Margin = new Thickness(18) };
            panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
            if (extra != null) panel.Children.Add(extra);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 18, 0, 0),
            };
            var ok = new Button { Content = okText, MinWidth = 90, Padding = new Thickness(10, 3, 10, 3), IsDefault = true };
            ok.Click += (_, _) => { onOk(); w.DialogResult = true; };
            buttons.Children.Add(ok);
            if (cancel)
                buttons.Children.Add(new Button
                {
                    Content = "Hủy",
                    MinWidth = 90,
                    Padding = new Thickness(10, 3, 10, 3),
                    Margin = new Thickness(8, 0, 0, 0),
                    IsCancel = true,
                });
            panel.Children.Add(buttons);
            w.Content = panel;
            w.Loaded += (_, _) =>
            {
                Native.ForceForeground(new WindowInteropHelper(w).Handle);
                w.Activate();
            };
            return w;
        }
    }
}
