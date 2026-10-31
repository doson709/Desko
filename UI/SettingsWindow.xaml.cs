using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Desko.Core;
using Desko.Models;

namespace Desko.UI
{
    internal partial class SettingsWindow : Window
    {
        readonly FenceManager _mgr;
        bool _loading = true; // don't treat the initial control setup as user changes

        public SettingsWindow(FenceManager mgr)
        {
            _mgr = mgr;
            InitializeComponent();
            SourceInitialized += (_, _) => DialogTheme.Apply(this);
            Load();
            _loading = false;
        }

        AppConfig Cfg => _mgr.Config;

        void Load()
        {
            Check(BackdropChoices, Cfg.Backdrop);
            // A shared value only when every fence agrees; otherwise nothing is pre-selected.
            var colors = Cfg.Fences.Select(f => f.Color).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (colors.Count == 1) Check(ColorChoices, colors[0]);
            var opacity = Cfg.Fences.Count > 0 ? Cfg.Fences.Average(f => f.Opacity) : 0.4;
            OpacitySlider.Value = Math.Round(opacity * 20) / 20;
            OpacityText.Text = $"{OpacitySlider.Value * 100:0}%";
            var sizes = Cfg.Fences.Select(f => f.IconSize).Distinct().ToList();
            if (sizes.Count == 1) Check(IconChoices, sizes[0].ToString());
            IconFollowDesktop.Content = $"Giống desktop ({_mgr.DesktopIconSize}px)";
            GpuRendering.IsChecked = Cfg.GpuRendering;

            foreach (var (text, get, set) in new (string, Func<bool>, Action<bool>)[]
            {
                ("Nhấp đúp vùng trống desktop để ẩn / hiện fence", () => Cfg.DoubleClickToHide, v => Cfg.DoubleClickToHide = v),
                ("Tự xếp gọn icon ngoài fence (như Auto arrange)", () => Cfg.ArrangesLooseIcons, v => Cfg.AutoArrangeLoose = v),
                ("Căn icon ngoài fence theo lưới", () => Cfg.AlignLooseIcons, v => Cfg.AlignLooseIcons = v),
                ("Kéo chuột phải trên desktop để tạo fence", () => Cfg.RightDragToCreate, v => Cfg.RightDragToCreate = v),
                ("Mở file bằng một cú nhấp (Folder Portal)", () => Cfg.SingleClickOpen, v => Cfg.SingleClickOpen = v),
                ("Bắt dính lưới khi di chuyển fence", () => Cfg.SnapToGrid, v => Cfg.SnapToGrid = v),
                ("Tự động xếp file mới trên desktop theo quy tắc của các fence", () => Cfg.AutoApplyRules, v => Cfg.AutoApplyRules = v),
            })
            {
                var box = new CheckBox { Content = text, IsChecked = get() };
                box.Click += (_, _) =>
                {
                    set(box.IsChecked == true);
                    _mgr.ApplySettings();
                    _mgr.SaveSoon();
                };
                BehaviourToggles.Children.Add(box);
            }

            AutoStartToggle.IsChecked = AutoStart.Known ?? false;
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            VersionText.Text = $"Desko {version?.ToString(3)}  ·  sắp xếp desktop bằng các fence";
        }

        static void Check(Panel group, string tag)
        {
            foreach (var r in group.Children.OfType<RadioButton>())
                if (string.Equals(r.Tag as string, tag, StringComparison.OrdinalIgnoreCase)) r.IsChecked = true;
        }

        void Backdrop_Checked(object sender, RoutedEventArgs e)
        {
            if (_loading || sender is not RadioButton { Tag: string mode }) return;
            Cfg.Backdrop = mode;
            _mgr.ApplySettings();
            _mgr.SaveSoon();
        }

        void Color_Checked(object sender, RoutedEventArgs e)
        {
            if (_loading || sender is not RadioButton { Tag: string color }) return;
            _mgr.Checkpoint("Đổi màu mọi fence");
            _mgr.ApplyToAllFences(f => f.Color = color);
        }

        void Opacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (OpacityText == null) return;
            OpacityText.Text = $"{e.NewValue * 100:0}%";
            if (_loading) return;
            _mgr.ApplyToAllFences(f => f.Opacity = e.NewValue);
        }

        void Icon_Checked(object sender, RoutedEventArgs e)
        {
            if (_loading || sender is not RadioButton { Tag: string tag } || !int.TryParse(tag, out int size)) return;
            _mgr.ApplyToAllFences(f => f.IconSize = size, reload: true);
        }

        void Gpu_Click(object sender, RoutedEventArgs e)
        {
            Cfg.GpuRendering = GpuRendering.IsChecked == true;
            _mgr.SaveSoon();
        }

        void AutoStart_Click(object sender, RoutedEventArgs e)
        {
            AutoStart.Enabled = AutoStartToggle.IsChecked == true;
            _mgr.UpdateRestoreEntry();
        }

        void Welcome_Click(object sender, RoutedEventArgs e) => _mgr.ShowWelcome();

        void DataFolder_Click(object sender, RoutedEventArgs e) =>
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{ConfigStore.AppDataDir}\"") { UseShellExecute = true });
    }
}
