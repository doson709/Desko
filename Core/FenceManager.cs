using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Desko.Interop;
using Desko.Models;
using Desko.Shell;
using Desko.UI;

namespace Desko.Core
{
    // Normal fences keep their files on the Desktop and hide them from Explorer's desktop view
    // with the Hidden attribute. Everything we hide is recorded in Config.HiddenByUs and undone on exit.
    internal sealed class FenceManager
    {
        const int MenuCreateFence = 1, MenuCreatePortal = 2, MenuCancel = 3;

        readonly List<FenceWindow> _windows = new();
        readonly DesktopHook _hook = new();
        readonly SelectionOverlay _overlay = new();
        readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
        readonly DispatcherTimer _desktopTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
        readonly DispatcherTimer _tidyTimer = new() { Interval = TimeSpan.FromSeconds(2) };
        readonly HashSet<string> _pendingTidy = new(StringComparer.OrdinalIgnoreCase);

        // Items temporarily un-hidden: while being dragged out / deleted / moved, and while on the clipboard.
        // Windows copies the Hidden attribute along with a file, so copies made elsewhere would vanish otherwise.
        readonly HashSet<string> _opVisible = new(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> _clipboardVisible = new(StringComparer.OrdinalIgnoreCase);
        uint _clipboardSeq;

        readonly object _rectLock = new();
        Native.RECT _latestRect;
        bool _rectQueued;
        FileSystemWatcher? _desktopWatcher;
        HwndSource? _clipboardHost;
        bool _shuttingDown;

        public FenceManager(AppConfig config)
        {
            Config = config;
            _saveTimer.Tick += (_, _) =>
            {
                _saveTimer.Stop();
                DisplayLayouts.Remember(Config, _displaySignature);
                SaveNow();
            };
            _displayTimer.Tick += (_, _) => { _displayTimer.Stop(); ApplyDisplayChange(); };
            _desktopTimer.Tick += (_, _) =>
            {
                _desktopTimer.Stop();
                Reconcile();
                RefreshNormalFences();
            };
            _tidyTimer.Tick += (_, _) =>
            {
                _tidyTimer.Stop();
                var list = _pendingTidy.ToList();
                _pendingTidy.Clear();
                TidyDesktop(list, quiet: true);
            };
        }

        public AppConfig Config { get; }
        public bool Hidden { get; private set; }

        // Icon size of the desktop itself; fences with IconSize 0 use it so both look identical.
        public int DesktopIconSize { get; private set; } = 48;
        readonly DispatcherTimer _iconSizeTimer = new() { Interval = TimeSpan.FromSeconds(2) };
        int? _lastIconSizeSetting;
        static Dispatcher UI => Application.Current.Dispatcher;
        static string Desktop => DesktopShell.DesktopPath;
        IEnumerable<FenceConfig> NormalFences => Config.Fences.Where(f => !f.IsPortal);

        public void Start()
        {
            // Icons were hidden by us when the app last died: give them back.
            if (Config.IconsHiddenByUs)
            {
                DesktopShell.SetIconsVisible(true);
                Config.IconsHiddenByUs = false;
            }

            MigrateLegacyFences();

            if (!Config.FollowDesktopIconSizeMigrated)
            {
                foreach (var f in Config.Fences.Where(f => f.IconSize == 48)) f.IconSize = 0;
                Config.FollowDesktopIconSizeMigrated = true;
            }
            // The registry value is instant; asking Explorer itself (COM) can stall while it is still
            // starting up after logon, so that more precise check runs once the fences are on screen.
            _lastIconSizeSetting = DesktopIconPlacer.ReadDesktopIconSizeSetting();
            DesktopIconSize = _lastIconSizeSetting ?? 48;
            UI.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
            {
                if (DesktopIconPlacer.GetDesktopIconSize() is int exact && exact != DesktopIconSize)
                {
                    DesktopIconSize = exact;
                    foreach (var w in _windows) w.Reload();
                }
            });
            _iconSizeTimer.Tick += (_, _) => CheckDesktopIconSize();
            _iconSizeTimer.Start();

            // First run: the welcome window offers starter fences built from what is on the desktop.
            Config.FirstRunDone = true;

            // Positions saved for this monitor setup win over whatever the last session ended with.
            _displaySignature = DisplayLayouts.CurrentSignature();
            if (!DisplayLayouts.Restore(Config, _displaySignature)) DisplayLayouts.BringOffscreenHome(Config);
            DisplayLayouts.Remember(Config, _displaySignature);

            Reconcile();
            foreach (var group in Config.Fences.GroupBy(GroupKey).ToList())
                OpenWindow(group.FirstOrDefault(t => t.ActiveTab) ?? group.First(), rename: false);

            _hook.DesktopDoubleClicked += () => UI.BeginInvoke(() => { if (Config.DoubleClickToHide) ToggleHidden(); });
            _hook.MouseDownOn += hwnd => UI.BeginInvoke(() => OnMouseDownAnywhere(hwnd));
            _hook.RightDragChanged += r =>
            {
                lock (_rectLock)
                {
                    _latestRect = r;
                    if (_rectQueued) return;
                    _rectQueued = true;
                }
                UI.BeginInvoke(() =>
                {
                    Native.RECT rect;
                    lock (_rectLock) { rect = _latestRect; _rectQueued = false; }
                    _overlay.ShowRect(rect);
                });
            };
            _hook.RightDragCompleted += r => UI.BeginInvoke(() => OnRightDragCompleted(r));

            StartDesktopWatcher();
            StartClipboardListener();
            ApplySettings();
            _hook.Start();
            SaveNow();
        }

        public void Shutdown()
        {
            _shuttingDown = true;
            EndPeek();
            _iconSizeTimer.Stop();
            if (_clipboardHost != null)
            {
                Native.UnregisterHotKey(_clipboardHost.Handle, HotkeySearch);
                Native.UnregisterHotKey(_clipboardHost.Handle, HotkeyPeek);
            }
            _hook.Dispose();
            _desktopWatcher?.Dispose();
            _clipboardHost?.Dispose();

            // Like the original: when the app is not running, fenced items are back on the desktop.
            foreach (var path in Config.HiddenByUs) Recovery.SetHidden(path, false);
            Config.HiddenByUs.Clear();
            Recovery.ShowVirtualIcons(Config);
            AutoStart.SetRestoreEntry(false);

            if (Config.IconsHiddenByUs)
            {
                DesktopShell.SetIconsVisible(true);
                Config.IconsHiddenByUs = false;
            }
            foreach (var w in _windows.ToList())
            {
                w.ClosingByManager = true;
                w.Close();
            }
            SaveNow();
        }

        public void SaveSoon()
        {
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        public void SaveNow() => ConfigStore.Save(Config);

        public void ShowWelcome() => BringForward(new WelcomeWindow(this));

        // Our windows open from a background app (tray, hotkey, a second launch): make sure they come to the front.
        static void BringForward(Window w)
        {
            w.Show();
            Native.ForceForeground(new WindowInteropHelper(w).Handle);
            w.Activate();
        }

        SettingsWindow? _settings;

        public void ShowSettings()
        {
            if (_settings is { IsLoaded: true })
            {
                Native.ForceForeground(new WindowInteropHelper(_settings).Handle);
                _settings.Activate();
                return;
            }
            _settings = new SettingsWindow(this);
            _settings.Closed += (_, _) => _settings = null;
            BringForward(_settings);
        }

        public void ApplySettings()
        {
            _hook.DoubleClickEnabled = Config.DoubleClickToHide;
            _hook.RightDragEnabled = Config.RightDragToCreate;
            foreach (var w in _windows) w.ApplyAppearance();
            UpdateRestoreEntry();
        }

        // If the app won't start at logon by itself, a logon "--restore" run brings hidden items back
        // after a power loss (when even the watchdog could not run).
        // Skipped until AutoStart has been primed in the background; the app calls it again then.
        public void UpdateRestoreEntry()
        {
            if (AutoStart.Known is bool autostart)
                AutoStart.SetRestoreEntry(Config.HiddenByUs.Count > 0 && !autostart);
        }

        public IReadOnlyList<Native.RECT> GetOtherRects(FenceWindow except) =>
            _windows.Where(w => w != except && w.IsVisible && w.Handle != IntPtr.Zero).Select(w => w.ScreenRect).ToList();

        public bool IsCursorOverFence()
        {
            Native.GetCursorPos(out var pt);
            return _windows.Any(w =>
            {
                if (!w.IsVisible || w.Handle == IntPtr.Zero) return false;
                var r = w.ScreenRect;
                return pt.X >= r.Left && pt.X < r.Right && pt.Y >= r.Top && pt.Y < r.Bottom;
            });
        }

        // The user resized the desktop icons (View menu or Ctrl+wheel): follow along.
        void CheckDesktopIconSize()
        {
            var setting = DesktopIconPlacer.ReadDesktopIconSizeSetting();
            if (setting == _lastIconSizeSetting) return;
            _lastIconSizeSetting = setting;
            int size = DesktopIconPlacer.GetDesktopIconSize() ?? DesktopIconSize;
            if (size == DesktopIconSize) return;
            DesktopIconSize = size;
            foreach (var w in _windows) w.Reload();
        }

        // Monitors plugged/unplugged or resolution changed. Every fence window reports it, and Windows
        // may still be rearranging, so wait a moment and handle it once.
        string _displaySignature = "";
        readonly DispatcherTimer _displayTimer = new() { Interval = TimeSpan.FromMilliseconds(1500) };

        public void DisplayChanged()
        {
            _displayTimer.Stop();
            _displayTimer.Start();
        }

        void ApplyDisplayChange()
        {
            string signature = DisplayLayouts.CurrentSignature();
            if (signature == _displaySignature) return;
            DisplayLayouts.Remember(Config, _displaySignature); // the fences still hold the old setup's positions
            _displaySignature = signature;
            if (!DisplayLayouts.Restore(Config, signature)) DisplayLayouts.BringOffscreenHome(Config);
            foreach (var w in _windows) w.ApplyBounds();
            DisplayLayouts.Remember(Config, signature);
            SaveNow();
        }

        void RefreshNormalFences()
        {
            foreach (var w in _windows) w.ScheduleReload();
        }

        #region Tabs

        public static string GroupKey(FenceConfig f) => string.IsNullOrEmpty(f.GroupId) ? f.Id : f.GroupId;

        public List<FenceConfig> TabsOf(FenceConfig f)
        {
            string key = GroupKey(f);
            var tabs = Config.Fences.Where(t => GroupKey(t) == key).ToList();
            if (tabs.Count == 0) tabs.Add(f);
            return tabs;
        }

        public IReadOnlyList<FenceWindow> OtherWindows(FenceWindow except) => _windows.Where(w => w != except).ToList();

        // A click outside a fence (another fence, the desktop, any other app) drops that fence's selection.
        // Clicks on our own menus and dialogs don't: they usually act on that very selection.
        void OnMouseDownAnywhere(IntPtr hwnd)
        {
            IntPtr root = Native.GetAncestor(hwnd, Native.GA_ROOT);
            var fence = _windows.FirstOrDefault(w => w.Handle == root);
            if (fence != null)
            {
                ClearSelectionsExcept(fence);
                return;
            }
            Native.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == (uint)Environment.ProcessId) return;
            ClearSelectionsExcept(null);
        }

        public void ClearSelectionsExcept(FenceWindow? keep)
        {
            foreach (var w in _windows)
                if (w != keep) w.ClearSelection();
        }

        public FenceWindow? WindowAt(Native.POINT pt, FenceWindow except) => _windows.FirstOrDefault(w =>
        {
            if (w == except || !w.IsVisible || w.Handle == IntPtr.Zero) return false;
            var r = w.ScreenRect;
            return pt.X >= r.Left && pt.X < r.Right && pt.Y >= r.Top && pt.Y < r.Bottom;
        });

        static void CopyLook(FenceConfig from, FenceConfig to)
        {
            to.X = from.X; to.Y = from.Y; to.Width = from.Width; to.Height = from.Height;
            to.RolledUp = from.RolledUp; to.Locked = from.Locked;
            to.Color = from.Color; to.Opacity = from.Opacity; to.IconSize = from.IconSize;
        }

        public void SyncGroup(FenceConfig source)
        {
            foreach (var t in TabsOf(source))
                if (t != source) CopyLook(source, t);
        }

        public void SetActiveTab(FenceConfig tab)
        {
            foreach (var t in TabsOf(tab)) t.ActiveTab = t == tab;
            SaveSoon();
        }

        public void AddTab(FenceWindow w)
        {
            var current = w.Config;
            if (string.IsNullOrEmpty(current.GroupId)) current.GroupId = current.Id;
            var tab = new FenceConfig { GroupId = current.GroupId, Title = "Tab mới" };
            CopyLook(current, tab);
            Config.Fences.Insert(Config.Fences.IndexOf(TabsOf(current).Last()) + 1, tab);
            w.SwitchTab(tab);
            w.BeginRename();
            SaveSoon();
        }

        public void DetachTab(FenceWindow w)
        {
            var tab = w.Config;
            var rest = TabsOf(tab).Where(t => t != tab).ToList();
            if (rest.Count == 0) return;
            Checkpoint($"Tách tab \"{tab.Title}\"");

            tab.GroupId = "";
            tab.ActiveTab = false;
            if (rest.Count == 1) rest[0].GroupId = "";
            w.SwitchTab(rest[0]);

            double s = Native.ScaleForPoint(new Native.POINT(tab.X, tab.Y));
            tab.X += (int)(32 * s);
            tab.Y += (int)(32 * s);
            OpenWindow(tab, rename: false);
            SaveSoon();
        }

        // Turns every tab of `source` into tabs of `target`; the source window goes away.
        public void MergeInto(FenceWindow source, FenceWindow target)
        {
            Checkpoint($"Gộp \"{source.Config.Title}\" vào \"{target.Config.Title}\"");
            var host = target.Config;
            if (string.IsNullOrEmpty(host.GroupId)) host.GroupId = host.Id;
            var moving = TabsOf(source.Config);
            foreach (var t in moving)
            {
                Config.Fences.Remove(t);
                t.GroupId = host.GroupId;
                t.ActiveTab = false;
                CopyLook(host, t);
            }
            Config.Fences.InsertRange(Config.Fences.IndexOf(TabsOf(host).Last()) + 1, moving);

            source.ClosingByManager = true;
            source.Close();
            target.SwitchTab(moving[0]);
            target.RebuildTabs();
            SaveSoon();
        }

        #endregion

        #region Hidden attribute bookkeeping

        // Brings the Hidden attribute of every desktop item in line with fence membership.
        public void Reconcile()
        {
            if (_shuttingDown) return;
            ReconcileVirtualIcons();
            var virtualShortcuts = Config.VirtualIcons.Select(v => v.ShortcutName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in NormalFences)
                foreach (var name in f.Members)
                {
                    if (virtualShortcuts.Contains(name)) continue; // always hidden, see ReconcileVirtualIcons
                    var path = Path.Combine(Desktop, name);
                    if (!_opVisible.Contains(path) && !_clipboardVisible.Contains(path) && Exists(path)) wanted.Add(path);
                }

            var tracked = new HashSet<string>(Config.HiddenByUs, StringComparer.OrdinalIgnoreCase);
            var toHide = wanted.Where(p => !tracked.Contains(p)).ToList();
            var toRelease = tracked.Where(p => !wanted.Contains(p)).ToList();

            // Record before hiding, so a crash right after can still undo it.
            if (toHide.Count > 0)
            {
                Config.HiddenByUs = tracked.Concat(toHide).ToList();
                SaveNow();
                UpdateRestoreEntry();
            }
            foreach (var p in wanted) Recovery.SetHidden(p, true);

            if (toRelease.Count > 0)
            {
                foreach (var p in toRelease) Recovery.SetHidden(p, false);
                Config.HiddenByUs = Config.HiddenByUs.Where(p => wanted.Contains(p)).ToList();
                SaveNow();
                UpdateRestoreEntry();
            }
        }

        // A fenced Recycle Bin / This PC... is a shortcut on the Desktop (kept hidden) plus the original icon
        // switched off. Once the shortcut leaves every fence (dragged out, deleted), the original comes back.
        void ReconcileVirtualIcons()
        {
            bool refresh = false, changed = false;
            foreach (var v in Config.VirtualIcons.ToList())
            {
                string path = Path.Combine(Desktop, v.ShortcutName);
                bool fenced = Exists(path) &&
                              NormalFences.Any(f => f.Members.Contains(v.ShortcutName, StringComparer.OrdinalIgnoreCase));
                if (fenced)
                {
                    Recovery.SetHidden(path, true);
                    refresh |= VirtualItems.SetDesktopIconHidden(v.Clsid, true);
                    continue;
                }
                refresh |= VirtualItems.SetDesktopIconHidden(v.Clsid, false);
                try { if (File.Exists(path)) File.Delete(path); }
                catch (Exception ex) { Log.Error(ex); }
                foreach (var f in NormalFences)
                {
                    f.Members.RemoveAll(n => string.Equals(n, v.ShortcutName, StringComparison.OrdinalIgnoreCase));
                    f.Order.RemoveAll(n => string.Equals(n, v.ShortcutName, StringComparison.OrdinalIgnoreCase));
                }
                Config.VirtualIcons.Remove(v);
                changed = true;
            }
            if (refresh) VirtualItems.RefreshDesktop();
            if (changed) SaveNow();
        }

        // Dropped items that are not files: store a shortcut to each one in the fence.
        public void AddShellItems(FenceConfig fence, IReadOnlyList<IntPtr> pidls, int index)
        {
            string folder = fence.IsPortal ? fence.FolderPath : Desktop;
            var names = new List<string>();
            foreach (var pidl in pidls)
            {
                string? clsid = fence.IsPortal ? null : VirtualItems.DesktopIconClsid(pidl);
                if (clsid != null && Config.VirtualIcons.Any(v => string.Equals(v.Clsid, clsid, StringComparison.OrdinalIgnoreCase)))
                    continue;
                string display = VirtualItems.GetName(pidl, parsing: false) ?? "Shortcut";
                string safe = new string(display.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray()).Trim();
                string path = UniquePath(folder, (safe.Length > 0 ? safe : "Shortcut") + ".lnk");
                if (!VirtualItems.CreateShortcut(pidl, path)) continue;
                string name = Path.GetFileName(path);
                names.Add(name);
                if (clsid != null) Config.VirtualIcons.Add(new VirtualIcon { Clsid = clsid, ShortcutName = name });
            }
            if (names.Count == 0) return;
            if (fence.IsPortal) SaveSoon();
            else AddMembers(fence, names, index);
        }

        public bool WithVisible(IEnumerable<string> paths, Func<bool> action)
        {
            var list = paths.ToList();
            _opVisible.UnionWith(list);
            Reconcile();
            try { return action(); }
            finally
            {
                _opVisible.ExceptWith(list);
                Reconcile();
                RefreshNormalFences();
            }
        }

        public void BeginTransientVisible(IEnumerable<string> paths)
        {
            _opVisible.UnionWith(paths);
            Reconcile();
        }

        public void EndTransientVisible(IEnumerable<string> paths) => _opVisible.ExceptWith(paths);

        public void PutOnClipboard(IReadOnlyList<string> paths, bool cut)
        {
            if (paths.Count == 0) return;
            _clipboardVisible.Clear();
            _clipboardVisible.UnionWith(paths);
            Reconcile();

            var list = new StringCollection();
            list.AddRange(paths.ToArray());
            var data = new DataObject();
            data.SetFileDropList(list);
            data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(cut ? 2 : 5)));
            try { Clipboard.SetDataObject(data, true); }
            catch (Exception ex) { Log.Error(ex); }
            _clipboardSeq = Native.GetClipboardSequenceNumber();
        }

        void StartClipboardListener()
        {
            try
            {
                _clipboardHost = new HwndSource(new HwndSourceParameters("DeskoClipboard")
                {
                    Width = 0,
                    Height = 0,
                    WindowStyle = 0,
                    ParentWindow = new IntPtr(-3), // HWND_MESSAGE
                });
                _clipboardHost.AddHook((IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled) =>
                {
                    // Something else replaced our clipboard content: the items can be hidden again.
                    if (msg == Native.WM_CLIPBOARDUPDATE && _clipboardVisible.Count > 0 &&
                        Native.GetClipboardSequenceNumber() != _clipboardSeq)
                    {
                        _clipboardVisible.Clear();
                        Reconcile();
                    }
                    else if (msg == Native.WM_HOTKEY)
                    {
                        if (w.ToInt32() == HotkeySearch) ShowGlobalSearch();
                        else if (w.ToInt32() == HotkeyPeek) TogglePeek();
                    }
                    return IntPtr.Zero;
                });
                Native.AddClipboardFormatListener(_clipboardHost.Handle);
                // Global hotkeys: Ctrl+Alt+F search every fence, Ctrl+Alt+D peek at the fences.
                const uint mods = Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT;
                if (!Native.RegisterHotKey(_clipboardHost.Handle, HotkeySearch, mods, 0x46))
                    Log.Error(new InvalidOperationException("Ctrl+Alt+F is already taken by another app"));
                if (!Native.RegisterHotKey(_clipboardHost.Handle, HotkeyPeek, mods, 0x44))
                    Log.Error(new InvalidOperationException("Ctrl+Alt+D is already taken by another app"));
            }
            catch (Exception ex) { Log.Error(ex); }
        }

        #endregion

        #region Settings helpers

        // Live changes from the Settings window: applied to every fence (all tabs) at once.
        public void ApplyToAllFences(Action<FenceConfig> change, bool reload = false)
        {
            foreach (var f in Config.Fences) change(f);
            foreach (var w in _windows)
            {
                w.ApplyAppearance();
                if (reload) w.Reload();
            }
            SaveSoon();
        }

        // First-run helper: one fence per kind of thing found on the desktop, each with matching rules,
        // then sorts the desktop into them. Returns how many fences were created.
        public int CreateStarterFences()
        {
            var kinds = new (string Title, string[] Rules)[]
            {
                ("Thư mục", new[] { Rules.FolderToken }),
                ("Tài liệu", Rules.Presets.First(p => p.Name == "Tài liệu").Patterns),
                ("Ảnh & video", Rules.Presets.First(p => p.Name == "Hình ảnh").Patterns
                                     .Concat(Rules.Presets.First(p => p.Name == "Video").Patterns).ToArray()),
                ("Ứng dụng", Rules.Presets.First(p => p.Name == "Shortcut & ứng dụng").Patterns),
            };
            List<FileSystemInfo> loose;
            try
            {
                var fenced = NormalFences.SelectMany(f => f.Members).ToHashSet(StringComparer.OrdinalIgnoreCase);
                loose = new DirectoryInfo(Desktop).EnumerateFileSystemInfos()
                    .Where(i => !Rules.IsIgnorable(i) && !fenced.Contains(i.Name)).ToList();
            }
            catch (Exception ex) { Log.Error(ex); return 0; }

            var work = Native.WorkAreaFromPoint(new Native.POINT(0, 0));
            double s = Native.ScaleForPoint(new Native.POINT(work.Left + 1, work.Top + 1));
            int w = (int)(330 * s), h = (int)(230 * s), gap = (int)(12 * s);
            int x = work.Left + (int)(110 * s), y = work.Top + (int)(20 * s), created = 0;

            Checkpoint("Tạo các fence gợi ý");
            foreach (var (title, rules) in kinds)
            {
                if (!loose.Any(i => Rules.Matches(rules, i.FullName, i is DirectoryInfo))) continue;
                if (x + w > work.Right) { x = work.Left + (int)(110 * s); y += h + gap; }
                var fence = new FenceConfig { Title = title, Rules = rules.ToList(), X = x, Y = y, Width = w, Height = h };
                Config.Fences.Add(fence);
                OpenWindow(fence, rename: false);
                x += w + gap;
                created++;
            }
            if (created > 0) TidyDesktop(null, quiet: true);
            SaveSoon();
            return created;
        }

        #endregion

        #region Undo

        // Fence-level undo (Ctrl+Z in a fence, or the tray menu): a snapshot of the fences taken before each
        // structural change. File operations are not included; Explorer's own Ctrl+Z covers those.
        sealed record UndoEntry(string Description, string Snapshot);
        readonly List<UndoEntry> _undo = new();
        public Action<string, string>? Notify { get; set; }

        public string? UndoDescription => _undo.Count > 0 ? _undo[^1].Description : null;

        public void Checkpoint(string description)
        {
            var snapshot = new AppConfig { Fences = Config.Fences, VirtualIcons = Config.VirtualIcons };
            _undo.Add(new UndoEntry(description, System.Text.Json.JsonSerializer.Serialize(snapshot, ConfigJson.Default.AppConfig)));
            if (_undo.Count > 25) _undo.RemoveAt(0);
        }

        public void Undo()
        {
            if (_undo.Count == 0)
            {
                Notify?.Invoke("Desko", "Không còn thao tác nào để hoàn tác.");
                return;
            }
            var entry = _undo[^1];
            _undo.RemoveAt(_undo.Count - 1);
            var snapshot = System.Text.Json.JsonSerializer.Deserialize(entry.Snapshot, ConfigJson.Default.AppConfig);
            if (snapshot == null) return;

            EndPeek();
            foreach (var w in _windows.ToList())
            {
                w.ClosingByManager = true;
                w.Close();
            }
            Config.Fences = snapshot.Fences;
            Config.VirtualIcons = snapshot.VirtualIcons;
            Reconcile();
            foreach (var group in Config.Fences.GroupBy(GroupKey).ToList())
                OpenWindow(group.FirstOrDefault(t => t.ActiveTab) ?? group.First(), rename: false);
            SaveNow();
            Notify?.Invoke("Đã hoàn tác", entry.Description);
        }

        #endregion

        #region Peek & global search

        const int HotkeySearch = 1, HotkeyPeek = 2;
        PeekOverlay? _peekOverlay;
        public bool Peeking => _peekOverlay != null;

        public void TogglePeek()
        {
            if (Peeking) EndPeek();
            else BeginPeek();
        }

        // Lifts every fence above all windows over a dimmed screen.
        public void BeginPeek()
        {
            if (Peeking) return;
            if (Hidden) SetHidden(false);
            _peekOverlay = new PeekOverlay(EndPeek);
            _peekOverlay.ShowOverlay();
            foreach (var w in _windows) w.SetPeek(true); // after the overlay, so they land above it
        }

        public void EndPeek()
        {
            if (_peekOverlay == null) return;
            var overlay = _peekOverlay;
            _peekOverlay = null;
            foreach (var w in _windows) w.SetPeek(false);
            overlay.Close();
        }

        public void ShowGlobalSearch()
        {
            var results = new List<SearchWindow.Result>();
            foreach (var f in Config.Fences)
            {
                IEnumerable<string> paths;
                try
                {
                    paths = f.IsPortal
                        ? Directory.Exists(f.FolderPath) ? Directory.EnumerateFileSystemEntries(f.FolderPath).Take(3000).ToList() : new List<string>()
                        : f.Members.Select(n => Path.Combine(Desktop, n)).Where(Exists).ToList();
                }
                catch (Exception ex) { Log.Error(ex); continue; }

                foreach (var path in paths)
                {
                    FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
                    if (f.IsPortal && (info.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                    string name = ShellInfo.GetDisplayNameCached(path, info.LastWriteTimeUtc, Path.GetFileName(path));
                    results.Add(new SearchWindow.Result
                    {
                        Path = path,
                        Name = name,
                        Where = f.Title,
                        Fence = f,
                        Key = TextMatch.Normalize(name),
                    });
                }
            }
            new SearchWindow(this, results.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ToList()).Show();
        }

        public void OpenPath(string path)
        {
            EndPeek();
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path)
                {
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(path) ?? "",
                });
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) { }
            catch (Exception ex) { Dialogs.Info("Không mở được", ex.Message); }
        }

        // Shows an item in its fence: lifts the fences, opens the right tab and selects the item.
        public void Reveal(FenceConfig tab, string path)
        {
            var window = _windows.FirstOrDefault(w => TabsOf(w.Config).Contains(tab));
            if (window == null) return;
            BeginPeek();
            window.SwitchTab(tab);
            window.RevealItem(path);
        }

        #endregion

        #region Membership

        public void AddMembers(FenceConfig fence, IReadOnlyCollection<string> names, int index)
        {
            if (names.Count == 0) return;
            foreach (var other in NormalFences.Where(f => f != fence))
            {
                other.Members.RemoveAll(n => names.Contains(n, StringComparer.OrdinalIgnoreCase));
                other.Order.RemoveAll(n => names.Contains(n, StringComparer.OrdinalIgnoreCase));
            }
            foreach (var n in names)
                if (!fence.Members.Contains(n, StringComparer.OrdinalIgnoreCase)) fence.Members.Add(n);

            var order = fence.Order.Where(n => !names.Contains(n, StringComparer.OrdinalIgnoreCase)).ToList();
            order.InsertRange(Math.Clamp(index, 0, order.Count), names);
            fence.Order = order;

            Reconcile();
            RefreshNormalFences();
            SaveSoon();
        }

        public void RemoveMembers(FenceConfig fence, IEnumerable<string> names)
        {
            var set = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
            fence.Members.RemoveAll(set.Contains);
            fence.Order.RemoveAll(set.Contains);
            Reconcile();
            RefreshNormalFences();
            SaveSoon();
        }

        public void RenameMember(string? oldName, string? newName)
        {
            if (string.IsNullOrEmpty(oldName) || string.IsNullOrEmpty(newName)) return;
            foreach (var f in NormalFences)
            {
                int i = f.Members.FindIndex(n => string.Equals(n, oldName, StringComparison.OrdinalIgnoreCase));
                if (i < 0) continue;
                f.Members[i] = newName;
                int k = f.Order.FindIndex(n => string.Equals(n, oldName, StringComparison.OrdinalIgnoreCase));
                if (k >= 0) f.Order[k] = newName;
                SaveSoon();
            }
        }

        // Forget desktop items that were moved away (dragged to another folder, pasted elsewhere...).
        // Deleted items are deliberately kept, so restoring them from the Recycle Bin puts them back.
        public void ForgetMissing(IEnumerable<string> paths)
        {
            var gone = paths.Select(p => p.TrimEnd('\\'))
                            .Where(p => PathEquals(Path.GetDirectoryName(p), Desktop) && !Exists(p))
                            .Select(Path.GetFileName)
                            .OfType<string>()
                            .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (gone.Count == 0) return;
            foreach (var f in NormalFences)
            {
                f.Members.RemoveAll(gone.Contains);
                f.Order.RemoveAll(gone.Contains);
            }
            SaveSoon();
        }

        // Drop/paste into a normal fence: items already on the Desktop just join the fence,
        // anything else is moved or copied onto the Desktop first (as the original does).
        public void ImportToDesktop(FenceConfig fence, IReadOnlyList<string> files, bool copy, int index, IntPtr owner)
        {
            var onDesktop = files.Where(f => PathEquals(Path.GetDirectoryName(f.TrimEnd('\\')), Desktop)).ToList();
            var others = files.Except(onDesktop).ToList();
            var names = new List<string>();

            if (copy)
            {
                var before = DesktopSnapshot();
                WithVisible(files, () => FileOps.Copy(files, Desktop, owner, renameOnCollision: onDesktop.Count > 0));
                names.AddRange(DesktopSnapshot().Where(n => !before.Contains(n)));
            }
            else
            {
                names.AddRange(onDesktop.Select(f => Path.GetFileName(f.TrimEnd('\\'))));
                if (others.Count > 0)
                {
                    var before = DesktopSnapshot();
                    WithVisible(others, () => FileOps.Move(others, Desktop, owner));
                    names.AddRange(DesktopSnapshot().Where(n => !before.Contains(n)));
                    names.AddRange(others.Select(f => Path.GetFileName(f.TrimEnd('\\')))
                                         .Where(n => Exists(Path.Combine(Desktop, n))));
                    ForgetMissing(others);
                }
            }
            AddMembers(fence, names.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), index);
        }

        static HashSet<string> DesktopSnapshot()
        {
            try
            {
                return Directory.EnumerateFileSystemEntries(Desktop).Select(Path.GetFileName).OfType<string>()
                                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                Log.Error(ex);
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }
        }

        #endregion

        #region Fence lifecycle

        FenceWindow OpenWindow(FenceConfig f, bool rename)
        {
            var w = new FenceWindow(this, f) { PendingRename = rename };
            w.Closed += (_, _) => OnWindowClosed(w);
            _windows.Add(w);
            w.Show();
            if (Hidden) w.Hide();
            return w;
        }

        // If Explorer restarts it can take our (Progman-owned) windows down with it; bring them back.
        void OnWindowClosed(FenceWindow w)
        {
            _windows.Remove(w);
            if (_shuttingDown || w.ClosingByManager || !Config.Fences.Contains(w.Config)) return;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            timer.Tick += (_, _) =>
            {
                if (DesktopShell.Progman == IntPtr.Zero) return;
                timer.Stop();
                if (!_shuttingDown && Config.Fences.Contains(w.Config) && _windows.All(x => x.Config != w.Config))
                    OpenWindow(w.Config, rename: false);
            };
            timer.Start();
        }

        public FenceWindow CreateFence(Native.RECT r, string? portal, bool rename)
        {
            if (Hidden) SetHidden(false);
            var f = new FenceConfig { X = r.Left, Y = r.Top, Width = r.Width, Height = r.Height };
            if (portal != null)
            {
                f.IsPortal = true;
                f.FolderPath = portal;
                f.Title = new DirectoryInfo(portal).Name;
            }
            Config.Fences.Add(f);
            SaveSoon();
            return OpenWindow(f, rename);
        }

        public void CreateFenceAtCursor()
        {
            Native.GetCursorPos(out var pt);
            var work = Native.WorkAreaFromPoint(pt);
            double s = Native.ScaleForPoint(pt);
            int w = (int)(340 * s), h = (int)(220 * s);
            int x = Math.Clamp(pt.X - w / 2, work.Left, Math.Max(work.Left, work.Right - w));
            int y = Math.Clamp(pt.Y - (int)(14 * s), work.Top, Math.Max(work.Top, work.Bottom - h));
            CreateFence(new Native.RECT(x, y, x + w, y + h), null, rename: true);
        }

        void OnRightDragCompleted(Native.RECT r)
        {
            double s = Native.ScaleForPoint(new Native.POINT(r.Left, r.Top));
            if (r.Width < 120 * s) r.Right = r.Left + (int)(120 * s);
            if (r.Height < 80 * s) r.Bottom = r.Top + (int)(80 * s);
            _overlay.ShowRect(r);

            int cmd;
            try
            {
                cmd = NativePopupMenu.Show(new (int, string?)[]
                {
                    (MenuCreateFence, "Tạo Fence tại đây"),
                    (MenuCreatePortal, "Tạo Folder Portal tại đây…"),
                    (0, null),
                    (MenuCancel, "Hủy"),
                });
            }
            finally { _overlay.Hide(); }

            if (cmd == MenuCreateFence)
            {
                CreateFence(r, null, rename: true);
            }
            else if (cmd == MenuCreatePortal)
            {
                var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Chọn thư mục hiển thị trong fence" };
                if (dlg.ShowDialog() == true) CreateFence(r, dlg.FolderName, rename: false);
            }
        }

        public void RemoveFence(FenceWindow w, bool wholeFence)
        {
            var f = w.Config;
            var tabs = TabsOf(f);
            var removing = wholeFence ? tabs : new List<FenceConfig> { f };
            string what = removing.Count > 1 ? $"fence \"{f.Title}\" và {removing.Count - 1} tab khác"
                        : wholeFence ? $"fence \"{f.Title}\"" : $"tab \"{f.Title}\"";
            string msg = removing.All(t => t.IsPortal)
                ? $"Gỡ {what}?\nThư mục gốc vẫn được giữ nguyên."
                : $"Xóa {what}?\nCác mục bên trong sẽ hiện lại trên Desktop (không file nào bị xóa).";
            if (!Dialogs.Confirm(wholeFence ? "Xóa fence" : "Xóa tab", msg)) return;
            Checkpoint(wholeFence ? $"Xóa {what}" : $"Xóa {what}");

            foreach (var t in removing) Config.Fences.Remove(t);
            Reconcile();

            var remaining = tabs.Except(removing).ToList();
            if (remaining.Count > 0)
            {
                if (remaining.Count == 1) remaining[0].GroupId = "";
                w.SwitchTab(remaining[0]);
                w.RebuildTabs();
            }
            else
            {
                w.ClosingByManager = true;
                w.Close();
            }
            SaveSoon();
        }

        public void RenameFence(FenceWindow w, string name)
        {
            Checkpoint($"Đổi tên \"{w.Config.Title}\" thành \"{name}\"");
            w.Config.Title = name;
            w.ApplyAppearance();
            SaveSoon();
        }

        public void SetPortal(FenceWindow w, string folder)
        {
            var f = w.Config;
            if (f.IsPortal && PathEquals(f.FolderPath, folder)) return;
            if (!f.IsPortal)
            {
                if (f.Members.Count > 0 &&
                    !Dialogs.Confirm("Chuyển thành Folder Portal",
                        "Các mục đang có trong fence sẽ hiện lại trên Desktop. Tiếp tục?")) return;
                Checkpoint($"Chuyển \"{f.Title}\" thành Folder Portal");
                f.Members.Clear();
                f.Title = new DirectoryInfo(folder).Name;
            }
            f.IsPortal = true;
            f.FolderPath = folder;
            f.Order.Clear();
            Reconcile();
            w.RestartWatcher();
            w.ApplyAppearance();
            w.Reload();
            SaveSoon();
        }

        // Version 1.0 moved fenced files into %USERPROFILE%\Desko\<fence>. Bring them back
        // to the Desktop, keeping them in the same fence.
        void MigrateLegacyFences()
        {
            foreach (var f in Config.Fences.Where(f => !f.IsPortal && f.FolderPath.Length > 0))
            {
                if (Directory.Exists(f.FolderPath))
                {
                    foreach (var entry in Directory.GetFileSystemEntries(f.FolderPath))
                    {
                        string target = UniqueDesktopPath(Path.GetFileName(entry));
                        string oldName = Path.GetFileName(entry);
                        if (!FileOps.MoveTo(entry, target, IntPtr.Zero)) continue;
                        string newName = Path.GetFileName(target);
                        f.Members.Add(newName);
                        int k = f.Order.FindIndex(n => string.Equals(n, oldName, StringComparison.OrdinalIgnoreCase));
                        if (k >= 0) f.Order[k] = newName;
                    }
                    TryDeleteEmpty(f.FolderPath);
                }
                // Anything that could not be moved stays reachable: the fence becomes a portal onto it.
                if (Directory.Exists(f.FolderPath)) f.IsPortal = true;
                else f.FolderPath = "";
            }
            TryDeleteEmpty(ConfigStore.LegacyStorageRoot);
        }

        static string UniqueDesktopPath(string name) => UniquePath(Desktop, name);

        static string UniquePath(string folder, string name)
        {
            string path = Path.Combine(folder, name);
            string stem = Path.GetFileNameWithoutExtension(name), ext = Path.GetExtension(name);
            for (int n = 2; Exists(path); n++) path = Path.Combine(folder, $"{stem} ({n}){ext}");
            return path;
        }

        #endregion

        #region Hide / show

        public void ToggleHidden() => SetHidden(!Hidden);

        public void SetHidden(bool hide)
        {
            if (Hidden == hide) return;
            Hidden = hide;
            foreach (var w in _windows)
            {
                if (hide) w.FadeTo(0, () => { if (Hidden) w.Hide(); });
                else
                {
                    w.Show();
                    w.FadeTo(1);
                }
            }

            if (hide && Config.HideIconsToo && DesktopShell.IconsVisible)
            {
                DesktopShell.SetIconsVisible(false);
                Config.IconsHiddenByUs = true;
                SaveNow();
            }
            else if (!hide && Config.IconsHiddenByUs)
            {
                DesktopShell.SetIconsVisible(true);
                Config.IconsHiddenByUs = false;
                SaveNow();
            }
        }

        #endregion

        #region Desktop watcher & auto-organize rules

        void StartDesktopWatcher()
        {
            try
            {
                var w = new FileSystemWatcher(Desktop)
                {
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName |
                                   NotifyFilters.Attributes | NotifyFilters.LastWrite,
                };
                // Only changes to fenced items need a refresh; a download in progress elsewhere on the
                // Desktop would otherwise refresh every fence several times a second.
                w.Created += (_, e) => UI.BeginInvoke(() => { if (IsFenced(e.Name)) DesktopChanged(); QueueTidy(e.FullPath); });
                w.Deleted += (_, e) => UI.BeginInvoke(() => { if (IsFenced(e.Name)) DesktopChanged(); });
                w.Changed += (_, e) => UI.BeginInvoke(() => { if (IsFenced(e.Name)) DesktopChanged(); });
                w.Renamed += (_, e) => UI.BeginInvoke(() =>
                {
                    bool fenced = IsFenced(e.OldName) || IsFenced(e.Name);
                    RenameMember(e.OldName, e.Name);
                    if (fenced) DesktopChanged();
                    QueueTidy(e.FullPath);
                });
                w.Error += (_, _) => UI.BeginInvoke(() =>
                {
                    _desktopWatcher?.Dispose();
                    _desktopWatcher = null;
                    StartDesktopWatcher();
                    DesktopChanged();
                });
                w.EnableRaisingEvents = true;
                _desktopWatcher = w;
            }
            catch (Exception ex) { Log.Error(ex); }
        }

        bool IsFenced(string? name) =>
            !string.IsNullOrEmpty(name) &&
            NormalFences.Any(f => f.Members.Contains(name, StringComparer.OrdinalIgnoreCase));

        void DesktopChanged()
        {
            _desktopTimer.Stop();
            _desktopTimer.Start();
        }

        void QueueTidy(string path)
        {
            if (!Config.AutoApplyRules) return;
            _pendingTidy.Add(path);
            _tidyTimer.Stop();
            _tidyTimer.Start();
        }

        // Puts loose desktop items into the first fence whose rules match. Returns how many were sorted.
        public int TidyDesktop(IEnumerable<string>? only, bool quiet)
        {
            var fences = Config.Fences
                .Where(f => f.Rules.Count > 0 && (!f.IsPortal || (f.FolderPath.Length > 0 && !PathEquals(f.FolderPath, Desktop))))
                .ToList();
            if (fences.Count == 0) return 0;

            List<string> entries;
            try { entries = only?.ToList() ?? Directory.EnumerateFileSystemEntries(Desktop).ToList(); }
            catch (Exception ex) { Log.Error(ex); return 0; }

            var fenced = NormalFences.SelectMany(f => f.Members).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var groups = new Dictionary<FenceConfig, List<string>>();
            foreach (var path in entries)
            {
                FileSystemInfo fsi = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
                if (!fsi.Exists || !PathEquals(Path.GetDirectoryName(fsi.FullName), Desktop) ||
                    Rules.IsIgnorable(fsi) || fenced.Contains(fsi.Name)) continue;
                bool isDir = fsi is DirectoryInfo;
                var target = fences.FirstOrDefault(f =>
                    (!f.IsPortal || !IsSameOrUnder(f.FolderPath, fsi.FullName)) && Rules.Matches(f.Rules, fsi.FullName, isDir));
                if (target == null) continue;
                if (!groups.TryGetValue(target, out var list)) groups[target] = list = new List<string>();
                list.Add(fsi.FullName);
            }

            if (groups.Count > 0 && !quiet) Checkpoint("Dọn desktop theo quy tắc");
            int sorted = 0;
            foreach (var (fence, list) in groups)
            {
                if (!fence.IsPortal)
                {
                    AddMembers(fence, list.Select(Path.GetFileName).OfType<string>().ToList(), int.MaxValue);
                    sorted += list.Count;
                }
                else if (Directory.Exists(fence.FolderPath) &&
                         FileOps.Move(list, fence.FolderPath, IntPtr.Zero, quiet: quiet, renameOnCollision: true))
                {
                    sorted += list.Count;
                }
            }
            return sorted;
        }

        #endregion

        #region Paths

        static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

        static void TryDeleteEmpty(string folder)
        {
            try
            {
                if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
                    Directory.Delete(folder);
            }
            catch (Exception ex) { Log.Error(ex); }
        }

        public static bool PathEquals(string? a, string? b) =>
            !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) &&
            string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

        static bool IsSameOrUnder(string path, string root)
        {
            string p = Path.GetFullPath(path).TrimEnd('\\') + "\\";
            string r = Path.GetFullPath(root).TrimEnd('\\') + "\\";
            return p.StartsWith(r, StringComparison.OrdinalIgnoreCase);
        }

        #endregion
    }
}