using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Desko.Core;

namespace Desko.UI
{
    // First-run walkthrough (also reachable from Settings): four short pages, the last one offering to
    // create starter fences from what is on the desktop.
    internal partial class WelcomeWindow : Window
    {
        readonly FenceManager _mgr;
        int _page;

        public WelcomeWindow(FenceManager mgr)
        {
            _mgr = mgr;
            InitializeComponent();
            SourceInitialized += (_, _) => DialogTheme.Apply(this);
            AutoStartToggle.IsChecked = AutoStart.Known ?? true;
            ShowPage(0);
        }

        void ShowPage(int page)
        {
            _page = page;
            for (int i = 0; i < Pages.Children.Count; i++)
                Pages.Children[i].Visibility = i == page ? Visibility.Visible : Visibility.Collapsed;
            var current = Pages.Children[page];
            current.Opacity = 0;
            current.BeginAnimation(OpacityProperty, new DoubleAnimation(1, System.TimeSpan.FromMilliseconds(220)));

            bool last = page == Pages.Children.Count - 1;
            BackButton.Visibility = page == 0 ? Visibility.Hidden : Visibility.Visible;
            SkipButton.Visibility = last ? Visibility.Visible : Visibility.Collapsed;
            NextButton.Content = last ? "Tạo các fence gợi ý" : "Tiếp";

            Dots.Children.Clear();
            for (int i = 0; i < Pages.Children.Count; i++)
                Dots.Children.Add(new Ellipse
                {
                    Width = 7, Height = 7, Margin = new Thickness(0, 0, 6, 0),
                    Fill = (System.Windows.Media.Brush)FindResource(i == page ? "Accent" : "FgSubtle"),
                    Opacity = i == page ? 1 : 0.5,
                });
        }

        void Back_Click(object sender, RoutedEventArgs e)
        {
            if (_page > 0) ShowPage(_page - 1);
        }

        void Next_Click(object sender, RoutedEventArgs e)
        {
            if (_page < Pages.Children.Count - 1)
            {
                ShowPage(_page + 1);
                return;
            }
            ApplyAutoStart();
            int created = _mgr.CreateStarterFences();
            Close();
            _mgr.Notify?.Invoke("Desko", created > 0
                ? $"Đã tạo {created} fence và xếp desktop vào đó. Ctrl + Z để hoàn tác."
                : "Desktop chưa có gì để xếp. Kéo chuột phải trên desktop để tự tạo fence.");
        }

        void Finish_Click(object sender, RoutedEventArgs e)
        {
            ApplyAutoStart();
            Close();
        }

        void ApplyAutoStart()
        {
            bool want = AutoStartToggle.IsChecked == true;
            if (AutoStart.Known != want) AutoStart.Enabled = want;
            _mgr.UpdateRestoreEntry();
        }
    }
}
