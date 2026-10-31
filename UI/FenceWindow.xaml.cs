using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Desko.Core;
using Desko.Interop;
using Desko.Models;
using Desko.Shell;

namespace Desko.UI
{
    internal partial class FenceWindow : Window
    {
        const double TitleHeightDip = 28;
        const int CmdMoveToDesktop = 0x7F01;
        const int WM_SETTINGCHANGE = 0x001A, WM_DWMCOLORIZATIONCOLORCHANGED = 0x0320, WM_DISPLAYCHANGE = 0x007E;
        const int WM_WINDOWPOSCHANGED = 0x0047, SPI_SETDESKWALLPAPER = 0x0014;
        static readonly Brush HitBrush = Brush(Color.FromArgb(1, 0, 0, 0)); // invisible, but takes the mouse
        static readonly int WM_TASKBARCREATED = (int)Native.RegisterWindowMessage("TaskbarCreated");

        static readonly (string Name, string Hex)[] ColorPresets =
        {
            ("Đen", "#000000"), ("Xám than", "#2B2B2B"), ("Xanh dương", "#1E4E8C"), ("Xanh ngọc", "#0F6B6B"),
            ("Xanh lá", "#1F6B32"), ("Tím", "#4B2A7B"), ("Hồng", "#8A2A5E"), ("Đỏ", "#7A1F2B"), ("Cam", "#8A4A12"),
        };

        static readonly IComparer<string> Logical = Comparer<string>.Create(Native.StrCmpLogicalW);

        readonly FenceManager _mgr;
        readonly ObservableCollection<FileItem> _items = new();
        readonly DispatcherTimer _reloadTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
        readonly DispatcherTimer _peekTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
        FileSystemWatcher? _watcher;
        bool _peeking, _dragOver, _renaming, _menuOpen, _inSizeMove;
        bool _maybeDrag;
        Point _dragStart;
        ListBoxItem? _pendingSelect;

        public FenceWindow(FenceManager mgr, FenceConfig config)
        {
            _mgr = mgr;
            Config = config;
            ZoneId = mgr.NewZoneId();
            InitializeComponent();
            Items.ItemsSource = _items;
            Items.LostMouseCapture += (_, _) => EndMarquee();
            // One selection on screen at a time, like Explorer: selecting here clears the other fences.
            Items.SelectionChanged += (_, e) => { if (e.AddedItems.Count > 0) _mgr.ClearSelectionsExcept(this); };

            // Real bounds are applied in physical pixels once the HWND exists; the content fades in once shown.
            Root.Opacity = 0;
            Left = -32000; Top = -32000; Width = 200; Height = 100;

            _reloadTimer.Tick += (_, _) => { _reloadTimer.Stop(); Reload(); };
            _peekTimer.Tick += PeekTimer_Tick;
            _slowClickTimer.Tick += SlowClickTimer_Tick;
            SourceInitialized += OnSourceInitialized;
            Loaded += (_, _) =>
            {
                if (!_mgr.Hidden) FadeTo(1);
                if (PendingRename) { PendingRename = false; BeginRename(); }
            };
            Closed += (_, _) =>
            {
                StopWatcher();
                _reloadTimer.Stop();
                _peekTimer.Stop();
                StopFade();
                _backdrop?.Dispose();
                _backdrop = null;
            };
            IsVisibleChanged += (_, _) =>
            {
                _backdrop?.SetVisible(IsVisible);
                _mgr.UpdateZones();
            };

            DragEnter += Window_DragEnter;
            DragOver += Window_DragOver;
            DragLeave += Window_DragLeave;
            Drop += Window_Drop;
            PreviewMouseRightButtonUp += Window_PreviewMouseRightButtonUp;
            MouseEnter += Window_MouseEnter;
            MouseLeave += (_, _) =>
            {
                if (_resizeEdge == 0) { Cursor = null; ForceCursor = false; }
                if (_peeking) _peekTimer.Start();
            };
            PreviewMouseMove += Resize_PreviewMouseMove;
            PreviewMouseLeftButtonDown += Resize_PreviewMouseLeftButtonDown;
            PreviewMouseLeftButtonUp += Resize_PreviewMouseLeftButtonUp;
            LostMouseCapture += (_, _) => EndResize();
        }

        // The active tab. Every tab is its own FenceConfig; tabs of one fence share a GroupId.
        public FenceConfig Config { get; private set; }
        public IntPtr Handle { get; private set; }
        public bool ClosingByManager { get; set; }
        public bool PendingRename { get; set; }

        // Normal fences hold desktop icons; folder portals show a folder themselves.
        public bool IsDesktopFence => !Config.IsPortal;
        // Identifies this fence to the mouse hook.
        public int ZoneId { get; }

        FenceBackdrop? _backdrop;
        FenceBackdrop.Look? _look;
        bool _glow;
        Native.RECT? _spot;

        double DpiScale => Handle != IntPtr.Zero ? Native.GetDpiForWindow(Handle) / 96.0 : 1.0;
        int TitleHeightPx => (int)Math.Round((TitleHeightDip + 2) * DpiScale);

        #region Window plumbing

        void OnSourceInitialized(object? sender, EventArgs e)
        {
            Handle = new WindowInteropHelper(this).Handle;
            var source = HwndSource.FromHwnd(Handle)!;
            source.AddHook(WndProc);
            Native.AddExStyle(Handle, Native.WS_EX_TOOLWINDOW, Native.WS_EX_APPWINDOW);
            _backdrop = new FenceBackdrop();
            _backdrop.SetAlpha(0); // fades in with the window
            AttachToDesktop();
            ApplyBounds();
            ApplyAppearance();
            RestartWatcher();
            Reload();
        }

        // Owned by Progman: stays on the desktop layer (just above the icons) and survives Win+D / "Show desktop".
        public void AttachToDesktop()
        {
            var progman = DesktopShell.Progman;
            if (progman != IntPtr.Zero) Native.SetWindowLongPtr(Handle, Native.GWLP_HWNDPARENT, progman);
            Native.SetWindowPos(Handle, Native.HWND_BOTTOM, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }

        IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (ShellContextMenu.TryHandleMessage(msg, wParam, lParam, out var menuResult))
            {
                handled = true;
                return menuResult;
            }

            switch (msg)
            {
                case Native.WM_WINDOWPOSCHANGING:
                {
                    var pos = Marshal.PtrToStructure<Native.WINDOWPOS>(lParam);
                    if ((pos.flags & Native.SWP_NOZORDER) == 0)
                    {
                        pos.hwndInsertAfter = Native.HWND_BOTTOM;
                        Marshal.StructureToPtr(pos, lParam, false);
                    }
                    break;
                }
                case WM_WINDOWPOSCHANGED:
                {
                    var pos = Marshal.PtrToStructure<Native.WINDOWPOS>(lParam);
                    if ((pos.flags & (Native.SWP_NOMOVE | Native.SWP_NOSIZE)) != (Native.SWP_NOMOVE | Native.SWP_NOSIZE))
                        BoundsChanged();
                    break;
                }
                case Native.WM_ENTERSIZEMOVE:
                    Log.Trace($"fence \"{Config.Title}\": move loop starts");
                    _inSizeMove = true;
                    _moveOthers = _mgr.GetOtherRects(this);
                    _moveStartRect = _pressAnchored ? _pressRect : ScreenRect;
                    if (_pressAnchored) _moveStartCursor = _pressCursor;
                    else Native.GetCursorPos(out _moveStartCursor);
                    break;
                case Native.WM_EXITSIZEMOVE:
                {
                    Log.Trace($"fence \"{Config.Title}\": move loop ends");
                    _inSizeMove = false;
                    if (_mergeTarget is FenceWindow target)
                    {
                        SetMergeTarget(null);
                        EndLift();
                        UpdateBackdrop();
                        // Ask after the move loop has fully ended.
                        Dispatcher.BeginInvoke(() => AskMerge(target));
                        break;
                    }
                    // The grid is applied once on release; snapping to it while moving feels jerky.
                    var r = ScreenRect;
                    double s = DpiScale;
                    Snapper.SnapMove(ref r, _moveOthers, Native.WorkAreaFromRect(r), (int)(12 * s), GridPx(s), (int)(8 * s));
                    Native.SetWindowPos(Handle, IntPtr.Zero, r.Left, r.Top, 0, 0,
                        Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
                    EndLift();
                    UpdateBackdrop(); // repainted for where it landed
                    SaveBounds();
                    _mgr.FenceMoveEnded(); // icons it now covers make room
                    break;
                }
                case Native.WM_MOVING:
                {
                    // Windows builds each step from the previous (already snapped) rect, so snaps would pile up
                    // and the fence would drift away from the grab point. Rebuild it from the cursor instead.
                    Native.GetCursorPos(out var cursor);
                    var r = _moveStartRect;
                    int dx = cursor.X - _moveStartCursor.X, dy = cursor.Y - _moveStartCursor.Y;
                    r.Left += dx; r.Right += dx; r.Top += dy; r.Bottom += dy;

                    // Magnetic snapping to screen edges and other fences only.
                    double s = DpiScale;
                    Snapper.SnapMove(ref r, _moveOthers, Native.WorkAreaFromRect(r), (int)(10 * s), 0, (int)(8 * s));
                    Marshal.StructureToPtr(r, lParam, false);
                    if (!_movedOnce) { _movedOnce = true; Log.Trace($"fence \"{Config.Title}\": first move"); }
                    // The icons ride along on this window; the backdrop is put at the same place right now,
                    // so all three layers arrive on screen together.
                    BeginLift();
                    ParkLifted("fence moved");
                    _backdrop?.MoveTo(r);
                    SetMergeTarget(_mgr.WindowAt(cursor, this));
                    if (_mergeTarget != null) CursorHintWindow.ShowAt("Thả để gộp thành tab", highlight: true);
                    handled = true;
                    return new IntPtr(1);
                }
                case Native.WM_DPICHANGED:
                    // WPF resizes the window to keep its DIP size; we want our stored pixel bounds back.
                    if (!_inSizeMove)
                        Dispatcher.BeginInvoke(DispatcherPriority.Background, () => { ApplyBounds(); Reload(); });
                    break;
                case WM_DISPLAYCHANGE:
                    _mgr.DisplayChanged();
                    break;
                case WM_SETTINGCHANGE when wParam.ToInt32() == SPI_SETDESKWALLPAPER:
                    _mgr.WallpaperChanged();
                    break;
                case WM_SETTINGCHANGE when lParam != IntPtr.Zero && Marshal.PtrToStringUni(lParam) == "ImmersiveColorSet":
                case WM_DWMCOLORIZATIONCOLORCHANGED:
                    // Light/dark or accent colour changed in Windows settings.
                    Theme.Refresh();
                    ApplyAppearance();
                    break;
                default:
                    if (msg == WM_TASKBARCREATED)
                    {
                        AttachToDesktop();
                        _mgr.ExplorerRestarted();
                    }
                    break;
            }
            return IntPtr.Zero;
        }

        int GridPx(double scale) => _mgr.Config.SnapToGrid ? (int)Math.Round(_mgr.Config.GridSize * scale) : 0;

        // Moved or resized (by the user, an animation, a display change): the backdrop follows at once,
        // the icons once per frame.
        bool _iconsQueued, _movedOnce;
        Native.POINT _lastSize;

        void BoundsChanged()
        {
            var r = ScreenRect;
            bool resized = r.Width != _lastSize.X || r.Height != _lastSize.Y;
            _lastSize = new Native.POINT(r.Width, r.Height);
            // Being dragged: the backdrop moves along as it is (repainted once let go); otherwise it repaints.
            if (_inSizeMove && !resized) _backdrop?.MoveTo(r);
            else UpdateBackdrop();
            if (_lifted && resized) { BuildProxy(); _proxyKey = null; }
            if (_iconsQueued) return;
            _iconsQueued = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
            {
                _iconsQueued = false;
                if (Handle != IntPtr.Zero) _mgr.FenceBoundsChanged(this);
            });
        }

        void UpdateBackdrop()
        {
            if (_backdrop == null || _look == null || Handle == IntPtr.Zero) return;
            _backdrop.Update(ScreenRect, _look with { Glow = _glow, Spot = _spot });
        }

        #endregion

        #region Edge resizing

        // Windows will not size a borderless layered window from its edges, so resizing is done by hand.
        // Edge codes follow WMSZ_*: 1 left, 2 right, 3 top, 4 top-left, 5 top-right, 6 bottom, 7 bottom-left, 8 bottom-right.
        const int EdgeLeft = 1, EdgeRight = 2, EdgeTop = 3, EdgeBottom = 6;
        int _resizeEdge;
        Native.RECT _resizeStartRect, _moveStartRect;
        Native.POINT _resizeStartCursor, _moveStartCursor, _pressCursor;
        Native.RECT _pressRect;
        bool _pressAnchored;
        IReadOnlyList<Native.RECT> _moveOthers = Array.Empty<Native.RECT>();

        int EdgeAt(Point p)
        {
            if (Config.Locked || _renaming || _mgr.Hidden) return 0;
            const double band = 7;
            bool left = p.X < band, right = p.X >= ActualWidth - band;
            bool top = p.Y < 4, bottom = p.Y >= ActualHeight - band;
            if (Config.RolledUp && !_peeking) top = bottom = false;
            if (top && left) return 4;
            if (top && right) return 5;
            if (bottom && left) return 7;
            if (bottom && right) return 8;
            if (left) return EdgeLeft;
            if (right) return EdgeRight;
            if (top) return EdgeTop;
            return bottom ? EdgeBottom : 0;
        }

        static Cursor? CursorFor(int edge) => edge switch
        {
            1 or 2 => Cursors.SizeWE,
            3 or 6 => Cursors.SizeNS,
            4 or 8 => Cursors.SizeNWSE,
            5 or 7 => Cursors.SizeNESW,
            _ => null,
        };

        void Resize_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (_resizeEdge != 0)
            {
                Native.GetCursorPos(out var pt);
                int dx = pt.X - _resizeStartCursor.X, dy = pt.Y - _resizeStartCursor.Y;
                if (LiftSettling) { e.Handled = true; return; } // see BeginLift
                if (dx != 0 || dy != 0) ParkLifted("fence resized"); // the icons re-flow on this window while it is resized
                var r = _resizeStartRect;
                int edge = _resizeEdge;
                if (edge is 1 or 4 or 7) r.Left += dx;
                if (edge is 2 or 5 or 8) r.Right += dx;
                if (edge is 3 or 4 or 5) r.Top += dy;
                if (edge is 6 or 7 or 8) r.Bottom += dy;

                double s = DpiScale;
                bool rolled = Config.RolledUp && !_peeking;
                int minW = (int)(120 * s), minH = rolled ? TitleHeightPx : (int)(80 * s);
                var raw = r;
                Snapper.SnapSize(ref r, edge, _moveOthers, Native.WorkAreaFromRect(r),
                    (int)(10 * s), GridPx(s), (int)(8 * s), minW, minH);

                // "Fits exactly N icons" stops win over edge/grid snapping on their axis.
                int threshold = (int)(14 * s);
                bool horizontal = edge is 1 or 2 or 4 or 5 or 7 or 8, vertical = !rolled && edge is 3 or 4 or 5 or 6 or 7 or 8;
                if (horizontal && FitStop(raw.Width, _chromeDip.Width, _cellDip.Width, s, threshold, minW) is int w)
                {
                    if (edge is 1 or 4 or 7) r.Left = r.Right - w;
                    else r.Right = r.Left + w;
                }
                if (vertical && FitStop(raw.Height, _chromeDip.Height, _cellDip.Height, s, threshold, minH) is int h)
                {
                    if (edge is 3 or 4 or 5) r.Top = r.Bottom - h;
                    else r.Bottom = r.Top + h;
                }

                Native.SetWindowPos(Handle, IntPtr.Zero, r.Left, r.Top, r.Width, r.Height,
                    Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
                ShowSizeHint(r, s, rolled, horizontal, vertical);
                e.Handled = true;
                return;
            }

            int hover = e.LeftButton == MouseButtonState.Released ? EdgeAt(e.GetPosition(this)) : 0;
            Cursor = CursorFor(hover);
            ForceCursor = hover != 0;
            if (hover != 0) PrepareLift();
        }

        void Resize_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            int edge = EdgeAt(e.GetPosition(this));
            if (edge == 0) return;
            _resizeEdge = edge;
            _inSizeMove = true;
            _resizeStartRect = ScreenRect;
            Native.GetCursorPos(out _resizeStartCursor);
            _moveOthers = _mgr.GetOtherRects(this);
            MeasureCells();
            CaptureMouse();
            BeginLift(); // see TitleBar_MouseLeftButtonDown
            e.Handled = true;
        }

        Size _cellDip, _chromeDip;

        // Size of one icon cell and of everything around the icon area, measured from the live layout.
        void MeasureCells()
        {
            if (IsDesktopFence)
            {
                // The desktop's own icon grid.
                double s = DpiScale;
                var r = ScreenRect;
                var area = IconArea(r);
                _cellDip = new Size(IconGrid.Cell.X / s, IconGrid.Cell.Y / s);
                _chromeDip = new Size((r.Width - area.Width) / s, (r.Height - area.Height) / s);
                return;
            }
            // Portals use the same cells (see UpdateCellSize).
            _cellDip = new Size(CellWidth, CellHeight);

            var sv = FindChild<ScrollViewer>(Items);
            double viewW = sv != null && sv.ViewportWidth > 0 ? sv.ViewportWidth : ActualWidth - 16;
            double viewH = sv != null && sv.ViewportHeight > 0 ? sv.ViewportHeight : ActualHeight - TitleHeightDip - 8;
            _chromeDip = new Size(Math.Max(0, ActualWidth - viewW), Math.Max(0, ActualHeight - viewH));
        }

        // The window length (px) that fits a whole number of cells, if one is within the threshold.
        static int? FitStop(int length, double chromeDip, double cellDip, double scale, int threshold, int min)
        {
            if (cellDip <= 0) return null;
            int? best = null;
            for (int n = 1; n <= 40; n++)
            {
                int target = (int)Math.Ceiling((chromeDip + n * cellDip) * scale) + 1;
                if (target < min) continue;
                int d = Math.Abs(target - length);
                if (d <= threshold && (best == null || d < Math.Abs(best.Value - length))) best = target;
            }
            return best;
        }

        static CursorHint? _cursorHint;
        static CursorHint CursorHintWindow => _cursorHint ??= new CursorHint();

        // "3,6 × 2" next to the cursor; whole numbers (and a highlight) when the size fits the icons exactly.
        void ShowSizeHint(Native.RECT r, double scale, bool rolled, bool horizontal, bool vertical)
        {
            double cols = Math.Max(0, (r.Width / scale - _chromeDip.Width) / _cellDip.Width);
            double rows = Math.Max(0, (r.Height / scale - _chromeDip.Height) / _cellDip.Height);
            bool colsExact = IsWhole(cols), rowsExact = IsWhole(rows);
            bool highlight = (!horizontal || colsExact) && (!vertical || rowsExact);
            string text = rolled ? $"{Format(cols, colsExact)} cột" : $"{Format(cols, colsExact)} × {Format(rows, rowsExact)}";
            CursorHintWindow.ShowAt(text, highlight);
        }

        // Fit stops land a pixel or two past the exact multiple, so allow a little slack.
        static bool IsWhole(double v) => v >= 0.95 && Math.Abs(v - Math.Round(v)) < 0.05;

        static string Format(double v, bool whole) =>
            whole ? Math.Round(v).ToString("0") : v.ToString("0.0", System.Globalization.CultureInfo.GetCultureInfo("vi-VN"));

        static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T t) return t;
                if (FindChild<T>(child) is T found) return found;
            }
            return null;
        }

        void Resize_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_resizeEdge == 0) return;
            ReleaseMouseCapture(); // ends the resize via LostMouseCapture
            e.Handled = true;
        }

        void EndResize()
        {
            if (_resizeEdge == 0) return;
            _resizeEdge = 0;
            _inSizeMove = false;
            _cursorHint?.Hide();
            Cursor = null;
            ForceCursor = false;
            EndLift();
            SaveBounds();
            _mgr.FenceMoveEnded();
        }

        bool Collapsed => Config.RolledUp && !_peeking;

        // animate: roll up / unfold smoothly when only the height changes.
        public void ApplyBounds(bool animate = false)
        {
            if (Handle == IntPtr.Zero) return;
            EnsureOnScreen();
            int h = Collapsed ? TitleHeightPx : Config.Height;
            var current = ScreenRect;
            StopHeightAnimation();
            if (animate && IsVisible && current.Left == Config.X && current.Top == Config.Y &&
                current.Width == Config.Width && current.Height != h)
            {
                AnimateHeight(current.Height, h);
                return;
            }
            Native.SetWindowPos(Handle, IntPtr.Zero, Config.X, Config.Y, Config.Width, h,
                Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        }

        EventHandler? _heightFrame;

        void AnimateHeight(int from, int to)
        {
            var clock = Stopwatch.StartNew();
            const double duration = 170;
            _heightFrame = (_, _) =>
            {
                double t = Math.Min(1, clock.Elapsed.TotalMilliseconds / duration);
                double eased = 1 - Math.Pow(1 - t, 3); // ease-out cubic
                int h = (int)Math.Round(from + (to - from) * eased);
                Native.SetWindowPos(Handle, IntPtr.Zero, 0, 0, Config.Width, h,
                    Native.SWP_NOMOVE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
                if (t >= 1) StopHeightAnimation();
            };
            CompositionTarget.Rendering += _heightFrame;
        }

        void StopHeightAnimation()
        {
            if (_heightFrame == null) return;
            CompositionTarget.Rendering -= _heightFrame;
            _heightFrame = null;
        }

        void EnsureOnScreen()
        {
            Config.Width = Math.Max(Config.Width, 100);
            Config.Height = Math.Max(Config.Height, 60);
            var r = new Native.RECT(Config.X, Config.Y, Config.X + Config.Width, Config.Y + TitleHeightPx);
            if (Native.MonitorFromRect(ref r, Native.MONITOR_DEFAULTTONULL) != IntPtr.Zero) return;
            var work = Native.WorkAreaFromRect(r);
            Config.X = work.Left + 40;
            Config.Y = work.Top + 40;
        }

        void SaveBounds()
        {
            Native.GetWindowRect(Handle, out var r);
            Config.X = r.Left;
            Config.Y = r.Top;
            Config.Width = r.Width;
            if (!Config.RolledUp || _peeking) Config.Height = r.Height;
            _mgr.SyncGroup(Config);
            _mgr.SaveSoon();
        }

        public Native.RECT ScreenRect
        {
            get { Native.GetWindowRect(Handle, out var r); return r; }
        }

        // Fades the window and its backdrop together.
        EventHandler? _fadeFrame;

        public void FadeTo(double target, Action? done = null)
        {
            StopFade();
            double from = Root.Opacity;
            var clock = Stopwatch.StartNew();
            const double duration = 180;
            _fadeFrame = (_, _) =>
            {
                double t = Math.Min(1, clock.Elapsed.TotalMilliseconds / duration);
                double eased = 1 - (1 - t) * (1 - t); // ease-out quadratic
                double value = from + (target - from) * eased;
                Root.Opacity = value;
                _backdrop?.SetAlpha((byte)Math.Round(value * 255));
                if (t < 1) return;
                StopFade();
                done?.Invoke();
            };
            CompositionTarget.Rendering += _fadeFrame;
        }

        void StopFade()
        {
            if (_fadeFrame == null) return;
            CompositionTarget.Rendering -= _fadeFrame;
            _fadeFrame = null;
        }

        public void HideFence() => Hide();

        public void ShowFence()
        {
            Show();
            _mgr.LayoutWindow(this);
        }

        public static Color ResolveColor(string setting) => setting switch
        {
            "auto" => Theme.IsLight ? Color.FromRgb(0xF3, 0xF3, 0xF3) : Colors.Black,
            "accent" => Theme.Accent(lightSurface: false),
            _ => TryParseColor(setting) ?? Colors.Black,
        };

        static Color? TryParseColor(string s)
        {
            try { return (Color)ColorConverter.ConvertFromString(s); }
            catch (FormatException) { return null; }
        }

        static SolidColorBrush Brush(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        public void ApplyAppearance()
        {
            var c = ResolveColor(Config.Color);
            double opacity = Math.Clamp(Config.Opacity, 0.02, 1.0);
            byte a = (byte)Math.Round(opacity * 255);
            // A light, fairly opaque tint needs dark text (light theme, or a light custom colour).
            bool lightSurface = Theme.Luminance(c) > 0.55 && opacity >= 0.35;
            double shade = lightSurface ? 0.93 : 0.7;
            var accent = Theme.Accent(lightSurface);
            double s = DpiScale;
            _look = new FenceBackdrop.Look(
                Tint: Color.FromArgb(a, c.R, c.G, c.B),
                TitleTint: Color.FromArgb((byte)Math.Min(255, a + 45), (byte)(c.R * shade), (byte)(c.G * shade), (byte)(c.B * shade)),
                Border: lightSurface ? Color.FromArgb(0x22, 0, 0, 0) : Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF),
                Accent: accent,
                TitlePx: (int)Math.Round(TitleHeightDip * s),
                Radius: (int)Math.Round(8 * s),
                Blur: _mgr.Config.Backdrop != "Clear",
                Noise: _mgr.Config.Backdrop == "Acrylic",
                Glow: false,
                Spot: null);
            UpdateBackdrop();
            UpdateHitArea();
            Resources["FenceTextBrush"] = Brush(lightSurface ? Color.FromRgb(0x1B, 0x1B, 0x1B) : Colors.White);
            Resources["FenceSubtleTextBrush"] = Brush(lightSurface ? Color.FromArgb(0x99, 0, 0, 0) : Color.FromArgb(0xAA, 0xFF, 0xFF, 0xFF));
            Resources["FenceShadowOpacity"] = lightSurface ? 0.0 : 0.9;
            Resources["HoverBrush"] = Brush(lightSurface ? Color.FromArgb(0x14, 0, 0, 0) : Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
            Resources["AccentBrush"] = Brush(accent);
            Resources["SelectionBrush"] = Brush(Theme.WithAlpha(accent, 0x55));
            Resources["SelectionBorderBrush"] = Brush(Theme.WithAlpha(accent, 0xAA));
            Resources["DropTargetBrush"] = Brush(Theme.WithAlpha(accent, 0x77));
            Resources["DropGlowBrush"] = Brush(Theme.WithAlpha(accent, 0x2A));
            Resources["MarqueeFillBrush"] = Brush(Theme.WithAlpha(accent, 0x33));

            Items.Visibility = IsDesktopFence ? Visibility.Collapsed : Visibility.Visible;
            TitleText.Text = DisplayTitle;
            BackButton.Visibility = _browsePath != null ? Visibility.Visible : Visibility.Collapsed;
            TitleBar.ToolTip = Config.IsPortal ? Config.FolderPath : null;
            TitleBar.Cursor = Config.Locked ? null : Cursors.SizeAll;
            LockGlyph.Visibility = Config.Locked ? Visibility.Visible : Visibility.Collapsed;
            PortalGlyph.Visibility = Config.IsPortal && _browsePath == null ? Visibility.Visible : Visibility.Collapsed;
            RebuildTabs();
        }

        #endregion

        #region Tabs

        static readonly Brush ActiveTabBrush = new SolidColorBrush(Color.FromArgb(0x48, 0xFF, 0xFF, 0xFF));

        // With two or more tabs the title bar shows them instead of the title.
        public void RebuildTabs()
        {
            var tabs = _mgr.TabsOf(Config);
            TabStrip.Children.Clear();
            if (tabs.Count < 2)
            {
                TabStrip.Visibility = Visibility.Collapsed;
                if (!_renaming) TitleText.Visibility = Visibility.Visible;
                return;
            }
            TitleText.Visibility = Visibility.Collapsed;
            if (!_renaming) TabStrip.Visibility = Visibility.Visible;
            foreach (var tab in tabs)
            {
                bool active = tab == Config;
                var text = new TextBlock
                {
                    Text = tab.Title,
                    Foreground = Brushes.White,
                    Opacity = active ? 1 : 0.7,
                    FontSize = 12.5,
                    MaxWidth = 150,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                var header = new Border
                {
                    Child = text,
                    Tag = tab,
                    Padding = new Thickness(10, 0, 10, 0),
                    Margin = new Thickness(1, 4, 1, 4),
                    CornerRadius = new CornerRadius(3),
                    Background = active ? ActiveTabBrush : Brushes.Transparent,
                    ToolTip = tab.IsPortal ? tab.FolderPath : tab.Title,
                };
                // Not handled: the click goes on to the title bar, which still drags / rolls up the fence.
                header.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 1) SwitchTab(tab); };
                TabStrip.Children.Add(header);
            }
        }

        public void SwitchTab(FenceConfig tab)
        {
            if (tab == Config) return;
            Config = tab;
            _browsePath = null;
            _scrollRow = 0;
            ClearSearch();
            _mgr.SetActiveTab(tab);
            Items.UnselectAll();
            RestartWatcher();
            ApplyAppearance();
            Reload();
            _mgr.LayoutAll(); // the other tabs' icons step aside
        }

        // Switched between a normal fence and a folder portal.
        public void ModeChanged()
        {
            _browsePath = null;
            _scrollRow = 0;
            _iconOrder.Clear();
            RestartWatcher();
            ApplyAppearance();
            Reload();
        }

        // Dropping a fence (moved by its title) onto another one offers to merge them into tabs.
        FenceWindow? _mergeTarget;

        void SetMergeTarget(FenceWindow? target)
        {
            if (target == _mergeTarget) return;
            _mergeTarget?.SetMergeHighlight(false);
            _mergeTarget = target;
            if (target != null) target.SetMergeHighlight(true);
            else _cursorHint?.Hide();
        }

        // Drawn by the backdrop, under the icons: an accent frame and a light accent wash.
        public void SetMergeHighlight(bool on)
        {
            if (_glow == on) return;
            _glow = on;
            UpdateBackdrop();
        }

        void AskMerge(FenceWindow target)
        {
            string from = string.Join(", ", _mgr.TabsOf(Config).Select(t => $"\"{t.Title}\""));
            string into = string.Join(", ", _mgr.TabsOf(target.Config).Select(t => $"\"{t.Title}\""));
            if (Dialogs.Confirm("Gộp fence", $"Gộp {from} vào fence {into} thành tab?"))
            {
                _mgr.MergeInto(this, target);
                return;
            }
            // Declined: put the fence back where it was instead of leaving it on top of the other one.
            Native.SetWindowPos(Handle, IntPtr.Zero, _moveStartRect.Left, _moveStartRect.Top, 0, 0,
                Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
            SaveBounds();
        }

        static FenceConfig? TabAt(DependencyObject? d)
        {
            while (d != null)
            {
                if (d is FrameworkElement { Tag: FenceConfig tab }) return tab;
                d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
            }
            return null;
        }

        #endregion

        #region Items

        public void StopWatcher()
        {
            _watcher?.Dispose();
            _watcher = null;
        }

        // Normal fences are refreshed by the manager's Desktop watcher; only portals watch their own folder.
        public void RestartWatcher()
        {
            StopWatcher();
            if (!Config.IsPortal || !Directory.Exists(ItemFolder)) return;
            try
            {
                var w = new FileSystemWatcher(ItemFolder)
                {
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName |
                                   NotifyFilters.Attributes | NotifyFilters.LastWrite,
                };
                FileSystemEventHandler changed = (_, _) => Dispatcher.BeginInvoke(ScheduleReload);
                w.Created += changed;
                w.Deleted += changed;
                w.Changed += changed;
                w.Renamed += (_, _) => Dispatcher.BeginInvoke(ScheduleReload);
                w.Error += (_, _) => Dispatcher.BeginInvoke(() => { RestartWatcher(); ScheduleReload(); });
                w.EnableRaisingEvents = true;
                _watcher = w;
            }
            catch (Exception ex) { Log.Error(ex); }
        }

        public void ScheduleReload()
        {
            _reloadTimer.Stop();
            _reloadTimer.Start();
        }

        public void Reload()
        {
            if (Handle == IntPtr.Zero) return;
            if (IsDesktopFence)
            {
                if (_items.Count > 0) _items.Clear();
                RefreshIconOrder();
                _mgr.LayoutWindow(this);
                return;
            }
            string folder = ItemFolder;
            bool exists = Directory.Exists(folder);
            var fresh = new List<FileItem>();
            if (exists)
            {
                try
                {
                    foreach (var fsi in new DirectoryInfo(folder).EnumerateFileSystemInfos())
                        if ((fsi.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0)
                            fresh.Add(MakeItem(fsi));
                }
                catch (Exception ex) { Log.Error(ex); }
            }

            var sorted = Sort(fresh);
            if (Config.SortMode == SortMode.Custom && _browsePath == null)
            {
                var order = sorted.Select(i => i.FileName).ToList();
                if (!order.SequenceEqual(Config.Order, StringComparer.OrdinalIgnoreCase))
                {
                    Config.Order = order;
                    _mgr.SaveSoon();
                }
            }
            Sync(sorted);
            UpdatePanelMode();

            if (!exists && Config.IsPortal)
            {
                EmptyHint.Text = "Không tìm thấy thư mục:\n" + folder;
                EmptyHint.Visibility = Visibility.Visible;
            }
            else if (_items.Count == 0)
            {
                EmptyHint.Text = "Kéo thả file vào đây";
                EmptyHint.Visibility = Visibility.Visible;
            }
            else EmptyHint.Visibility = Visibility.Collapsed;
        }

        int IconSizeDip => Config.IconSize > 0 ? Config.IconSize : _mgr.DesktopIconSize;

        // One icon cell in DIPs: the desktop's own grid, so a folder portal lines up with normal fences and with
        // the desktop. A portal with its own icon size grows or shrinks the cell around the icon by as much.
        public static readonly DependencyProperty CellWidthProperty =
            DependencyProperty.Register(nameof(CellWidth), typeof(double), typeof(FenceWindow), new PropertyMetadata(76.0));
        public static readonly DependencyProperty CellHeightProperty =
            DependencyProperty.Register(nameof(CellHeight), typeof(double), typeof(FenceWindow), new PropertyMetadata(91.0));

        public double CellWidth
        {
            get => (double)GetValue(CellWidthProperty);
            set => SetValue(CellWidthProperty, value);
        }

        public double CellHeight
        {
            get => (double)GetValue(CellHeightProperty);
            set => SetValue(CellHeightProperty, value);
        }

        void UpdateCellSize()
        {
            double s = DpiScale;
            double extra = IconSizeDip - _mgr.DesktopIconSize;
            CellWidth = Math.Round(IconGrid.Cell.X / s + extra);
            CellHeight = Math.Round(IconGrid.Cell.Y / s + extra);
            if (ItemsHost is VirtualizingWrapPanel panel) panel.CellSize = new Size(CellWidth, CellHeight);
            // Picture and label exactly where Explorer draws them in a cell (see IconGrid.PictureDrop).
            double top = (IconGrid.Offset.Y + IconGrid.PictureDrop) / s;
            double gap = Math.Max(0, (IconGrid.LabelTop - IconGrid.PictureDrop) / s - _mgr.DesktopIconSize);
            Resources["IconPictureMargin"] = new Thickness(0, top, 0, gap);
            double font = IconGrid.LabelFontPx / s, line = Math.Ceiling(font * 1.33);
            Resources["IconFont"] = new FontFamily(IconGrid.LabelFontFamily);
            Resources["IconFontSize"] = font;
            Resources["IconLineHeight"] = line;
            Resources["IconLabelHeight"] = 2 * line;
        }

        // Labels as wide as their cell at most, as on the desktop.
        double LabelWidth => CellWidth;

        // Hundreds of items (a portal onto a big folder): switch to a panel that only builds what is in view.
        // Hysteresis so a folder hovering around the limit doesn't flip back and forth.
        ItemsPanelTemplate? _wrapPanel;
        bool _virtualized;

        void UpdatePanelMode()
        {
            bool want = _items.Count > 200 || (_virtualized && _items.Count > 150);
            if (want != _virtualized)
            {
                _wrapPanel ??= Items.ItemsPanel;
                _virtualized = want;
                Items.ItemsPanel = want ? new ItemsPanelTemplate(new FrameworkElementFactory(typeof(VirtualizingWrapPanel))) : _wrapPanel;
                VirtualizingPanel.SetIsVirtualizing(Items, want);
                ScrollViewer.SetCanContentScroll(Items, want);
                _itemsHost = null;
            }
            if (_virtualized)
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
                {
                    // Same cells as the regular panel.
                    if (ItemsHost is VirtualizingWrapPanel panel) panel.CellSize = new Size(CellWidth, CellHeight);
                });
        }

        string ItemFolder => Config.IsPortal ? _browsePath ?? Config.FolderPath : DesktopShell.DesktopPath;

        static FileItem MakeItem(FileSystemInfo fsi) => new(fsi.FullName)
        {
            IsFolder = fsi is DirectoryInfo,
            Modified = fsi.LastWriteTimeUtc,
            Size = fsi is FileInfo fi ? fi.Length : 0,
            Name = ShellInfo.GetDisplayNameCached(fsi.FullName, fsi.LastWriteTimeUtc, fsi.Name),
        };

        List<FileItem> Sort(List<FileItem> list)
        {
            IEnumerable<FileItem> q = Config.SortMode switch
            {
                SortMode.Name => list.OrderBy(i => !i.IsFolder).ThenBy(i => i.Name, Logical),
                SortMode.Type => list.OrderBy(i => !i.IsFolder)
                                     .ThenBy(i => i.Extension, StringComparer.OrdinalIgnoreCase)
                                     .ThenBy(i => i.Name, Logical),
                SortMode.Date => list.OrderByDescending(i => i.Modified),
                SortMode.Size => list.OrderBy(i => !i.IsFolder).ThenByDescending(i => i.Size),
                _ => SortCustom(list),
            };
            return q.ToList();
        }

        IEnumerable<FileItem> SortCustom(List<FileItem> list)
        {
            var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < Config.Order.Count; i++) index.TryAdd(Config.Order[i], i);
            // New files go to the end, in name order.
            return list.OrderBy(i => index.TryGetValue(i.FileName, out var k) ? k : int.MaxValue)
                       .ThenBy(i => i.Name, Logical);
        }

        // Update the collection in place so selection and scroll position survive a refresh.
        void Sync(List<FileItem> target)
        {
            UpdateCellSize();
            double size = IconSizeDip, label = LabelWidth;
            int px = (int)Math.Round(size * DpiScale);

            // Drop vanished items first: the remaining ones then mostly line up already, which keeps the
            // pass below linear even for a portal onto a folder with thousands of files.
            var keep = new HashSet<string>(target.Select(t => t.Path), StringComparer.OrdinalIgnoreCase);
            for (int k = _items.Count - 1; k >= 0; k--)
                if (!keep.Contains(_items[k].Path)) _items.RemoveAt(k);

            for (int i = 0; i < target.Count; i++)
            {
                var t = target[i];
                int j = IndexOfPath(t.Path, i);
                if (j < 0)
                {
                    t.IconSize = size;
                    t.ItemWidth = label;
                    _items.Insert(i, t);
                    RequestIcon(t, px);
                    continue;
                }
                if (j != i) _items.Move(j, i);
                var item = _items[i];
                bool needIcon = item.Modified != t.Modified || item.IconPx != px || item.Icon == null;
                item.Name = t.Name;
                item.Size = t.Size;
                item.IsFolder = t.IsFolder;
                item.Modified = t.Modified;
                item.IconSize = size;
                item.ItemWidth = label;
                if (needIcon) RequestIcon(item, px);
            }
            while (_items.Count > target.Count) _items.RemoveAt(_items.Count - 1);
        }

        int IndexOfPath(string path, int start)
        {
            for (int k = start; k < _items.Count; k++)
                if (string.Equals(_items[k].Path, path, StringComparison.OrdinalIgnoreCase)) return k;
            return -1;
        }

        void RequestIcon(FileItem item, int px)
        {
            item.IconPx = px;
            IconLoader.Request(item.Path, px, item.Modified.Ticks, img =>
                Dispatcher.BeginInvoke(() => { if (item.IconPx == px) item.Icon = img; }));
        }

        List<FileItem> SelectedItems() =>
            Items.SelectedItems.Cast<FileItem>().OrderBy(i => _items.IndexOf(i)).ToList();

        // Double-click / Enter: inside a folder portal a subfolder opens in place (Ctrl: in Explorer).
        void ActivateItem(FileItem item)
        {
            if (Config.IsPortal && item.IsFolder && !CtrlDown)
            {
                BrowseInto(item.Path);
                return;
            }
            OpenItem(item);
        }

        void OpenItem(FileItem item)
        {
            try
            {
                Process.Start(new ProcessStartInfo(item.Path)
                {
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(item.Path) ?? "",
                });
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { } // user cancelled UAC
            catch (Exception ex) { Dialogs.Info("Không mở được", ex.Message); }
        }

        // In-place rename, like Explorer: the label becomes an edit box (F2, the Rename verb, or a slow click).
        FileItem? _renamingItem;
        int _renameSelectLength;

        static bool HidesExtension(FileItem item)
        {
            string ext = Path.GetExtension(item.FileName);
            return !item.IsFolder && (ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase) ||
                                      ext.Equals(".url", StringComparison.OrdinalIgnoreCase));
        }

        void RenameItem(FileItem item)
        {
            EndItemRename(commit: true);
            string current = item.FileName;
            bool hiddenExt = HidesExtension(item);
            item.EditName = hiddenExt ? Path.GetFileNameWithoutExtension(current) : current;
            // Like Explorer: the name is selected without its extension.
            _renameSelectLength = item.IsFolder || hiddenExt ? item.EditName.Length : Path.GetFileNameWithoutExtension(current).Length;
            _renamingItem = item;
            Native.ForceForeground(Handle);
            Activate();
            Items.ScrollIntoView(item);
            item.IsRenaming = true; // RenameBox_IsVisibleChanged focuses the box
        }

        void EndItemRename(bool commit)
        {
            var item = _renamingItem;
            if (item == null) return;
            _renamingItem = null;
            item.IsRenaming = false;
            Items.Focus();
            if (commit) CommitRename(item, item.EditName.Trim());
        }

        void CommitRename(FileItem item, string name)
        {
            string current = item.FileName;
            string ext = Path.GetExtension(current);
            bool hiddenExt = HidesExtension(item);
            if (name.Length == 0 || name == (hiddenExt ? Path.GetFileNameWithoutExtension(current) : current)) return;
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                Dialogs.Info("Đổi tên", "Tên không được chứa các ký tự \\ / : * ? \" < > |");
                return;
            }
            if (hiddenExt && !name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) name += ext;

            if (FileOps.Rename(item.Path, Path.Combine(Path.GetDirectoryName(item.Path)!, name), Handle))
            {
                if (Config.IsPortal)
                {
                    int k = Config.Order.FindIndex(n => string.Equals(n, current, StringComparison.OrdinalIgnoreCase));
                    if (k >= 0) Config.Order[k] = name;
                }
                else _mgr.RenameMember(current, name);
            }
            ScheduleReload();
        }

        void RenameBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (sender is not TextBox box || !box.IsVisible) return;
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            {
                box.Focus();
                Keyboard.Focus(box);
                box.Select(0, Math.Min(_renameSelectLength, box.Text.Length));
            });
        }

        void RenameBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { EndItemRename(commit: true); e.Handled = true; }
            else if (e.Key == Key.Escape) { EndItemRename(commit: false); e.Handled = true; }
        }

        void RenameBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (_renamingItem != null && (sender as FrameworkElement)?.DataContext == _renamingItem) EndItemRename(commit: true);
        }

        // Explorer's "slow click": clicking the label of the only selected item again, without a double-click.
        readonly DispatcherTimer _slowClickTimer = new() { Interval = TimeSpan.FromMilliseconds(550) };
        FileItem? _slowClickItem;
        bool _wasOnlySelected;

        void ArmSlowClickRename(FileItem item)
        {
            _slowClickItem = item;
            _slowClickTimer.Stop();
            _slowClickTimer.Start();
        }

        void SlowClickTimer_Tick(object? sender, EventArgs e)
        {
            _slowClickTimer.Stop();
            var item = _slowClickItem;
            _slowClickItem = null;
            if (item != null && Items.SelectedItems.Count == 1 && Items.SelectedItem == item) RenameItem(item);
        }

        #endregion

        #region Search

        // Typing in a fence filters it: matches anywhere in the name, ignoring case and Vietnamese diacritics.
        string _filter = "";

        void Items_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            if (_renamingItem != null || e.OriginalSource is TextBox) return;
            if (string.IsNullOrEmpty(e.Text) || char.IsControl(e.Text[0])) return;
            SearchPanel.Visibility = Visibility.Visible;
            SearchBox.Focus();
            SearchBox.Text += e.Text;
            SearchBox.CaretIndex = SearchBox.Text.Length;
            e.Handled = true;
        }

        void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            _filter = TextMatch.Normalize(SearchBox.Text);
            var view = System.Windows.Data.CollectionViewSource.GetDefaultView(_items);
            view.Filter = _filter.Length == 0 ? null : o => o is FileItem f && TextMatch.Normalize(f.Name).Contains(_filter);
            if (Items.Items.Count > 0) Items.SelectedIndex = 0;
        }

        void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Escape:
                    ClearSearch();
                    Items.Focus();
                    e.Handled = true;
                    break;
                case Key.Enter:
                    if (Items.SelectedItem is FileItem first) OpenItem(first);
                    e.Handled = true;
                    break;
                case Key.Down:
                    Items.Focus();
                    if (Items.ItemContainerGenerator.ContainerFromIndex(Math.Max(0, Items.SelectedIndex)) is ListBoxItem lbi) lbi.Focus();
                    e.Handled = true;
                    break;
            }
        }

        void SearchBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            // Keep the filter while working in the list; drop the box when the search is empty.
            if (SearchBox.Text.Length == 0) SearchPanel.Visibility = Visibility.Collapsed;
        }

        public void ClearSearch()
        {
            if (SearchBox.Text.Length > 0) SearchBox.Text = "";
            SearchPanel.Visibility = Visibility.Collapsed;
        }

        bool IsFiltering => _filter.Length > 0;

        #endregion

        #region Reveal

        // Selects an item and flashes it (used by the global search's "show in fence").
        public void RevealItem(string path)
        {
            ClearSearch();
            if (IsDesktopFence)
            {
                RevealIcon(FenceManager.MemberName(path));
                return;
            }
            if (Config.IsPortal)
            {
                var parent = Path.GetDirectoryName(path.TrimEnd('\\'));
                if (parent != null && !FenceManager.PathEquals(parent, ItemFolder) && IsSameOrUnder(parent, Config.FolderPath))
                    BrowseInto(parent);
            }
            Reload();
            if (Config.RolledUp && !_peeking)
            {
                _peeking = true;
                ApplyBounds(animate: true);
            }
            var item = _items.FirstOrDefault(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase));
            if (item == null) return;
            Items.SelectedItem = item;
            Items.ScrollIntoView(item);
            item.IsDropTarget = true; // the drop-target highlight doubles as a brief "here it is" flash
            var flash = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
            flash.Tick += (_, _) => { flash.Stop(); item.IsDropTarget = false; };
            flash.Start();
        }

        void RevealIcon(string name)
        {
            RefreshIconOrder();
            if (Config.RolledUp && !_peeking)
            {
                // Unfold for a while; the usual hover check folds it again once the mouse is elsewhere.
                _peeking = true;
                ApplyBounds();
                _peekTimer.Start();
            }
            int index = _iconOrder.FindIndex(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
            if (index < 0) return;
            var (cols, rows, _, _) = IconGridOf(ScreenRect);
            int row = index / cols;
            if (row < _scrollRow) _scrollRow = row;
            else if (rows > 0 && row >= _scrollRow + rows) _scrollRow = row - rows + 1;
            _mgr.LayoutWindow(this);
            DesktopIcons.Select(name);
            SetSpot(name); // a brief "here it is" glow under the icon
            var flash = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
            flash.Tick += (_, _) => { flash.Stop(); SetSpot(null); };
            flash.Start();
        }

        #endregion

        #region Portal browsing

        // A folder portal can be browsed into: double-clicking a subfolder opens it inside the fence.
        string? _browsePath;

        void BrowseInto(string folder)
        {
            _browsePath = FenceManager.PathEquals(folder, Config.FolderPath) ? null : folder;
            ClearSearch();
            Items.UnselectAll();
            RestartWatcher();
            ApplyAppearance();
            Reload();
        }

        void BrowseBack()
        {
            if (_browsePath == null) return;
            var parent = Path.GetDirectoryName(_browsePath.TrimEnd('\\'));
            BrowseInto(parent != null && IsSameOrUnder(parent, Config.FolderPath) ? parent : Config.FolderPath);
        }

        static bool IsSameOrUnder(string path, string root) =>
            (Path.GetFullPath(path).TrimEnd('\\') + "\\").StartsWith(Path.GetFullPath(root).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

        void BackButton_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            BrowseBack();
            e.Handled = true; // not a title-bar drag
        }

        string DisplayTitle =>
            _browsePath == null ? Config.Title
                                : Config.Title + "  ›  " + Path.GetRelativePath(Config.FolderPath, _browsePath).Replace("\\", "  ›  ");

        void CopyToClipboard(bool cut) => FenceManager.PutOnClipboard(SelectedItems().Select(i => i.Path).ToList(), cut);

        void DeleteItems(List<FileItem> items, bool permanent)
        {
            var paths = items.Select(i => i.Path).ToList();
            if (paths.Count == 0) return;
            FileOps.Delete(paths, Handle, permanent);
            ScheduleReload();
        }

        void Paste()
        {
            if (!Directory.Exists(ItemFolder)) return;
            var data = Clipboard.GetDataObject();
            if (data?.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0) return;
            bool move = false;
            if (data.GetData("Preferred DropEffect") is MemoryStream ms && ms.Length >= 4)
            {
                var bytes = new byte[4];
                ms.Read(bytes, 0, 4);
                move = (BitConverter.ToInt32(bytes, 0) & 2) != 0;
            }
            if (!Config.IsPortal)
            {
                _mgr.ImportToDesktop(Config, files, copy: !move, null, Handle);
                if (move) Clipboard.Clear();
            }
            else if (move)
            {
                var list = files.Where(f => !InFolder(f)).ToArray();
                if (FileOps.Move(list, ItemFolder, Handle)) Clipboard.Clear();
                _mgr.ForgetMissing(list);
            }
            else FileOps.Copy(files, ItemFolder, Handle, renameOnCollision: files.Any(InFolder));
            ScheduleReload();
        }

        bool InFolder(string path) => FenceManager.PathEquals(Path.GetDirectoryName(path.TrimEnd('\\')), ItemFolder);

        #endregion

        #region Mouse & keyboard

        void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_renaming) return;
            if (e.ClickCount == 2)
            {
                ToggleRollUp();
                e.Handled = true;
                return;
            }
            if (!Config.Locked)
            {
                // The stand-in icons go up as soon as the title is pressed, and the move starts only once they are on
                // screen and the real ones gone: a dragged fence never shows its icons twice. The move is anchored
                // where the title was pressed, so the fence catches up with the mouse after the wait.
                _movedOnce = false;
                Native.GetCursorPos(out _pressCursor);
                _pressRect = ScreenRect;
                _pressAnchored = true;
                BeginLift(moving: true);
                try { if (WaitForLift(TitleBar)) DragMove(); } catch (InvalidOperationException) { }
                finally { _pressAnchored = false; EndLift(); }
            }
        }

        void TitleBar_MouseEnter(object sender, MouseEventArgs e)
        {
            if (!Config.Locked && !_renaming) PrepareLift();
        }

        void Items_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var source = e.OriginalSource as DependencyObject;
            var lbi = FindAncestor<ListBoxItem>(source);
            _pendingSelect = null;
            _maybeDrag = false;

            if (lbi == null)
            {
                if (FindAncestor<ScrollBar>(source) == null) BeginMarquee(e);
                return;
            }
            if (e.ClickCount == 2)
            {
                _slowClickTimer.Stop();
                if (!_mgr.Config.SingleClickOpen && lbi.DataContext is FileItem it) ActivateItem(it);
                e.Handled = true;
                return;
            }
            _dragStart = e.GetPosition(this);
            _maybeDrag = true;
            _wasOnlySelected = lbi.IsSelected && Items.SelectedItems.Count == 1;

            // Pressing on an already-selected item must not collapse a multi-selection before a drag.
            if (lbi.IsSelected && Keyboard.Modifiers == ModifierKeys.None && Items.SelectedItems.Count > 1)
            {
                _pendingSelect = lbi;
                lbi.Focus();
                e.Handled = true;
            }
        }

        void Items_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (_marqueeActive)
            {
                UpdateMarquee(e);
                e.Handled = true;
                return;
            }
            if (!_maybeDrag || e.LeftButton != MouseButtonState.Pressed) return;
            var p = e.GetPosition(this);
            if (Math.Abs(p.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(p.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            _maybeDrag = false;
            _pendingSelect = null;
            StartDrag();
        }

        void Items_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_marqueeActive)
            {
                EndMarquee();
                e.Handled = true;
                return;
            }
            var lbi = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
            if (_pendingSelect != null && lbi == _pendingSelect) Items.SelectedItem = lbi.DataContext;
            _pendingSelect = null;
            if (_maybeDrag && _mgr.Config.SingleClickOpen && Keyboard.Modifiers == ModifierKeys.None &&
                lbi?.DataContext is FileItem it)
                ActivateItem(it);
            else if (_maybeDrag && _wasOnlySelected && Keyboard.Modifiers == ModifierKeys.None &&
                     e.OriginalSource is TextBlock && lbi?.DataContext is FileItem slow)
                ArmSlowClickRename(slow);
            _maybeDrag = false;
        }

        // Rubber-band selection, started from empty space inside the fence (like on the desktop).
        bool _marqueeActive, _marqueeMoved;
        Point _marqueeStart;                 // in items-panel coordinates, so it scrolls with the items
        HashSet<object> _marqueeBase = new(); // selection before the drag, kept when Ctrl is held
        Panel? _itemsHost;

        Panel? ItemsHost => _itemsHost ??= FindItemsHost(Items);

        static Panel? FindItemsHost(DependencyObject parent)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is Panel { IsItemsHost: true } host) return host;
                if (FindItemsHost(child) is Panel found) return found;
            }
            return null;
        }

        static bool CtrlDown => (Keyboard.Modifiers & ModifierKeys.Control) != 0;

        public void ClearSelection()
        {
            if (Items.SelectedItems.Count > 0) Items.UnselectAll();
        }

        void BeginMarquee(MouseButtonEventArgs e)
        {
            if (!CtrlDown) Items.UnselectAll();
            if (ItemsHost is not Panel host) return;
            _marqueeActive = true;
            _marqueeMoved = false;
            _marqueeStart = e.GetPosition(host);
            _marqueeBase = new HashSet<object>(Items.SelectedItems.Cast<object>());
            Items.Focus();
            Items.CaptureMouse();
            e.Handled = true;
        }

        void UpdateMarquee(MouseEventArgs e)
        {
            if (ItemsHost is not Panel host) return;
            var current = e.GetPosition(host);
            if (!_marqueeMoved && Math.Abs(current.X - _marqueeStart.X) < 3 && Math.Abs(current.Y - _marqueeStart.Y) < 3)
                return;
            _marqueeMoved = true;

            // Dragging past the top or bottom edge scrolls the fence.
            double y = e.GetPosition(Items).Y;
            if (FindChild<ScrollViewer>(Items) is ScrollViewer sv && (y < 0 || y > Items.ActualHeight))
            {
                if (y < 0) sv.LineUp(); else sv.LineDown();
                sv.UpdateLayout();
                current = e.GetPosition(host);
            }

            var band = new Rect(_marqueeStart, current);
            bool toggle = CtrlDown;
            foreach (var item in _items)
            {
                if (Items.ItemContainerGenerator.ContainerFromItem(item) is not ListBoxItem c) continue;
                var bounds = c.TransformToAncestor(host).TransformBounds(new Rect(c.RenderSize));
                bool hit = band.IntersectsWith(bounds);
                bool before = _marqueeBase.Contains(item);
                bool want = toggle ? before ^ hit : before || hit;
                if (c.IsSelected != want) c.IsSelected = want;
            }

            var a = host.TranslatePoint(_marqueeStart, MarqueeLayer);
            var b = host.TranslatePoint(current, MarqueeLayer);
            var r = new Rect(a, b);
            Canvas.SetLeft(Marquee, r.X);
            Canvas.SetTop(Marquee, r.Y);
            Marquee.Width = r.Width;
            Marquee.Height = r.Height;
            Marquee.Visibility = Visibility.Visible;
        }

        void EndMarquee()
        {
            if (!_marqueeActive) return;
            _marqueeActive = false; // first: releasing capture below re-enters through LostMouseCapture
            Marquee.Visibility = Visibility.Collapsed;
            _marqueeBase.Clear();
            Items.ReleaseMouseCapture();
        }

        void Items_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.OriginalSource is TextBox) return;
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            var sel = SelectedItems();
            switch (e.Key)
            {
                case Key.Enter:
                    foreach (var it in sel) ActivateItem(it);
                    break;
                case Key.Back:
                    BrowseBack();
                    break;
                case Key.Escape when IsFiltering:
                    ClearSearch();
                    break;
                case Key.Delete:
                    DeleteItems(sel, (Keyboard.Modifiers & ModifierKeys.Shift) != 0);
                    break;
                case Key.F2:
                    if (sel.Count == 1) RenameItem(sel[0]);
                    break;
                case Key.F5:
                    Reload();
                    break;
                case Key.C when ctrl:
                    CopyToClipboard(false);
                    break;
                case Key.X when ctrl:
                    CopyToClipboard(true);
                    break;
                case Key.V when ctrl:
                    Paste();
                    break;
                case Key.Z when ctrl:
                    _mgr.Undo();
                    break;
                default:
                    return;
            }
            e.Handled = true;
        }

        void Window_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_renaming) return;
            e.Handled = true;
            Activate();
            if (TabAt(e.OriginalSource as DependencyObject) is FenceConfig tab) SwitchTab(tab);
            var lbi = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
            if (lbi?.DataContext is FileItem item)
            {
                if (!lbi.IsSelected) Items.SelectedItem = item;
                ShowItemMenu();
            }
            else ShowFenceMenu();
        }

        void Window_MouseEnter(object sender, MouseEventArgs e)
        {
            _peekTimer.Stop();
            if (Config.RolledUp && !_peeking && !_mgr.Hidden)
            {
                _peeking = true;
                ApplyBounds(animate: true);
            }
        }

        void PeekTimer_Tick(object? sender, EventArgs e)
        {
            if (_dragOver || _dragMode || _renaming || _menuOpen || _inSizeMove) return;
            Native.GetCursorPos(out var pt);
            var r = ScreenRect;
            if (pt.X >= r.Left && pt.X < r.Right && pt.Y >= r.Top && pt.Y < r.Bottom) return;
            _peekTimer.Stop();
            if (_peeking)
            {
                _peeking = false;
                ApplyBounds(animate: true);
            }
        }

        public void ToggleRollUp()
        {
            Config.RolledUp = !Config.RolledUp;
            _peeking = false;
            ApplyBounds(animate: true);
            _mgr.SyncGroup(Config);
            _mgr.SaveSoon();
        }

        static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
        {
            while (d != null)
            {
                if (d is T t) return t;
                d = d is Visual || d is System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(d)
                    : LogicalTreeHelper.GetParent(d);
            }
            return null;
        }

        #endregion

        #region Drag & drop

        // Dragging items out of a folder portal (normal fences hold real desktop icons: Explorer drags those).
        void StartDrag()
        {
            var paths = SelectedItems().Select(i => i.Path).ToArray();
            if (paths.Length == 0) return;
            Mouse.Capture(null);
            _dragSource = Config;
            try
            {
                // The shell's own drag (icons under the cursor, "Move to ..." labels); WPF's as a fallback.
                if (!ShellContextMenu.DoDragDrop(Handle, paths, () => false))
                {
                    var data = new DataObject();
                    data.SetData(DataFormats.FileDrop, paths);
                    DragDrop.DoDragDrop(Items, data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
                }
            }
            catch (COMException ex) { Log.Error(ex); }
            finally { _dragSource = null; }
            _mgr.ForgetMissing(paths);
            ScheduleReload();
        }

        // The fence tab a drag started from (drags are modal, so there is at most one).
        static FenceConfig? _dragSource;
        // Files of the drag currently over this fence, read once on enter (each read is a cross-process call).
        string[]? _dragFiles;
        bool _dragShellItemsOnly; // Recycle Bin, This PC, Start-menu apps: no file path, only a shell ID list
        bool _dragInside, _leavePending;

        bool IsFromSelf => _dragSource == Config;

        // Facts about the current drag that only change with the tab under it; DragOver fires on every
        // mouse move, so they are worked out once instead of re-resolving every dragged path each time.
        FenceConfig? _dragInfoTab;
        bool _dragFolderExists, _dragAllInFolder, _dragSameVolume;

        void EnsureDragInfo()
        {
            if (_dragInfoTab == Config) return;
            _dragInfoTab = Config;
            string folder = ItemFolder;
            _dragFolderExists = Directory.Exists(folder);
            var files = _dragFiles;
            // For a normal fence "in the folder" means already a desktop icon (user's or public Desktop).
            _dragAllInFolder = files is { Length: > 0 } && files.All(f => IsDesktopFence ? FenceManager.IsDesktopItem(f) : InFolder(f));
            _dragSameVolume = files is { Length: > 0 } &&
                              string.Equals(Path.GetPathRoot(files[0]), Path.GetPathRoot(folder), StringComparison.OrdinalIgnoreCase);
        }

        DragDropEffects ComputeEffect(DragEventArgs e)
        {
            EnsureDragInfo();
            if (!_dragFolderExists) return DragDropEffects.None;
            if (_dragShellItemsOnly)
                return e.AllowedEffects.HasFlag(DragDropEffects.Link) ? DragDropEffects.Link : DragDropEffects.Copy;

            var dropped = _dragFiles;
            if (dropped == null || dropped.Length == 0) return DragDropEffects.None;
            if (IsFromSelf) return DragDropEffects.Move;
            // Desktop icons only change fence (or place).
            if (IsDesktopFence && _dragAllInFolder) return DragDropEffects.Move;

            var allowed = e.AllowedEffects;
            if ((e.KeyStates & DragDropKeyStates.ControlKey) != 0 && allowed.HasFlag(DragDropEffects.Copy))
                return DragDropEffects.Copy;
            if ((e.KeyStates & DragDropKeyStates.ShiftKey) != 0 && allowed.HasFlag(DragDropEffects.Move))
                return DragDropEffects.Move;

            // Same rule as Explorer: move within a drive, copy across drives.
            if (_dragSameVolume && allowed.HasFlag(DragDropEffects.Move)) return DragDropEffects.Move;
            if (allowed.HasFlag(DragDropEffects.Copy)) return DragDropEffects.Copy;
            return allowed.HasFlag(DragDropEffects.Move) ? DragDropEffects.Move : DragDropEffects.None;
        }

        // WPF raises DragEnter/DragLeave every time the cursor crosses a child element;
        // only the real enter/leave of the fence matters.
        void Window_DragEnter(object sender, DragEventArgs e)
        {
            _leavePending = false;
            if (!_dragInside)
            {
                _dragInside = true;
                _dragOver = true;
                _peekTimer.Stop();
                _dragInfoTab = null;
                try
                {
                    _dragFiles = e.Data.GetData(DataFormats.FileDrop) as string[];
                    _dragShellItemsOnly = _dragFiles == null && e.Data.GetDataPresent(VirtualItems.IdListFormat);
                }
                catch (COMException)
                {
                    _dragFiles = null;
                    _dragShellItemsOnly = false;
                }
                if (Config.RolledUp && !_peeking)
                {
                    _peeking = true;
                    ApplyBounds(animate: true);
                }
                e.Effects = ComputeEffect(e);
                DropTargetHelper.Enter(Handle, e.Data, e.Effects);
            }
            else e.Effects = ComputeEffect(e);
            SetDropGlow(_itemTarget == null && !IsFromSelf && e.Effects != DragDropEffects.None);
            e.Handled = true;
        }

        void Window_DragOver(object sender, DragEventArgs e)
        {
            _leavePending = false;
            // Hovering a tab of a folder portal while dragging opens it. A normal fence keeps its tab: switching
            // would move desktop icons while Explorer is still dragging (it stalls and scatters them), and a drag
            // merely passing over the title would leave another tab open. Dropping on a tab still targets it.
            if (!IsDesktopFence && TabAt(InputHitTest(e.GetPosition(this)) as DependencyObject) is FenceConfig tab && tab != Config)
                SwitchTab(tab);

            SetItemTarget(ItemTargetAt(e), e);
            if (_itemTarget != null)
            {
                uint effect = (uint)e.AllowedEffects;
                _itemTarget.DragOver((uint)e.KeyStates, CursorPoint(), ref effect);
                e.Effects = (DragDropEffects)effect;
            }
            else e.Effects = ComputeEffect(e);
            // The fence lights up when the drop goes into it; over a folder icon only that folder does.
            SetDropGlow(_itemTarget == null && !IsFromSelf && e.Effects != DragDropEffects.None);
            DropTargetHelper.Over(e.Effects);
            e.Handled = true;
        }

        void SetDropGlow(bool on) => SetMergeHighlight(on);

        // Dropping onto a folder (zip, program...) inside the fence goes into that item, exactly as in
        // Explorer: its own shell drop handler does the work. The middle of an icon targets the item;
        // the gaps between icons keep meaning "into the fence / reorder".
        ShellContextMenu.IDropTarget? _itemTarget;
        string? _itemTargetPath;
        FileItem? _itemTargetItem;

        string? ItemTargetAt(DragEventArgs e)
        {
            string? path;
            if (IsDesktopFence)
            {
                path = MemberAt(CursorPoint()) is string name ? DesktopShell.MemberPath(name) : null;
            }
            else
            {
                var lbi = FindAncestor<ListBoxItem>(Items.InputHitTest(e.GetPosition(Items)) as DependencyObject);
                if (lbi?.DataContext is not FileItem item) return null;
                if (Math.Abs(e.GetPosition(lbi).X - lbi.ActualWidth / 2) > lbi.ActualWidth * 0.3) return null;
                path = item.Path;
            }
            if (path == null) return null;
            if (_dragFiles?.Any(f => string.Equals(f.TrimEnd('\\'), path, StringComparison.OrdinalIgnoreCase)) == true)
                return null; // an item cannot be dropped onto itself
            return path;
        }

        void SetItemTarget(string? path, DragEventArgs e)
        {
            if (string.Equals(path, _itemTargetPath, StringComparison.OrdinalIgnoreCase)) return;
            ClearItemTarget();
            if (path == null || e.Data is not System.Runtime.InteropServices.ComTypes.IDataObject data) return;
            _itemTargetPath = path; // remembered even when the item takes no drops, so we don't ask again
            var target = ShellContextMenu.GetDropTarget(Handle, path);
            if (target == null) return;
            uint effect = (uint)e.AllowedEffects;
            if (target.DragEnter(data, (uint)e.KeyStates, CursorPoint(), ref effect) != 0)
            {
                Marshal.ReleaseComObject(target);
                return;
            }
            _itemTarget = target;
            if (IsDesktopFence) SetSpot(FenceManager.MemberName(path));
            else
            {
                _itemTargetItem = _items.FirstOrDefault(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase));
                if (_itemTargetItem != null) _itemTargetItem.IsDropTarget = true;
            }
        }

        void ClearItemTarget(bool dropped = false)
        {
            if (_itemTarget != null)
            {
                if (!dropped) _itemTarget.DragLeave();
                Marshal.ReleaseComObject(_itemTarget);
                _itemTarget = null;
            }
            if (_itemTargetItem != null) _itemTargetItem.IsDropTarget = false;
            _itemTargetItem = null;
            _itemTargetPath = null;
            SetSpot(null);
        }

        static Native.POINT CursorPoint()
        {
            Native.GetCursorPos(out var pt);
            return pt;
        }

        // Crossing from one child element to another raises DragLeave immediately followed by DragEnter.
        // A real leave (cursor out of the fence, Esc, or a refused drop) is not followed by anything,
        // so wait one dispatcher turn before treating it as such; the shell must always be told, or the
        // drag image stays painted on the fence.
        void Window_DragLeave(object sender, DragEventArgs e)
        {
            _leavePending = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            {
                if (!_leavePending || !_dragInside) return;
                _leavePending = false;
                DropTargetHelper.Leave();
                EndDragOver();
            });
        }

        void EndDragOver()
        {
            ClearItemTarget();
            SetDropGlow(false);
            _dragInside = false;
            _dragOver = false;
            _dragFiles = null;
            _dragShellItemsOnly = false;
            _dragInfoTab = null;
            if (_peeking) _peekTimer.Start();
        }

        void Window_Drop(object sender, DragEventArgs e)
        {
            e.Handled = true;
            _leavePending = false;
            var at = CursorPoint();

            if (_itemTarget != null && e.Data is System.Runtime.InteropServices.ComTypes.IDataObject data)
            {
                uint dropEffect = (uint)e.AllowedEffects;
                _itemTarget.Drop(data, (uint)e.KeyStates, at, ref dropEffect);
                e.Effects = (DragDropEffects)dropEffect;
                ClearItemTarget(dropped: true);
                DropTargetHelper.Drop(e.Data, e.Effects);
                EndDragOver();
                ScheduleReload();
                return;
            }

            var effect = ComputeEffect(e);
            var files = _dragFiles;
            bool shellItems = _dragShellItemsOnly;
            bool allOnDesktop = _dragAllInFolder;
            DropTargetHelper.Drop(e.Data, effect);
            EndDragOver();
            int index = DropIndex(e);

            if (shellItems && effect != DragDropEffects.None)
            {
                // The data object is only valid during the drop: copy the item IDs out now.
                var pidls = VirtualItems.ReadIdList(e.Data);
                e.Effects = IsDesktopFence ? DragDropEffects.None : DragDropEffects.Link;
                Dispatcher.BeginInvoke(() =>
                {
                    try { _mgr.AddShellItems(Config, pidls, at); }
                    finally { foreach (var p in pidls) Marshal.FreeCoTaskMem(p); }
                    ScheduleReload();
                });
                return;
            }

            if (effect == DragDropEffects.None || files == null || files.Length == 0)
            {
                e.Effects = DragDropEffects.None;
                return;
            }
            if (IsFromSelf)
            {
                if (!IsFiltering && _browsePath == null) Reorder(files, index);
                e.Effects = DragDropEffects.None;
                return;
            }

            // We perform the operation ourselves; reporting "none" keeps the source from deleting anything.
            e.Effects = effect == DragDropEffects.Copy ? DragDropEffects.Copy : DragDropEffects.None;
            bool copy = effect == DragDropEffects.Copy;

            // Run after the drop returns so the drag source (often Explorer) is not blocked.
            if (IsDesktopFence)
            {
                // Dropped on the header of another tab: into that tab (it is opened afterwards).
                var tab = TabAt(InputHitTest(e.GetPosition(this)) as DependencyObject) ?? Config;
                var point = tab == Config ? at : (Native.POINT?)null;
                _mgr.Checkpoint($"Đưa mục vào \"{tab.Title}\"");
                if (allOnDesktop)
                {
                    var names = files.Select(FenceManager.MemberName).ToList();
                    Dispatcher.BeginInvoke(() => { _mgr.AddMembers(tab, names, point); if (tab != Config) SwitchTab(tab); });
                }
                else Dispatcher.BeginInvoke(() => { _mgr.ImportToDesktop(tab, files, copy, point, Handle); if (tab != Config) SwitchTab(tab); });
                return;
            }

            var list = files.Where(f => !InFolder(f)).ToArray();
            if (list.Length == 0) return;
            var moved = list.Select(f => Path.GetFileName(f.TrimEnd('\\'))).ToList();
            string dest = ItemFolder;
            Dispatcher.BeginInvoke(() =>
            {
                bool ok = copy ? FileOps.Copy(list, dest, Handle) : FileOps.Move(list, dest, Handle);
                if (!copy) _mgr.ForgetMissing(list);
                if (ok && Config.SortMode == SortMode.Custom) InsertIntoOrder(moved, index);
                ScheduleReload();
            });
        }

        int DropIndex(DragEventArgs e)
        {
            var hit = Items.InputHitTest(e.GetPosition(Items)) as DependencyObject;
            var lbi = FindAncestor<ListBoxItem>(hit);
            if (lbi == null) return _items.Count;
            int i = Items.ItemContainerGenerator.IndexFromContainer(lbi);
            if (i < 0) return _items.Count;
            return e.GetPosition(lbi).X > lbi.ActualWidth / 2 ? i + 1 : i;
        }

        void Reorder(string[] files, int index)
        {
            var current = _items.Select(i => i.FileName).ToList();
            var moving = current.Where(n => files.Any(f => string.Equals(Path.GetFileName(f), n, StringComparison.OrdinalIgnoreCase))).ToList();
            if (moving.Count == 0) return;
            int insertAt = index - current.Take(Math.Min(index, current.Count)).Count(moving.Contains);
            current.RemoveAll(moving.Contains);
            current.InsertRange(Math.Clamp(insertAt, 0, current.Count), moving);
            Config.Order = current;
            Config.SortMode = SortMode.Custom;
            _mgr.SaveSoon();
            Reload();
        }

        void InsertIntoOrder(List<string> names, int index)
        {
            var order = _items.Select(i => i.FileName).ToList();
            order.RemoveAll(n => names.Contains(n, StringComparer.OrdinalIgnoreCase));
            order.InsertRange(Math.Clamp(index, 0, order.Count), names);
            Config.Order = order;
            _mgr.SaveSoon();
        }

        #endregion

        #region Menus & rename

        void ShowItemMenu()
        {
            var sel = SelectedItems();
            if (sel.Count == 0) return;
            Native.GetCursorPos(out var pt);
            var extra = new List<(int, string)> { (CmdMoveToDesktop, "Đưa về Desktop") };
            int cmd = 0;
            _menuOpen = true;
            try { cmd = ShellContextMenu.Show(Handle, sel.Select(i => i.Path).ToList(), pt.X, pt.Y, extra); }
            catch (Exception ex) { Log.Error(ex); }
            finally { _menuOpen = false; }

            switch (cmd)
            {
                case CmdMoveToDesktop:
                    FileOps.Move(sel.Select(i => i.Path), DesktopShell.DesktopPath, Handle);
                    break;
                case ShellContextMenu.CmdRename:
                    RenameItem(sel[0]);
                    break;
                case ShellContextMenu.CmdCopy:
                    CopyToClipboard(false);
                    break;
                case ShellContextMenu.CmdCut:
                    CopyToClipboard(true);
                    break;
                case ShellContextMenu.CmdDelete:
                    DeleteItems(sel, Native.IsKeyDown(Native.VK_SHIFT));
                    break;
            }
            ScheduleReload();
        }

        void ShowFenceMenu()
        {
            var menu = new ContextMenu();
            var tabs = _mgr.TabsOf(Config);
            bool multi = tabs.Count > 1;
            if (_mgr.UndoDescription is string undo) menu.Items.Add(Item($"Hoàn tác: {undo}  (Ctrl+Z)", _mgr.Undo));
            menu.Items.Add(Item("Tạo fence mới", () => _mgr.CreateFenceAtCursor()));
            menu.Items.Add(Item(multi ? "Đổi tên tab" : "Đổi tên fence", BeginRename));
            if (Clipboard.ContainsFileDropList()) menu.Items.Add(Item("Dán", Paste));
            menu.Items.Add(new Separator());

            menu.Items.Add(Item("Thêm tab mới", () => _mgr.AddTab(this)));
            if (multi) menu.Items.Add(Item("Tách tab này thành fence riêng", () => _mgr.DetachTab(this)));
            var others = _mgr.OtherWindows(this);
            if (others.Count > 0)
            {
                var merge = Sub(multi ? "Gộp các tab này vào fence" : "Gộp vào fence (thành tab)");
                foreach (var other in others)
                {
                    var target = other;
                    merge.Items.Add(Item(string.Join("  |  ", _mgr.TabsOf(target.Config).Select(t => t.Title)),
                        () => _mgr.MergeInto(this, target)));
                }
                menu.Items.Add(merge);
            }
            menu.Items.Add(new Separator());

            var color = Sub("Màu nền");
            color.Items.Add(Check("Theo Windows (sáng / tối)", Config.Color == "auto", () => { Config.Color = "auto"; Changed(); }));
            color.Items.Add(Check("Màu nhấn Windows", Config.Color == "accent", () => { Config.Color = "accent"; Changed(); }));
            color.Items.Add(new Separator());
            foreach (var (name, hex) in ColorPresets)
                color.Items.Add(Check(name, string.Equals(Config.Color, hex, StringComparison.OrdinalIgnoreCase),
                    () => { Config.Color = hex; Changed(); }));
            color.Items.Add(new Separator());
            color.Items.Add(Item("Màu khác…", PickColor));
            menu.Items.Add(color);

            var opacity = Sub("Độ đậm nền");
            foreach (var v in new[] { 0.1, 0.2, 0.3, 0.4, 0.5, 0.6, 0.7, 0.8, 0.9 })
                opacity.Items.Add(Check($"{v * 100:0}%", Math.Abs(Config.Opacity - v) < 0.01,
                    () => { Config.Opacity = v; Changed(); }));
            menu.Items.Add(opacity);

            // Normal fences show the desktop's own icons, sized with the desktop (View menu, Ctrl+wheel).
            if (Config.IsPortal)
            {
                var icons = Sub("Kích thước icon");
                icons.Items.Add(Check($"Giống desktop ({_mgr.DesktopIconSize}px)", Config.IconSize == 0,
                    () => { Config.IconSize = 0; Changed(); Reload(); }));
                icons.Items.Add(new Separator());
                foreach (var (name, size) in new[] { ("Nhỏ", 32), ("Vừa", 48), ("Lớn", 64), ("Rất lớn", 96) })
                    icons.Items.Add(Check(name, Config.IconSize == size,
                        () => { Config.IconSize = size; Changed(); Reload(); }));
                menu.Items.Add(icons);
            }

            var sort = Sub("Sắp xếp theo");
            foreach (var (name, mode) in new[] { ("Thủ công (kéo thả)", SortMode.Custom), ("Tên", SortMode.Name),
                                                 ("Loại", SortMode.Type), ("Ngày sửa", SortMode.Date), ("Kích thước", SortMode.Size) })
                sort.Items.Add(Check(name, Config.SortMode == mode,
                    () => { Config.SortMode = mode; _mgr.SaveSoon(); Reload(); }));
            menu.Items.Add(sort);

            var rules = Sub("Quy tắc tự sắp xếp");
            foreach (var (name, patterns) in Rules.Presets)
            {
                bool on = patterns.All(p => Config.Rules.Contains(p, StringComparer.OrdinalIgnoreCase));
                rules.Items.Add(Check(name, on, () =>
                {
                    if (on) Config.Rules.RemoveAll(r => patterns.Contains(r, StringComparer.OrdinalIgnoreCase));
                    else Config.Rules.AddRange(patterns.Where(p => !Config.Rules.Contains(p, StringComparer.OrdinalIgnoreCase)));
                    _mgr.SaveSoon();
                }));
            }
            rules.Items.Add(new Separator());
            rules.Items.Add(Item("Tùy chỉnh…", EditRules));
            if (Config.Rules.Count > 0)
                rules.Items.Add(Item("Xóa tất cả quy tắc", () => { Config.Rules.Clear(); _mgr.SaveSoon(); }));
            rules.Items.Add(new Separator());
            rules.Items.Add(Item("Áp dụng ngay cho Desktop", () => _mgr.TidyDesktop(null, quiet: false)));
            menu.Items.Add(rules);

            menu.Items.Add(new Separator());
            if (Config.IsPortal) menu.Items.Add(Item("Mở thư mục trong Explorer", OpenFolder));
            menu.Items.Add(Item(Config.IsPortal ? "Đổi thư mục của portal…" : "Chuyển thành Folder Portal…", ChoosePortal));
            menu.Items.Add(new Separator());
            menu.Items.Add(Check("Khóa vị trí và kích thước", Config.Locked, () => { Config.Locked = !Config.Locked; Changed(); }));
            menu.Items.Add(Check("Thu gọn (chỉ hiện tiêu đề)", Config.RolledUp, ToggleRollUp));
            menu.Items.Add(new Separator());
            if (multi)
            {
                menu.Items.Add(Item("Xóa tab này", () => _mgr.RemoveFence(this, wholeFence: false)));
                menu.Items.Add(Item($"Xóa cả fence ({tabs.Count} tab)", () => _mgr.RemoveFence(this, wholeFence: true)));
            }
            else menu.Items.Add(Item("Xóa fence", () => _mgr.RemoveFence(this, wholeFence: true)));

            menu.PlacementTarget = this;
            menu.Placement = PlacementMode.MousePoint;
            menu.Closed += (_, _) => _menuOpen = false;
            _menuOpen = true;
            menu.IsOpen = true;
        }

        static MenuItem Item(string header, Action click)
        {
            var mi = new MenuItem { Header = header };
            mi.Click += (_, _) => click();
            return mi;
        }

        static MenuItem Check(string header, bool isChecked, Action click)
        {
            var mi = Item(header, click);
            mi.IsChecked = isChecked;
            return mi;
        }

        static MenuItem Sub(string header) => new() { Header = header };

        // Look and placement belong to the whole fence, so every tab gets the change.
        void Changed()
        {
            ApplyAppearance();
            _mgr.SyncGroup(Config);
            _mgr.SaveSoon();
        }

        void PickColor()
        {
            if (Dialogs.PickColor("Màu nền fence", Config.Color) is not string hex) return;
            Config.Color = hex;
            Changed();
        }

        void EditRules()
        {
            var text = Dialogs.Prompt("Quy tắc tự sắp xếp",
                "File trên Desktop khớp mẫu sẽ được chuyển vào fence này. Ngăn cách bằng dấu ;\n" +
                "Ví dụ:  *.jpg; *.png; báo cáo*; pdf; <folder>  (<folder> = mọi thư mục)",
                string.Join("; ", Config.Rules));
            if (text == null) return;
            Config.Rules = Rules.Parse(text);
            _mgr.SaveSoon();
        }

        void OpenFolder()
        {
            if (Directory.Exists(ItemFolder))
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{ItemFolder}\"") { UseShellExecute = true });
        }

        void ChoosePortal()
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Chọn thư mục hiển thị trong fence" };
            if (Config.IsPortal && Directory.Exists(Config.FolderPath)) dlg.InitialDirectory = Config.FolderPath;
            if (dlg.ShowDialog() == true) _mgr.SetPortal(this, dlg.FolderName);
        }

        public void BeginRename()
        {
            if (_renaming) return;
            _renaming = true;
            Native.ForceForeground(Handle);
            Activate();
            TitleEdit.Text = Config.Title;
            TitleText.Visibility = Visibility.Hidden;
            TabStrip.Visibility = Visibility.Collapsed;
            TitleEdit.Visibility = Visibility.Visible;
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            {
                TitleEdit.Focus();
                Keyboard.Focus(TitleEdit);
                TitleEdit.SelectAll();
            });
        }

        void EndRename(bool commit)
        {
            if (!_renaming) return;
            _renaming = false;
            TitleEdit.Visibility = Visibility.Collapsed;
            var name = TitleEdit.Text.Trim();
            if (commit && name.Length > 0 && name != Config.Title) _mgr.RenameFence(this, name);
            RebuildTabs();
        }

        void TitleEdit_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { EndRename(true); e.Handled = true; }
            else if (e.Key == Key.Escape) { EndRename(false); e.Handled = true; }
        }

        void TitleEdit_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => EndRename(true);

        #endregion
    }
}