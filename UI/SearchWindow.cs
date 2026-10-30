using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Desko.Core;
using Desko.Interop;
using Desko.Models;
using Desko.Shell;

namespace Desko.UI
{
    // Ctrl+Alt+F: one search box over the items of every fence.
    // Enter opens the item, Ctrl+Enter shows it in its fence, Esc closes.
    internal sealed class SearchWindow : Window
    {
        internal sealed class Result : INotifyPropertyChanged
        {
            public required string Path { get; init; }
            public required string Name { get; init; }
            public required string Where { get; init; }
            public required FenceConfig Fence { get; init; }
            public string Key { get; init; } = "";

            ImageSource? _icon;
            public ImageSource? Icon
            {
                get => _icon;
                set { _icon = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon))); }
            }

            public event PropertyChangedEventHandler? PropertyChanged;
        }

        readonly FenceManager _mgr;
        readonly List<Result> _all;
        readonly TextBox _query;
        readonly ListBox _list;
        readonly TextBlock _hint;
        bool _closing;

        public SearchWindow(FenceManager mgr, List<Result> all)
        {
            _mgr = mgr;
            _all = all;
            Title = "Tìm trong fence";
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            Topmost = true;
            Width = 600;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            FontFamily = new FontFamily("Segoe UI");

            _query = new TextBox
            {
                FontSize = 18,
                Padding = new Thickness(36, 8, 10, 8),
                Background = Brushes.Transparent,
                Foreground = Brushes.White,
                CaretBrush = Brushes.White,
                BorderThickness = new Thickness(0),
            };
            var glyph = new TextBlock
            {
                Text = "",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 16,
                Foreground = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)),
                Margin = new Thickness(12, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
            };
            var queryRow = new Grid();
            queryRow.Children.Add(_query);
            queryRow.Children.Add(glyph);

            _list = new ListBox
            {
                MaxHeight = 420,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Margin = new Thickness(6, 0, 6, 6),
                ItemTemplate = BuildTemplate(),
                Focusable = false,
            };
            ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
            _list.MouseDoubleClick += (_, _) => OpenSelected(reveal: false);

            _hint = new TextBlock
            {
                Text = "Enter: mở   ·   Ctrl+Enter: chỉ vị trí trong fence   ·   Esc: đóng",
                Foreground = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)),
                FontSize = 11,
                Margin = new Thickness(14, 2, 14, 10),
            };

            var panel = new StackPanel();
            panel.Children.Add(queryRow);
            panel.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)) });
            panel.Children.Add(_list);
            panel.Children.Add(_hint);
            Content = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x99, 0x10, 0x10, 0x10)),
                CornerRadius = new CornerRadius(8),
                Child = panel,
            };

            _query.TextChanged += (_, _) => Filter();
            PreviewKeyDown += OnKey;
            Deactivated += (_, _) => CloseOnce();
            SourceInitialized += (_, _) =>
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                var source = HwndSource.FromHwnd(hwnd)!;
                source.CompositionTarget.BackgroundColor = Colors.Transparent;
                Native.AddExStyle(hwnd, Native.WS_EX_TOOLWINDOW);
                Native.SetBackdrop(hwnd, "Acrylic", darkFrame: true);
            };
            Loaded += (_, _) =>
            {
                Native.ForceForeground(new WindowInteropHelper(this).Handle);
                Activate();
                _query.Focus();
                Filter();
            };
        }

        static DataTemplate BuildTemplate()
        {
            var row = new FrameworkElementFactory(typeof(StackPanel));
            row.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
            row.SetValue(MarginProperty, new Thickness(4, 3, 4, 3));
            var icon = new FrameworkElementFactory(typeof(Image));
            icon.SetBinding(Image.SourceProperty, new Binding(nameof(Result.Icon)));
            icon.SetValue(WidthProperty, 28.0);
            icon.SetValue(HeightProperty, 28.0);
            icon.SetValue(MarginProperty, new Thickness(0, 0, 10, 0));
            row.AppendChild(icon);
            var texts = new FrameworkElementFactory(typeof(StackPanel));
            var name = new FrameworkElementFactory(typeof(TextBlock));
            name.SetBinding(TextBlock.TextProperty, new Binding(nameof(Result.Name)));
            name.SetValue(TextBlock.ForegroundProperty, Brushes.White);
            name.SetValue(TextBlock.FontSizeProperty, 14.0);
            texts.AppendChild(name);
            var where = new FrameworkElementFactory(typeof(TextBlock));
            where.SetBinding(TextBlock.TextProperty, new Binding(nameof(Result.Where)));
            where.SetValue(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)));
            where.SetValue(TextBlock.FontSizeProperty, 11.0);
            texts.AppendChild(where);
            row.AppendChild(texts);
            return new DataTemplate { VisualTree = row };
        }

        void Filter()
        {
            string q = TextMatch.Normalize(_query.Text.Trim());
            IEnumerable<Result> found = q.Length == 0
                ? _all
                : _all.Where(r => r.Key.Contains(q)).OrderBy(r => r.Key.StartsWith(q) ? 0 : 1); // prefix matches first
            var matches = found.Take(60).ToList();
            _list.ItemsSource = matches;
            _list.SelectedIndex = matches.Count > 0 ? 0 : -1;
            int px = (int)Math.Round(28 * VisualTreeHelper.GetDpi(this).DpiScaleX);
            foreach (var r in matches.Where(r => r.Icon == null))
            {
                var result = r;
                var stamp = File.Exists(r.Path) ? File.GetLastWriteTimeUtc(r.Path) : Directory.GetLastWriteTimeUtc(r.Path);
                IconLoader.Request(r.Path, px, stamp.Ticks, img => Dispatcher.BeginInvoke(() => result.Icon = img));
            }
        }

        void OnKey(object sender, KeyEventArgs e)
        {
            int count = _list.Items.Count;
            switch (e.Key)
            {
                case Key.Escape:
                    CloseOnce();
                    break;
                case Key.Down when count > 0:
                    _list.SelectedIndex = Math.Min(count - 1, _list.SelectedIndex + 1);
                    _list.ScrollIntoView(_list.SelectedItem);
                    break;
                case Key.Up when count > 0:
                    _list.SelectedIndex = Math.Max(0, _list.SelectedIndex - 1);
                    _list.ScrollIntoView(_list.SelectedItem);
                    break;
                case Key.Enter:
                    OpenSelected(reveal: (Keyboard.Modifiers & ModifierKeys.Control) != 0);
                    break;
                default:
                    return;
            }
            e.Handled = true;
        }

        void OpenSelected(bool reveal)
        {
            if (_list.SelectedItem is not Result r) return;
            CloseOnce();
            if (reveal) _mgr.Reveal(r.Fence, r.Path);
            else _mgr.OpenPath(r.Path);
        }

        void CloseOnce()
        {
            if (_closing) return;
            _closing = true;
            Close();
        }
    }
}
