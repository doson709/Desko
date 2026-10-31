using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Desko.Interop;
using Desko.Models;
using Desko.Shell;
using Desko.UI;

namespace Desko.Core
{
    // Normal fences hold the real desktop icons: each member is placed on its fence's grid by moving the icon
    // through Explorer's desktop view, and members that cannot show (rolled-up fence, other tabs, rows scrolled
    // away) are parked far off screen. Files never move and are never hidden, so Explorer, file dialogs and
    // everything else see the Desktop exactly as it is. Folder portals show another folder themselves.
    internal sealed partial class FenceManager
    {
        const int MenuCreateFence = 1, MenuCreatePortal = 2, MenuCancel = 3;

        readonly List<FenceWindow> _windows = new();
        readonly DesktopHook _hook = new();
        readonly SelectionOverlay _overlay = new();
        readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
        readonly DispatcherTimer _syncTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
        readonly DispatcherTimer _tidyTimer = new() { Interval = TimeSpan.FromSeconds(2) };
        readonly DispatcherTimer _wallpaperTimer = new() { Interval = TimeSpan.FromSeconds(30) };
        readonly HashSet<string> _pendingTidy = new(StringComparer.OrdinalIgnoreCase);

        // Where Desko last put each icon, and where every icon was at the last look (screen pixels).
        readonly Dictionary<string, Native.POINT> _placed = new(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, Native.POINT> _seen = new(StringComparer.OrdinalIgnoreCase);
        bool _pendingUserMove;

        readonly object _rectLock = new();
        Native.RECT _latestRect;
        bool _rectQueued;
        FileSystemWatcher? _desktopWatcher;
        HwndSource? _messageHost;
        bool _shuttingDown;
        int _nextZoneId = 1;

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
            _syncTimer.Tick += (_, _) =>
            {
                _syncTimer.Stop();
                bool user = _pendingUserMove;
                _pendingUserMove = false;
                _lastSyncWhy = _syncWhy.Count == 0 ? "other" : string.Join("+", _syncWhy);
                _syncWhy.Clear();
                SyncDesktop(user);
            };
            _tidyTimer.Tick += (_, _) =>
            {
                _tidyTimer.Stop();
                var list = _pendingTidy.ToList();
                _pendingTidy.Clear();
                TidyDesktop(list, quiet: true);
            };
            _wallpaperTimer.Tick += (_, _) => FenceBackdrop.CheckWallpaper();
        }

        public AppConfig Config { get; }
        public bool Hidden { get; private set; }

        // Icon size of the desktop itself; folder portals with IconSize 0 use it so both look identical.
        public int DesktopIconSize { get; private set; } = 48;
        readonly DispatcherTimer _iconSizeTimer = new() { Interval = TimeSpan.FromSeconds(2) };
        int? _lastIconSizeSetting;
        static Dispatcher UI => Application.Current.Dispatcher;
        static string Desktop => DesktopShell.DesktopPath;
        IEnumerable<FenceConfig> NormalFences => Config.Fences.Where(f => !f.IsPortal);

        public void Start()
        {
            // Started right after a crash: let the restore that the watchdog launched finish first.
            Recovery.WaitForRestore(TimeSpan.FromSeconds(10));

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
            _lastIconSizeSetting = DesktopIcons.ReadDesktopIconSizeSetting();
            DesktopIconSize = _lastIconSizeSetting ?? 48;
            _iconSizeTimer.Tick += (_, _) => CheckDesktopIconSize();
            _iconSizeTimer.Start();

            // First run: the welcome window offers starter fences built from what is on the desktop.
            Config.FirstRunDone = true;

            // Positions saved for this monitor setup win over whatever the last session ended with.
            _displaySignature = DisplayLayouts.CurrentSignature();
            if (!DisplayLayouts.Restore(Config, _displaySignature)) DisplayLayouts.BringOffscreenHome(Config);
            DisplayLayouts.Remember(Config, _displaySignature);

            PrepareDesktop();
            foreach (var group in Config.Fences.GroupBy(GroupKey).ToList())
                OpenWindow(group.FirstOrDefault(t => t.ActiveTab) ?? group.First(), rename: false);
            UI.BeginInvoke(DispatcherPriority.Loaded, () => { LayoutAll(); SettleLayout(); });

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
            _hook.DragZoneChanged += zone => UI.BeginInvoke(() => SetDragZone(zone));
            _hook.Released += (iconsMoved, from, to) => UI.BeginInvoke(() =>
            {
                WatchParked(iconsMoved ? "a drop" : "a release", iconsMoved ? from : null);
                if (iconsMoved) OnIconsDragged(from, to);
                else if (_movesDeferred) QueueSync(user: false, delayMs: 150, why: "deferred");
            });
            _hook.RightDragReleased += (from, to) => UI.BeginInvoke(() =>
            {
                WatchParked("a right-drop");
                OnIconsDragged(from, to);
                // ...and once more when the menu has most likely been answered.
                var late = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
                late.Tick += (_, _) => { late.Stop(); QueueSync(user: true); };
                late.Start();
            });
            _hook.DesktopPressed += pt => UI.BeginInvoke(() => _lastDesktopPress = (DateTime.UtcNow, pt));
            _hook.Wheel += (zone, delta) => UI.BeginInvoke(() =>
                _windows.FirstOrDefault(w => w.ZoneId == zone)?.ScrollRows(delta > 0 ? -1 : 1));

            StartDesktopWatcher();
            // A rebuilt icon list comes in a burst of events; parked icons it pulled on screen go back right after.
            DesktopEvents.ItemsChanged += () =>
            {
                if (_shuttingDown || Hidden) return;
                WatchParked("an explorer list change");
                QueueSync(user: false, delayMs: 120, why: "explorer list");
            };
            DesktopEvents.SelectionChanged += OnSelectionChanged;
            DesktopIcons.Watch.Reparked += (request, found, ms) => UI.BeginInvoke(() => OnReparked(request, found, ms));
            DesktopIcons.Watch.Landed += (request, ms) => UI.BeginInvoke(() => OnDropLanded(request, ms));
            DesktopEvents.Start();
            StartMessageHost();
            ApplySettings();
            _hook.Start();
            _wallpaperTimer.Start();
            SaveNow();
            TestChannel.Start(this);
        }

        public void Shutdown()
        {
            _shuttingDown = true;
            _iconSizeTimer.Stop();
            _wallpaperTimer.Stop();
            if (_messageHost != null)
            {
                Native.UnregisterHotKey(_messageHost.Handle, HotkeySearch);
                Native.UnregisterHotKey(_messageHost.Handle, HotkeyDesktop);
            }
            _hook.Dispose();
            DesktopEvents.Stop();
            _desktopWatcher?.Dispose();
            _messageHost?.Dispose();

            // Like the original: without the app the icons simply stay where they are; parked ones come back.
            if (ReleaseParked())
            {
                Config.IconsParked = false;
                AutoStart.SetRestoreEntry(false);
            }
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
            DisplayLayouts.Remember(Config, _displaySignature); // a move just before exit is still in the save timer
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
            if (_windows.Count > 0) QueueSync(user: false); // e.g. "Auto arrange" just switched on: pack now
        }

        // If the app won't start at logon by itself, a logon "--restore" run brings parked icons back
        // after a power loss (when even the watchdog could not run).
        // Skipped until AutoStart has been primed in the background; the app calls it again then.
        public void UpdateRestoreEntry()
        {
            if (AutoStart.Known is bool autostart)
                AutoStart.SetRestoreEntry(Config.IconsParked && !autostart);
        }

        // Messages for the user (tray balloon); the tray may not exist yet when the first one comes up.
        Action<string, string>? _notify;
        readonly List<(string, string)> _pendingNotices = new();

        public Action<string, string>? Notify
        {
            get => _notify;
            set
            {
                _notify = value;
                if (value == null) return;
                foreach (var (title, text) in _pendingNotices) value(title, text);
                _pendingNotices.Clear();
            }
        }

        void Tell(string title, string text)
        {
            if (_notify != null) _notify(title, text);
            else _pendingNotices.Add((title, text));
        }

        public IReadOnlyList<Native.RECT> GetOtherRects(FenceWindow except) =>
            _windows.Where(w => w != except && w.IsVisible && w.Handle != IntPtr.Zero).Select(w => w.ScreenRect).ToList();

        // The user resized the desktop icons (View menu or Ctrl+wheel): follow along.
        void CheckDesktopIconSize()
        {
            var setting = DesktopIcons.ReadDesktopIconSizeSetting();
            if (setting == _lastIconSizeSetting) return;
            _lastIconSizeSetting = setting;
            int size = DesktopIcons.GetDesktopIconSize() ?? DesktopIconSize;
            IconGrid.Refresh();
            DesktopIconSize = size;
            foreach (var w in _windows) w.Reload();
            LayoutAll();
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
            FenceBackdrop.InvalidateWallpaper();
            string signature = DisplayLayouts.CurrentSignature();
            if (signature != _displaySignature)
            {
                DisplayLayouts.Remember(Config, _displaySignature); // the fences still hold the old setup's positions
                _displaySignature = signature;
                if (!DisplayLayouts.Restore(Config, signature)) DisplayLayouts.BringOffscreenHome(Config);
                foreach (var w in _windows) w.ApplyBounds();
                DisplayLayouts.Remember(Config, signature);
                SaveNow();
            }
            // Windows may have shuffled the icons around for the new screen size.
            _placed.Clear();
            QueueSync(user: false);
        }

        public void WallpaperChanged() => FenceBackdrop.InvalidateWallpaper();

        // Explorer restarted: the desktop view, our backdrops (children of its desktop) and maybe the icon
        // positions are gone. Rebuild everything once it is back.
        readonly DispatcherTimer _explorerTimer = new() { Interval = TimeSpan.FromSeconds(2) };

        public void ExplorerRestarted()
        {
            _explorerTimer.Stop();
            _explorerTimer.Tick -= OnExplorerBack;
            _explorerTimer.Tick += OnExplorerBack;
            _explorerTimer.Start();
        }

        void OnExplorerBack(object? sender, EventArgs e)
        {
            if (DesktopShell.FindListView() == IntPtr.Zero) return; // not yet
            _explorerTimer.Stop();
            DesktopIcons.Reset();
            DesktopEvents.Start(); // the new Explorer's list view
            FenceBackdrop.RecreateAll();
            foreach (var w in _windows) w.AttachToDesktop();
            PrepareDesktop();
            _placed.Clear();
            LayoutAll();
            SettleLayout();
        }

        #region Desktop icons

        // Explorer must leave icons where they are put, and the icons of the old hide-the-files model come back.
        void PrepareDesktop()
        {
            var was = DesktopIcons.EnsureFreeLayout();
            if (was is { } flags && (flags.AutoArrange || flags.SnapToGrid))
            {
                // Remembered so uninstalling gives the desktop its own arrangement back.
                Config.DesktopArrangeBefore ??= (flags.AutoArrange ? 1 : 0) | (flags.SnapToGrid ? 2 : 0);
                Tell("Desko", "Đã tắt \"Auto arrange icons\" và \"Align icons to grid\" của desktop để đặt icon vào các fence.");
            }
            if (Config.LayoutVersion < 3) MigrateFromHiddenFiles();
            IconGrid.Refresh();
            Remember(DesktopIcons.Enumerate());
        }

        // Version 2 kept fenced files on the Desktop with the Hidden attribute and stood in for Recycle Bin & co.
        // with hidden shortcuts. Undo both once: the icons themselves go into the fences now.
        void MigrateFromHiddenFiles()
        {
            // The old configuration stays at hand, should anyone want to go back to version 2.
            try
            {
                string backup = Path.Combine(ConfigStore.AppDataDir, "config.v2-backup.json");
                if (File.Exists(ConfigStore.ConfigPath) && !File.Exists(backup)) File.Copy(ConfigStore.ConfigPath, backup);
            }
            catch (Exception ex) { Log.Error(ex); }

            bool refresh = false;
            foreach (var v in Config.VirtualIcons)
            {
                refresh |= VirtualItems.SetDesktopIconHidden(v.Clsid, false);
                string member = "::" + v.Clsid;
                foreach (var f in NormalFences)
                {
                    for (int i = 0; i < f.Members.Count; i++)
                        if (string.Equals(f.Members[i], v.ShortcutName, StringComparison.OrdinalIgnoreCase)) f.Members[i] = member;
                    for (int i = 0; i < f.Order.Count; i++)
                        if (string.Equals(f.Order[i], v.ShortcutName, StringComparison.OrdinalIgnoreCase)) f.Order[i] = member;
                }
                try
                {
                    string shortcut = Path.Combine(Desktop, v.ShortcutName);
                    if (File.Exists(shortcut)) File.Delete(shortcut);
                }
                catch (Exception ex) { Log.Error(ex); }
            }
            foreach (var path in Config.HiddenByUs) Recovery.SetHidden(path, false);
            Config.VirtualIcons.Clear();
            Config.HiddenByUs.Clear();
            Config.LayoutVersion = 3;
            SaveNow();
            if (refresh) VirtualItems.RefreshDesktop();
        }

        // Icons that were just un-hidden or created show up in Explorer's view a moment later, and right after logon
        // Explorer may still be filling the desktop: look again a few times, further and further apart.
        void SettleLayout()
        {
            int round = 0;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            timer.Tick += (_, _) =>
            {
                if (++round >= 5 || _shuttingDown) timer.Stop();
                timer.Interval = TimeSpan.FromMilliseconds(500 << round); // 0.5, 1, 2, 4, 8 s
                SyncDesktop(user: false);
            };
            timer.Start();
        }

        void Remember(List<DesktopIcons.Icon>? icons)
        {
            if (icons == null) return;
            var now = DateTime.UtcNow;
            foreach (var icon in icons)
                if (!_seen.ContainsKey(icon.Name)) _firstSeen[icon.Name] = now;
            _seen.Clear();
            foreach (var icon in icons) _seen[icon.Name] = icon.Position;
            foreach (var gone in _firstSeen.Keys.Where(n => !_seen.ContainsKey(n)).ToList()) _firstSeen.Remove(gone);
        }

        // When each icon first showed up. Explorer may create an item in one place and move it to where it was
        // created (New > Folder) a moment later, so an icon counts as new for a few seconds.
        readonly Dictionary<string, DateTime> _firstSeen = new(StringComparer.OrdinalIgnoreCase);

        bool IsNew(string name) => !_firstSeen.TryGetValue(name, out var t) || DateTime.UtcNow - t < TimeSpan.FromSeconds(3);

        public int NewZoneId() => _nextZoneId++;

        // A test copy running next to the real Desko (see TestChannel): it must never touch parked icons that are
        // not its own.
        static readonly bool Isolated = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DESKO_INSTANCE"));

        // Places every fence's icons (and parks the ones that must not show).
        public void LayoutAll()
        {
            if (_shuttingDown || Hidden) return; // hidden: the icons are switched off anyway; placed again on show
            _movesDeferred = false;
            var park = DesktopIcons.ParkPoint;
            var moves = new List<(string, Native.POINT)>();
            foreach (var w in _windows) w.CollectIconMoves(moves, park);
            MoveIcons(moves);
            if (Config.ArrangesLooseIcons) PackLoose();
            UpdateZones();
        }

        public void LayoutWindow(FenceWindow w)
        {
            if (_shuttingDown || Hidden) return;
            var moves = new List<(string, Native.POINT)>();
            w.CollectIconMoves(moves, DesktopIcons.ParkPoint);
            MoveIcons(moves);
            UpdateZones();
        }

        // Set while icon moves wait for a drag to end (see DesktopHook.ForeignDragActive).
        bool _movesDeferred;

        // Explorer is running its drag loop (icons or files being dragged): moving icons now would fight with it
        // (and stall the drag). The layout is redone once the button is released.
        bool DeferMoves()
        {
            if (!_hook.ForeignDragActive) return false;
            if (!_movesDeferred) Log.Trace("icon moves deferred: a drag is under way");
            _movesDeferred = true;
            return true;
        }

        void MoveIcons(List<(string Name, Native.POINT At)> moves)
        {
            if (DeferMoves()) return;
            var changed = moves.Where(m => DesktopIcons.Has(m.Name) &&
                                           (!_placed.TryGetValue(m.Name, out var p) || p.X != m.At.X || p.Y != m.At.Y))
                               .ToList();
            if (changed.Count == 0) return;
            Log.Trace($"move {changed.Count} icons: {changed.Count(m => DesktopIcons.IsParked(m.At))} to park, {changed.Count(m => !DesktopIcons.IsParked(m.At))} into place");
            // The watch works from a list of what is parked, which this changes: it starts over afterwards.
            bool watching = Environment.TickCount64 < _watchUntil;
            if (watching) DesktopIcons.Watch.Stop();
            if (!Config.IconsParked && changed.Any(m => DesktopIcons.IsParked(m.At)))
            {
                // Recorded before parking anything, so a crash still brings them back.
                Config.IconsParked = true;
                SaveNow();
                UpdateRestoreEntry();
            }
            DesktopIcons.Move(changed, repost: watching);
            foreach (var (name, at) in changed)
            {
                _placed[name] = at;
                _seen[name] = at;
            }
            // Parking keeps the selection (no selection event follows): an icon selected in a fence stays selected.
            var parked = changed.Where(m => DesktopIcons.IsParked(m.At)).Select(m => m.Name).ToList();
            if (parked.Count > 0 && DesktopIcons.DeselectIfSelected(parked) is int n and > 0) Log.Trace($"deselected {n} icons as they parked");
            RefreshIconRects();
            if (watching) WatchParked(_watchWhy);
        }

        void RefreshIconRects()
        {
            DesktopIcons.IconRects = _seen.Values.Where(p => !DesktopIcons.IsParked(p)).Select(IconGrid.CellRect).ToArray();
        }

        // The icon areas of the fences, for the mouse hook.
        public void UpdateZones()
        {
            _hook.Zones = Hidden ? Array.Empty<DesktopHook.Zone>()
                : _windows.Where(w => w.IsDesktopFence && w.ShowsIcons)
                          .Select(w => new DesktopHook.Zone(w.BodyRect, w.ZoneId)).ToArray();
        }

        // A fence moved or changed size: its icons follow.
        public void FenceBoundsChanged(FenceWindow w)
        {
            if (w.IsDesktopFence) LayoutWindow(w);
            else UpdateZones();
        }

        // ...and once it is let go, the icons it now covers make room.
        public void FenceMoveEnded() => QueueSync(user: false);

        void QueueSync(bool user, int delayMs = 350, string? why = null)
        {
            _pendingUserMove |= user;
            if (why != null && !_syncWhy.Contains(why)) _syncWhy.Add(why);
            _syncTimer.Stop();
            _syncTimer.Interval = TimeSpan.FromMilliseconds(delayMs);
            _syncTimer.Start();
        }

        readonly List<string> _syncWhy = new(); // for the trace
        string _lastSyncWhy = "";

        // Explorer brings parked icons back on screen by itself (about 0.1 s after icons are dropped on the desktop,
        // when it rebuilds its list...). DesktopIcons.Watch looks out for that on a thread of its own and sends them
        // back at once; it also reports when a dropped icon has landed, so the look at the desktop that follows a
        // drop (and the tidying up it does) happens right then rather than after a guessed delay.
        string _watchWhy = "";
        long _watchUntil;

        void WatchParked(string why, Native.POINT? dropFrom = null)
        {
            if (_shuttingDown || Hidden) return;
            var parked = new List<(string, byte[], Native.POINT, int)>();
            foreach (var (name, at) in _placed)
                if (DesktopIcons.IsParked(at) && DesktopIcons.PidlBytes(name) is { } bytes) parked.Add((name, bytes, at, DesktopIcons.IndexOf(name)));
            (string, byte[], Native.POINT)? dropped = null;
            if (dropFrom is { } from)
            {
                var hit = _seen.FirstOrDefault(s => !DesktopIcons.IsParked(s.Value) && Native.Contains(IconGrid.CellRect(s.Value), from));
                if (hit.Key != null && DesktopIcons.PidlBytes(hit.Key) is { } bytes) dropped = (hit.Key, bytes, hit.Value);
            }
            if (parked.Count == 0 && dropped == null) return;
            _watchWhy = why;
            _watchUntil = Environment.TickCount64 + 600;
            DesktopIcons.Watch.Start(new(parked, dropped, 600, why));
        }

        void OnReparked(DesktopIcons.Watch.Request request, List<(string Name, Native.POINT At)> found, long ms)
        {
            foreach (var (name, _) in found)
                if (_placed.TryGetValue(name, out var at)) _seen[name] = at;
            RefreshIconRects();
            Log.Trace($"explorer brought {found.Count} parked icons on screen; sent back {ms} ms after {request.Why} " +
                      $"(\"{found[0].Name}\" was at {found[0].At.X},{found[0].At.Y})");
        }

        void OnDropLanded(DesktopIcons.Watch.Request request, long ms)
        {
            Log.Trace($"dropped icon landed {ms} ms after {request.Why}");
            if (_pendingUserMove) QueueSync(user: true, delayMs: 1, why: "landed");
        }

        // After a file change Explorer updates its view in steps (a rename re-adds the icon a moment later, wherever
        // it likes): look once soon and once more when it has surely settled.
        DispatcherTimer? _lateSync;

        void QueueSyncTwice()
        {
            QueueSync(user: false);
            _lateSync ??= new DispatcherTimer(TimeSpan.FromMilliseconds(1600), DispatcherPriority.Background, (_, _) =>
            {
                _lateSync!.Stop();
                QueueSync(user: false);
            }, UI);
            _lateSync.Stop();
            _lateSync.Start();
        }

        // A drag passing over the fences: the one under it takes the drop (see FenceWindow.SetDragMode).
        // The drop reaches us just after the button is released, so the fences keep taking it a moment longer.
        DispatcherTimer? _dragModeOff;

        void SetDragZone(int zone)
        {
            _dragModeOff?.Stop();
            if (zone >= 0)
            {
                foreach (var w in _windows) w.SetDragMode(w.ZoneId == zone);
                return;
            }
            _dragModeOff ??= new DispatcherTimer(TimeSpan.FromMilliseconds(300), DispatcherPriority.Normal, (_, _) =>
            {
                _dragModeOff!.Stop();
                foreach (var w in _windows) w.SetDragMode(false);
            }, UI);
            _dragModeOff.Start();
        }

        // The user dragged desktop icons: Explorer moved them to wherever they were dropped.
        void OnIconsDragged(Native.POINT from, Native.POINT to)
        {
            NoteDrag(from, to);
            QueueSync(user: true, delayMs: 150, why: "drop");
            // Explorer can take a moment to report a drop of many icons.
            var late = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
            late.Tick += (_, _) => { late.Stop(); QueueSync(user: true); };
            late.Start();
        }

        // Explorer moves dropped icons by exactly how far the mouse travelled. Only icons that moved by a recent
        // drag's distance count as moved by the user: anything else that shifted icons meanwhile (Explorer itself,
        // a restore, another program) must never take them out of their fences.
        readonly List<(DateTime When, Native.POINT Delta, Native.POINT Drop)> _recentDrags = new();

        void NoteDrag(Native.POINT from, Native.POINT to)
        {
            _recentDrags.RemoveAll(d => DateTime.UtcNow - d.When > TimeSpan.FromSeconds(10));
            _recentDrags.Add((DateTime.UtcNow, new Native.POINT(to.X - from.X, to.Y - from.Y), to));
        }

        // Moved by a recent drag's distance, or (the icon that was under the mouse) now right where it was dropped.
        bool MovedByUser(Native.POINT before, Native.POINT now)
        {
            var cell = IconGrid.CellRect(now);
            return _recentDrags.Any(d => DateTime.UtcNow - d.When < TimeSpan.FromSeconds(10) &&
                ((Math.Abs(now.X - before.X - d.Delta.X) <= 12 && Math.Abs(now.Y - before.Y - d.Delta.Y) <= 12) ||
                 Native.Contains(cell, d.Drop)));
        }

        // Parked icons must never be selected: a drag of the selection would carry them along (Explorer then moves
        // and redraws them all, and the drag stalls), and Ctrl+A then Delete would delete icons nobody can see.
        // Selection changes come in bursts (a click deselects many, Ctrl+A selects all), so this runs once after.
        DispatcherTimer? _selectionTimer;

        void OnSelectionChanged()
        {
            if (_shuttingDown) return;
            _selectionTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(30), DispatcherPriority.Normal, (_, _) =>
            {
                _selectionTimer!.Stop();
                var parked = _placed.Where(p => DesktopIcons.IsParked(p.Value)).Select(p => p.Key).ToList();
                int n = parked.Count == 0 ? 0 : DesktopIcons.DeselectIfSelected(parked);
                if (n > 0) Log.Trace($"deselected {n} parked icons");
            }, UI);
            _selectionTimer.Stop();
            _selectionTimer.Start();
        }

        // Where the user last pressed a mouse button on the desktop. New items created there (New > Folder,
        // Paste) join a fence; items an app drops onto the desktop do not just because Windows put them under one.
        (DateTime When, Native.POINT Where) _lastDesktopPress;

        bool PressedRecentlyIn(FenceWindow w) =>
            DateTime.UtcNow - _lastDesktopPress.When < TimeSpan.FromMinutes(2) && Native.Contains(w.BodyRect, _lastDesktopPress.Where);

        FenceWindow? DesktopFenceAt(Native.POINT pt) =>
            _windows.FirstOrDefault(w => w.IsDesktopFence && w.ShowsIcons && !Hidden && Native.Contains(w.BodyRect, pt));

        // An icon (by position) that overlaps a fence's area, rolled up or not. A few pixels of touching are fine.
        bool CoveredByFence(Native.POINT position)
        {
            var cell = IconGrid.CellRect(position);
            var inner = new Native.RECT(cell.Left + 6, cell.Top + 4, cell.Right - 6, cell.Bottom - 4);
            return _windows.Any(w => w.Handle != IntPtr.Zero && IconGrid.Overlaps(w.RestingRect, inner));
        }

        // Compares what is on the desktop with what the fences expect, after the user moved icons with the mouse
        // (user = true) or after files appeared / changed. Explorer moved the icons itself in those cases:
        //  - a fenced icon dropped outside every fence leaves its fence;
        //  - an icon dropped on a fence's icon area joins that fence, and so does a new item created where the
        //    user just clicked in a fence (New > Folder, Paste);
        //  - any other icon under a fence (a fence moved over it, a download Windows put there) makes room;
        //  - anything else of ours that is not where we put it is put back.
        public void SyncDesktop(bool user)
        {
            if (_shuttingDown || Hidden || DeferMoves()) return; // after a drag: looked at once it is dropped
            var flags = DesktopIcons.EnsureFreeLayout();
            if (flags is { } f && (f.AutoArrange || f.SnapToGrid))
            {
                Tell("Desko", "Desko cần tắt \"Auto arrange icons\" và \"Align icons to grid\" để giữ icon trong fence.");
                _placed.Clear();
            }
            var icons = DesktopIcons.Enumerate();
            if (icons == null) return;
            IconGrid.Refresh(); // the desktop icon size may have changed
            // Members that appeared or went away since the last look change every fence's slots.
            foreach (var w in _windows) w.RefreshIconOrder();

            var owner = new Dictionary<string, FenceConfig>(StringComparer.OrdinalIgnoreCase);
            foreach (var fence in NormalFences)
                foreach (var name in fence.Members) owner.TryAdd(name, fence);

            // Diagnostics: parked members Explorer moved (dragged along with a selection) or brought back on screen.
            int unparkedByOthers = icons.Count(i => owner.ContainsKey(i.Name) && !DesktopIcons.IsParked(i.Position) &&
                                                   _placed.TryGetValue(i.Name, out var w) && DesktopIcons.IsParked(w));
            int displaced = icons.Count(i => owner.ContainsKey(i.Name) && DesktopIcons.IsParked(i.Position) &&
                                            _placed.TryGetValue(i.Name, out var w) && DesktopIcons.IsParked(w) && Far(w, i.Position));

            bool packing = Config.ArrangesLooseIcons;
            var mainWork = Native.WorkAreaFromPoint(new Native.POINT(0, 0));
            var joins = new Dictionary<FenceWindow, List<(string Name, Native.POINT Center)>>();
            var leaves = new List<(FenceConfig Fence, string Name, Native.POINT At)>();
            var loose = new List<(string Name, Native.POINT At)>();

            foreach (var icon in icons)
            {
                var pos = icon.Position;
                bool known = _seen.TryGetValue(icon.Name, out var before);
                bool moved = !known || Far(before, pos);
                bool parked = DesktopIcons.IsParked(pos);
                var center = IconGrid.CellCenter(pos);
                var target = parked ? null : DesktopFenceAt(center);
                // Dragged by the user (it moved exactly as far as a drag just did), not shifted by something else.
                bool dragged = user && known && moved && !parked && MovedByUser(before, pos);

                if (owner.TryGetValue(icon.Name, out var fence))
                {
                    if (_placed.TryGetValue(icon.Name, out var want) && !Far(want, pos)) continue;
                    if (dragged)
                    {
                        if (target != null) Join(joins, target, icon.Name, center);
                        else leaves.Add((fence, icon.Name, pos));
                    }
                    else _placed.Remove(icon.Name); // put back below
                }
                else if (parked) { if (!Isolated) loose.Add((icon.Name, pos)); } // stranded by a crash
                else if (target != null && (dragged || (IsNew(icon.Name) && PressedRecentlyIn(target))))
                    Join(joins, target, icon.Name, center);
                else if (packing && Native.Contains(mainWork, center)) continue; // packed below, all together
                else if (CoveredByFence(pos)) loose.Add((icon.Name, pos));
                else if (dragged && Config.AlignLooseIcons && !IconGrid.IsAligned(pos)) loose.Add((icon.Name, pos));
            }
            Remember(icons);
            Log.Trace($"sync ({_lastSyncWhy}) user={user}: {icons.Count} icons, {(user ? DesktopIcons.SelectedCount() : -1)} selected, " +
                      $"{displaced} parked moved off their spot, {unparkedByOthers} parked members found on screen, " +
                      $"{leaves.Count} leave, {joins.Sum(j => j.Value.Count)} join, {loose.Count} to free cells");
            _lastSyncWhy = "other"; // direct calls (not through the timer)

            if (leaves.Count > 0)
            {
                Checkpoint("Đưa mục ra desktop");
                foreach (var group in leaves.GroupBy(l => l.Fence))
                {
                    var names = group.Select(l => l.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    group.Key.Members.RemoveAll(names.Contains);
                    group.Key.Order.RemoveAll(names.Contains);
                    foreach (var n in names) _placed.Remove(n);
                }
                if (Config.AlignLooseIcons && !packing) loose.AddRange(leaves.Select(l => (l.Name, l.At)));
                SaveSoon();
            }
            if (joins.Count > 0)
            {
                Checkpoint("Đưa mục vào fence");
                foreach (var (window, list) in joins)
                {
                    var ordered = list.OrderBy(j => j.Center.Y / Math.Max(1, IconGrid.Cell.Y)).ThenBy(j => j.Center.X).ToList();
                    AddMembers(window.Config, ordered.Select(j => j.Name).ToList(), ordered[0].Center, layout: false);
                }
            }
            if (leaves.Count > 0 || joins.Count > 0)
                foreach (var w in _windows) w.RefreshIconOrder();
            if (loose.Count > 0) PlaceLoose(loose);
            LayoutAll(); // only what is not where it belongs actually moves
        }

        static void Join(Dictionary<FenceWindow, List<(string, Native.POINT)>> joins, FenceWindow w, string name, Native.POINT center)
        {
            if (!joins.TryGetValue(w, out var list)) joins[w] = list = new List<(string, Native.POINT)>();
            list.Add((name, center));
        }

        static bool Far(Native.POINT a, Native.POINT b) => Math.Abs(a.X - b.X) > 2 || Math.Abs(a.Y - b.Y) > 2;

        // Loose icons go to the nearest free grid cell outside every fence. Parked ones have no place of their own:
        // they go near `parkedNear` (the fence they come from), or else to the top-left of the main screen.
        void PlaceLoose(List<(string Name, Native.POINT At)> icons, Native.POINT? parkedNear = null)
        {
            var moving = icons.Select(i => i.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var blocked = _windows.Where(w => w.Handle != IntPtr.Zero).Select(w => w.RestingRect).ToList();
            var others = _seen.Where(s => !moving.Contains(s.Key) && !DesktopIcons.IsParked(s.Value))
                              .Select(s => IconGrid.CellRect(s.Value)).ToList();
            var home = Native.WorkAreaFromPoint(new Native.POINT(0, 0));
            var moves = new List<(string, Native.POINT)>();
            foreach (var (name, at) in icons)
            {
                var near = !DesktopIcons.IsParked(at) ? at : parkedNear ?? IconGrid.InCell(home.Left, home.Top);
                var spot = IconGrid.FreeCells(near, 1, blocked, others)[0];
                others.Add(IconGrid.CellRect(spot));
                moves.Add((name, spot));
                _placed.Remove(name);
            }
            if (moves.Count == 0) return;
            Log.Trace($"place {moves.Count} loose icons, e.g. {icons[0].At.X},{icons[0].At.Y} -> {moves[0].Item2.X},{moves[0].Item2.Y}");
            DesktopIcons.Move(moves);
            foreach (var (name, at) in moves) _seen[name] = at;
        }

        // Tray command: packs the icons outside the fences once, whatever the setting.
        public void ArrangeLooseIcons()
        {
            if (Hidden) return;
            var icons = DesktopIcons.Enumerate();
            if (icons == null) return;
            Remember(icons);
            PackLoose();
        }

        // Keeps the icons outside the fences on the main screen packed into columns from its top-left corner, in
        // their current order and flowing around the fences: Windows' "Auto arrange icons", which has to stay off
        // (it would pull the fenced icons into its columns too). Dropping an icon between others puts it there in
        // the order, as with Windows. Works from what was last seen; only icons not already in place move.
        void PackLoose()
        {
            if (DeferMoves()) return;
            var members = NormalFences.SelectMany(f => f.Members).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var work = Native.WorkAreaFromPoint(new Native.POINT(0, 0));
            var cell = IconGrid.Cell;
            var loose = _seen.Where(s => !members.Contains(s.Key) && !DesktopIcons.IsParked(s.Value) &&
                                         Native.Contains(work, IconGrid.CellCenter(s.Value)))
                             .OrderBy(s => (IconGrid.CellCenter(s.Value).X - work.Left) / cell.X)
                             .ThenBy(s => s.Value.Y)
                             .ToList();
            var fences = _windows.Where(w => w.Handle != IntPtr.Zero).Select(w => w.RestingRect).ToList();
            var moves = new List<(string, Native.POINT)>();
            int next = 0;
            for (int x = work.Left; x + cell.X <= work.Right && next < loose.Count; x += cell.X)
                for (int y = work.Top; y + cell.Y <= work.Bottom && next < loose.Count; y += cell.Y)
                {
                    var r = new Native.RECT(x, y, x + cell.X, y + cell.Y);
                    if (fences.Any(f => IconGrid.Overlaps(f, r))) continue;
                    var (name, at) = loose[next++];
                    var spot = IconGrid.InCell(x, y);
                    if (Far(at, spot)) moves.Add((name, spot));
                }
            // Anything that does not fit stays where it is.
            if (moves.Count == 0) return;
            Log.Trace($"pack: {moves.Count} loose icons move");
            DesktopIcons.Move(moves);
            foreach (var (name, at) in moves) _seen[name] = at;
            RefreshIconRects();
        }

        // Icons that leave the fences for good (fence deleted, turned into a portal, tab removed). Parked ones
        // come back on screen next to where the fence is (`from`); `all` also moves the visible ones out of the way.
        void ReleaseIcons(IEnumerable<string> names, bool all, FenceConfig? from = null)
        {
            var list = new List<(string, Native.POINT)>();
            foreach (var name in names)
            {
                if (!_seen.TryGetValue(name, out var at)) continue;
                _placed.Remove(name);
                if (all || DesktopIcons.IsParked(at)) list.Add((name, at));
            }
            if (list.Count > 0) PlaceLoose(list, from is null ? null : IconGrid.InCell(from.X + from.Width, from.Y));
        }

        // At exit: every parked icon back on screen, near its fence. Returns false if Explorer could not be reached.
        bool ReleaseParked()
        {
            var icons = DesktopIcons.Enumerate();
            if (icons == null) return !Config.IconsParked;
            var visible = icons.Where(i => !DesktopIcons.IsParked(i.Position)).Select(i => IconGrid.CellRect(i.Position)).ToList();
            var owner = new Dictionary<string, FenceConfig>(StringComparer.OrdinalIgnoreCase);
            foreach (var fence in NormalFences)
                foreach (var name in fence.Members) owner.TryAdd(name, fence);

            var home = Native.WorkAreaFromPoint(new Native.POINT(0, 0));
            var moves = new List<(string, Native.POINT)>();
            foreach (var group in icons.Where(i => DesktopIcons.IsParked(i.Position) && (!Isolated || owner.ContainsKey(i.Name)))
                                       .GroupBy(i => owner.TryGetValue(i.Name, out var f) ? f : null))
            {
                var near = group.Key is FenceConfig f ? IconGrid.InCell(f.X + f.Width, f.Y) : IconGrid.InCell(home.Left, home.Top);
                var spots = IconGrid.FreeCells(near, group.Count(), Array.Empty<Native.RECT>(), visible);
                int k = 0;
                foreach (var icon in group)
                {
                    var spot = spots[k++];
                    visible.Add(IconGrid.CellRect(spot));
                    moves.Add((icon.Name, spot));
                }
            }
            DesktopIcons.Move(moves);
            return true;
        }

        #endregion

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

        // A click outside a folder portal (another fence, the desktop, any other app) drops its selection.
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
            return Native.Contains(w.ScreenRect, pt);
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
            LayoutAll();
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
            LayoutAll();
            SaveSoon();
        }

        #endregion

        #region Shell items & clipboard

        // Dropped items that are not files. Desktop icons (Recycle Bin, This PC...) simply join the fence;
        // anything else (a Start-menu app...) gets a shortcut, on the Desktop or in the portal's folder.
        public void AddShellItems(FenceConfig fence, IReadOnlyList<IntPtr> pidls, Native.POINT? at)
        {
            string folder = fence.IsPortal ? fence.FolderPath : Desktop;
            var names = new List<string>();
            foreach (var pidl in pidls)
            {
                string? clsid = fence.IsPortal ? null : VirtualItems.DesktopIconClsid(pidl);
                if (clsid != null)
                {
                    names.Add("::" + clsid);
                    continue;
                }
                string display = VirtualItems.GetName(pidl, parsing: false) ?? "Shortcut";
                string safe = new string(display.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray()).Trim();
                string path = UniquePath(folder, (safe.Length > 0 ? safe : "Shortcut") + ".lnk");
                if (!VirtualItems.CreateShortcut(pidl, path)) continue;
                names.Add(Path.GetFileName(path));
            }
            if (names.Count == 0) return;
            if (fence.IsPortal) SaveSoon();
            else
            {
                AddMembers(fence, names, at);
                QueueSync(user: false); // new shortcuts reach the desktop view a moment later
            }
        }

        public static void PutOnClipboard(IReadOnlyList<string> paths, bool cut)
        {
            if (paths.Count == 0) return;
            var list = new System.Collections.Specialized.StringCollection();
            list.AddRange(paths.ToArray());
            var data = new DataObject();
            data.SetFileDropList(list);
            data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(cut ? 2 : 5)));
            try { Clipboard.SetDataObject(data, true); }
            catch (Exception ex) { Log.Error(ex); }
        }

        void StartMessageHost()
        {
            try
            {
                _messageHost = new HwndSource(new HwndSourceParameters("DeskoMessages")
                {
                    Width = 0,
                    Height = 0,
                    WindowStyle = 0,
                    ParentWindow = new IntPtr(-3), // HWND_MESSAGE
                });
                _messageHost.AddHook((IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled) =>
                {
                    if (msg == Native.WM_HOTKEY)
                    {
                        if (w.ToInt32() == HotkeySearch) ShowGlobalSearch();
                        else if (w.ToInt32() == HotkeyDesktop) ToggleDesktop();
                    }
                    return IntPtr.Zero;
                });
                // Global hotkeys: Ctrl+Alt+F search every fence, Ctrl+Alt+D show the desktop.
                const uint mods = Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT;
                if (!Native.RegisterHotKey(_messageHost.Handle, HotkeySearch, mods, 0x46))
                    Log.Error(new InvalidOperationException("Ctrl+Alt+F is already taken by another app"));
                if (!Native.RegisterHotKey(_messageHost.Handle, HotkeyDesktop, mods, 0x44))
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
            QueueSync(user: false); // loose icons under the new fences make room
            SaveSoon();
            return created;
        }

        #endregion

        #region Undo

        // Fence-level undo (Ctrl+Z in a fence, or the tray menu): a snapshot of the fences taken before each
        // structural change. File operations are not included; Explorer's own Ctrl+Z covers those.
        sealed record UndoEntry(string Description, string Snapshot);
        readonly List<UndoEntry> _undo = new();

        public string? UndoDescription => _undo.Count > 0 ? _undo[^1].Description : null;

        public void Checkpoint(string description)
        {
            var snapshot = new AppConfig { Fences = Config.Fences };
            _undo.Add(new UndoEntry(description, System.Text.Json.JsonSerializer.Serialize(snapshot, ConfigJson.Default.AppConfig)));
            if (_undo.Count > 25) _undo.RemoveAt(0);
        }

        public void Undo()
        {
            if (_undo.Count == 0)
            {
                Tell("Desko", "Không còn thao tác nào để hoàn tác.");
                return;
            }
            var entry = _undo[^1];
            _undo.RemoveAt(_undo.Count - 1);
            var snapshot = System.Text.Json.JsonSerializer.Deserialize(entry.Snapshot, ConfigJson.Default.AppConfig);
            if (snapshot == null) return;

            var before = NormalFences.SelectMany(f => f.Members).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var w in _windows.ToList())
            {
                w.ClosingByManager = true;
                w.Close();
            }
            Config.Fences = snapshot.Fences;
            var after = NormalFences.SelectMany(f => f.Members).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var group in Config.Fences.GroupBy(GroupKey).ToList())
                OpenWindow(group.FirstOrDefault(t => t.ActiveTab) ?? group.First(), rename: false);
            ReleaseIcons(before.Where(n => !after.Contains(n)), all: false);
            LayoutAll();
            QueueSync(user: false);
            SaveNow();
            Tell("Đã hoàn tác", entry.Description);
        }

        #endregion

        #region Show desktop & global search

        const int HotkeySearch = 1, HotkeyDesktop = 2;

        // Same as Win+D: the windows step aside and the fences (on the desktop) are in full view.
        public static void ToggleDesktop()
        {
            try
            {
                var type = Type.GetTypeFromProgID("Shell.Application");
                if (type == null) return;
                var shell = Activator.CreateInstance(type);
                try { type.InvokeMember("ToggleDesktop", BindingFlags.InvokeMethod, null, shell, null); }
                finally { if (shell != null) Marshal.ReleaseComObject(shell); }
            }
            catch (Exception ex) { Log.Error(ex); }
        }

        static bool DesktopInFront()
        {
            var cls = Native.ClassOf(Native.GetForegroundWindow());
            return cls is "Progman" or "WorkerW" or "Shell_TrayWnd";
        }

        public void ShowGlobalSearch()
        {
            var results = new List<SearchWindow.Result>();
            foreach (var f in Config.Fences)
            {
                List<string> paths;
                try
                {
                    paths = f.IsPortal
                        ? Directory.Exists(f.FolderPath) ? Directory.EnumerateFileSystemEntries(f.FolderPath).Take(3000).ToList() : new List<string>()
                        : f.Members.Select(DesktopShell.MemberPath).OfType<string>().ToList();
                }
                catch (Exception ex) { Log.Error(ex); continue; }

                foreach (var path in paths)
                {
                    string name;
                    if (path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
                        name = VirtualItems.DisplayName(path) ?? path;
                    else
                    {
                        FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
                        if (f.IsPortal && (info.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                        name = ShellInfo.GetDisplayNameCached(path, info.LastWriteTimeUtc, Path.GetFileName(path));
                    }
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
            try
            {
                bool shellItem = path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(shellItem ? "explorer.exe" : path)
                {
                    UseShellExecute = true,
                    Arguments = shellItem ? path : "",
                    WorkingDirectory = shellItem ? "" : Path.GetDirectoryName(path) ?? "",
                });
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) { }
            catch (Exception ex) { Dialogs.Info("Không mở được", ex.Message); }
        }

        // Shows an item in its fence: brings the desktop forward, opens the right tab and selects the item.
        public void Reveal(FenceConfig tab, string path)
        {
            var window = _windows.FirstOrDefault(w => TabsOf(w.Config).Contains(tab));
            if (window == null) return;
            if (Hidden) SetHidden(false);
            if (!DesktopInFront()) ToggleDesktop();
            window.SwitchTab(tab);
            window.RevealItem(path);
        }

        #endregion

        #region Membership

        public static string MemberName(string path) =>
            path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) ? path.Substring(6) : Path.GetFileName(path.TrimEnd('\\'));

        // Puts desktop icons into a fence (taking them out of any other one), at the slot under `at` if given.
        public void AddMembers(FenceConfig fence, IReadOnlyCollection<string> names, Native.POINT? at, bool layout = true)
        {
            if (names.Count == 0) return;
            var set = names.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var other in NormalFences.Where(f => f != fence))
            {
                other.Members.RemoveAll(set.Contains);
                other.Order.RemoveAll(set.Contains);
            }
            foreach (var n in names)
                if (!fence.Members.Contains(n, StringComparer.OrdinalIgnoreCase)) fence.Members.Add(n);

            var window = _windows.FirstOrDefault(w => w.Config == fence);
            if (at is Native.POINT p && window != null) window.InsertAt(names, p);
            else
            {
                var order = fence.Order.Where(n => !set.Contains(n)).ToList();
                order.AddRange(names);
                fence.Order = order;
            }
            foreach (var w in _windows) w.ScheduleReload();
            SaveSoon();
            if (layout)
            {
                foreach (var w in _windows) w.RefreshIconOrder();
                LayoutAll();
            }
        }

        public void RenameMember(string? oldName, string? newName)
        {
            if (string.IsNullOrEmpty(oldName) || string.IsNullOrEmpty(newName)) return;
            if (_placed.Remove(oldName, out var placed)) _placed[newName] = placed;
            if (_seen.Remove(oldName, out var seen)) _seen[newName] = seen;
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
        public void ImportToDesktop(FenceConfig fence, IReadOnlyList<string> files, bool copy, Native.POINT? at, IntPtr owner)
        {
            var onDesktop = files.Where(IsDesktopItem).ToList();
            var others = files.Except(onDesktop).ToList();
            var names = new List<string>();

            if (copy)
            {
                var before = DesktopSnapshot();
                FileOps.Copy(files, Desktop, owner, renameOnCollision: onDesktop.Count > 0);
                names.AddRange(DesktopSnapshot().Where(n => !before.Contains(n)));
            }
            else
            {
                names.AddRange(onDesktop.Select(f => Path.GetFileName(f.TrimEnd('\\'))));
                if (others.Count > 0)
                {
                    var before = DesktopSnapshot();
                    FileOps.Move(others, Desktop, owner);
                    names.AddRange(DesktopSnapshot().Where(n => !before.Contains(n)));
                    names.AddRange(others.Select(f => Path.GetFileName(f.TrimEnd('\\')))
                                         .Where(n => Exists(Path.Combine(Desktop, n))));
                    ForgetMissing(others);
                }
            }
            AddMembers(fence, names.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), at);
            QueueSync(user: false); // new files reach the desktop view a moment later
        }

        public static bool IsDesktopItem(string path)
        {
            string? parent = Path.GetDirectoryName(path.TrimEnd('\\'));
            return PathEquals(parent, Desktop) || PathEquals(parent, DesktopShell.PublicDesktopPath);
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
            if (Hidden) w.HideFence();
            return w;
        }

        // If Explorer restarts it takes our (Progman-owned) windows down with it; bring them back.
        void OnWindowClosed(FenceWindow w)
        {
            _windows.Remove(w);
            UpdateZones();
            if (_shuttingDown || w.ClosingByManager || !Config.Fences.Contains(w.Config)) return;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            timer.Tick += (_, _) =>
            {
                if (DesktopShell.Progman == IntPtr.Zero) return;
                timer.Stop();
                if (!_shuttingDown && Config.Fences.Contains(w.Config) && _windows.All(x => x.Config != w.Config))
                    OpenWindow(w.Config, rename: false);
                ExplorerRestarted();
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
            var w = OpenWindow(f, rename);
            QueueSync(user: false); // icons under the new fence make room
            return w;
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
                : $"Xóa {what}?\nCác icon bên trong vẫn ở lại trên Desktop (không file nào bị xóa).";
            if (!Dialogs.Confirm(wholeFence ? "Xóa fence" : "Xóa tab", msg)) return;
            Checkpoint($"Xóa {what}");

            foreach (var t in removing) Config.Fences.Remove(t);
            var released = removing.Where(t => !t.IsPortal).SelectMany(t => t.Members).ToList();

            var remaining = tabs.Except(removing).ToList();
            if (remaining.Count > 0)
            {
                if (remaining.Count == 1) remaining[0].GroupId = "";
                w.SwitchTab(remaining[0]);
                w.RebuildTabs();
                ReleaseIcons(released, all: true, from: f); // the fence stays: its old icons must make room
            }
            else
            {
                w.ClosingByManager = true;
                w.Close();
                ReleaseIcons(released, all: false, from: f); // they stay where the fence was
            }
            LayoutAll();
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
                        "Các icon đang có trong fence sẽ được đưa ra ngoài Desktop. Tiếp tục?")) return;
                Checkpoint($"Chuyển \"{f.Title}\" thành Folder Portal");
                var released = f.Members.ToList();
                f.Members.Clear();
                f.Title = new DirectoryInfo(folder).Name;
                f.IsPortal = true;
                ReleaseIcons(released, all: true, from: f);
            }
            f.IsPortal = true;
            f.FolderPath = folder;
            f.Order.Clear();
            w.ModeChanged();
            LayoutAll();
            SaveSoon();
        }

        // Version 1.0 moved fenced files into %USERPROFILE%\DeskFences\<fence>. Bring them back
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

        // Double-click on the desktop: fences and every desktop icon go away (and come back) together.
        public void SetHidden(bool hide)
        {
            if (Hidden == hide) return;
            Hidden = hide;
            foreach (var w in _windows)
            {
                if (hide) w.FadeTo(0, () => { if (Hidden) w.HideFence(); });
                else
                {
                    w.ShowFence();
                    w.FadeTo(1);
                }
            }

            if (hide && DesktopShell.IconsVisible)
            {
                Config.IconsHiddenByUs = true;
                SaveNow();
                DesktopShell.SetIconsVisible(false);
            }
            else if (!hide && Config.IconsHiddenByUs)
            {
                DesktopShell.SetIconsVisible(true);
                Config.IconsHiddenByUs = false;
                SaveNow();
            }
            UpdateZones();
            if (!hide) QueueSync(user: false); // whatever changed on the desktop meanwhile
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
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
                };
                // New, removed or renamed items: their icons appear / disappear in the view a moment later.
                w.Created += (_, e) => UI.BeginInvoke(() => { Log.Trace($"file created: {e.Name}"); WatchParked("a new file"); QueueSyncTwice(); QueueTidy(e.FullPath); });
                w.Deleted += (_, e) => UI.BeginInvoke(() => { Log.Trace($"file deleted: {e.Name}"); WatchParked("a deleted file"); if (IsFenced(e.Name)) DesktopChanged(); });
                w.Renamed += (_, e) => UI.BeginInvoke(() =>
                {
                    Log.Trace($"file renamed: {e.OldName} -> {e.Name}");
                    WatchParked("a renamed file");
                    RenameMember(e.OldName, e.Name);
                    DesktopChanged();
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

        // A fenced item went away or an item was renamed: the fences close the gap / keep it in place.
        void DesktopChanged()
        {
            foreach (var w in _windows) w.RefreshIconOrder();
            QueueSyncTwice();
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
                    AddMembers(fence, list.Select(Path.GetFileName).OfType<string>().ToList(), null);
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
