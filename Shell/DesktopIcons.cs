using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Desko.Core;
using Desko.Interop;
using Desko.Models;

namespace Desko.Shell
{
    // The real desktop icons, reached through Explorer's own desktop view (ShellWindows → desktop browser →
    // IFolderView2). Fences hold their items by placing these icons inside them; icons that must not show
    // (a rolled-up fence, an inactive tab, rows scrolled out of view) are parked far off screen.
    // Every call is cross-process COM into Explorer and runs on the UI thread, where the proxy lives.
    internal static class DesktopIcons
    {
        [ComImport, Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
        interface IShellWindows
        {
            void _get_Count();
            void _Item();
            void _NewEnum();
            void _Register();
            void _RegisterPending();
            void _Revoke();
            void _OnNavigate();
            void _OnActivated();
            [return: MarshalAs(UnmanagedType.IDispatch)]
            object FindWindowSW([In] ref object loc, [In] ref object locRoot, int swClass, out int hwnd, int options);
        }

        [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IOleServiceProvider
        {
            [PreserveSig] int QueryService(ref Guid service, ref Guid riid, out IntPtr ppv);
        }

        [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellBrowser
        {
            void _GetWindow();
            void _ContextSensitiveHelp();
            void _InsertMenusSB();
            void _SetMenuSB();
            void _RemoveMenusSB();
            void _SetStatusTextSB();
            void _EnableModelessSB();
            void _TranslateAcceleratorSB();
            void _BrowseObject();
            void _GetViewStateStream();
            void _GetControlWindow();
            void _SendControlMsg();
            [PreserveSig] int QueryActiveShellView(out IntPtr view);
        }

        [ComImport, Guid("1AF3A467-214F-4298-908E-06B03E0B39F9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IFolderView2
        {
            // IFolderView
            void _GetCurrentViewMode();
            void _SetCurrentViewMode();
            void _GetFolder();
            [PreserveSig] int Item(int index, out IntPtr pidl);
            [PreserveSig] int ItemCount(uint flags, out int count);
            void _Items();
            void _GetSelectionMarkedItem();
            void _GetFocusedItem();
            [PreserveSig] int GetItemPosition(IntPtr pidl, out Native.POINT pt);
            [PreserveSig] int GetSpacing(out Native.POINT spacing);
            void _GetDefaultSpacing();
            void _GetAutoArrange();
            void _SelectItem();
            [PreserveSig]
            int SelectAndPositionItems(uint count, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] pidls,
                                       [MarshalAs(UnmanagedType.LPArray)] Native.POINT[]? points, uint flags);
            // IFolderView2, up to GetViewModeAndIconSize
            void _SetGroupBy(); void _GetGroupBy(); void _SetViewProperty(); void _GetViewProperty();
            void _SetTileViewProperties(); void _SetExtendedTileViewProperties(); void _SetText();
            [PreserveSig] int SetCurrentFolderFlags(uint mask, uint flags);
            [PreserveSig] int GetCurrentFolderFlags(out uint flags);
            void _GetSortColumnCount(); void _SetSortColumns(); void _GetSortColumns(); void _GetItem(); void _GetVisibleItem();
            void _GetSelectedItem(); void _GetSelection();
            [PreserveSig] int GetSelectionState(IntPtr pidl, out uint flags);
            void _InvokeVerbOnSelection();
            void _SetViewModeAndIconSize();
            [PreserveSig] int GetViewModeAndIconSize(out int viewMode, out int iconSize);
        }

        [ComImport, Guid("000214E6-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellFolder
        {
            void _ParseDisplayName(); void _EnumObjects(); void _BindToObject(); void _BindToStorage();
            void _CompareIDs(); void _CreateViewObject(); void _GetAttributesOf(); void _GetUIObjectOf();
            [PreserveSig] int GetDisplayNameOf(IntPtr pidl, uint flags, out STRRET name);
        }

        [StructLayout(LayoutKind.Explicit, Size = 272)]
        struct STRRET
        {
            [FieldOffset(0)] public uint Type;
        }

        [DllImport("shell32.dll")] static extern int SHGetDesktopFolder(out IShellFolder folder);
        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)] static extern int StrRetToStrW(ref STRRET s, IntPtr pidl, out IntPtr text);
        [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr hwnd, ref Native.POINT pt);

        const int SWC_DESKTOP = 8, SWFO_NEEDDISPATCH = 1;
        const uint SVGIO_SELECTION = 0x1, SVGIO_ALLVIEW = 0x2, SHGDN_INFOLDER_FORPARSING = 0x8001;
        const uint SVSI_DESELECT = 0x0, SVSI_SELECT = 0x1, SVSI_DESELECTOTHERS = 0x4, SVSI_ENSUREVISIBLE = 0x8, SVSI_FOCUSED = 0x10, SVSI_POSITIONITEM = 0x80;
        const uint FWF_AUTOARRANGE = 0x1, FWF_SNAPTOGRID = 0x4;
        const string DesktopBagKey = @"Software\Microsoft\Windows\Shell\Bags\1\Desktop";

        // Parked icons sit this far up and left of the desktop's own origin: no monitor arrangement reaches it.
        const int ParkOffset = -20000;

        // name (as the Desktop folder parses it: "report.pdf", "::{645FF040-...}") → child ID in the desktop view
        static readonly Dictionary<string, IntPtr> Pidls = new(StringComparer.OrdinalIgnoreCase);
        // name → index in the view (the same as in its list view) as of the last Enumerate
        static readonly Dictionary<string, int> Indexes = new(StringComparer.OrdinalIgnoreCase);
        const int LVM_SETITEMPOSITION = 0x100F, WM_SETREDRAW = 0x000B;
        const uint RDW_INVALIDATE = 0x1, RDW_ERASE = 0x4, RDW_ALLCHILDREN = 0x80, RDW_UPDATENOW = 0x100;
        [DllImport("user32.dll")] static extern bool RedrawWindow(IntPtr hwnd, IntPtr rect, IntPtr region, uint flags);
        static IFolderView2? _view;
        static IShellFolder? _desktop;

        public readonly record struct Icon(string Name, Native.POINT Position);

        // Bounds of the visible icons, for the mouse hook (which cannot make COM calls itself).
        public static volatile Native.RECT[] IconRects = Array.Empty<Native.RECT>();

        public static Native.POINT Origin
        {
            get
            {
                var pt = new Native.POINT(0, 0);
                var lv = DesktopShell.FindListView();
                if (lv != IntPtr.Zero) ClientToScreen(lv, ref pt);
                return pt;
            }
        }

        public static Native.POINT ParkPoint
        {
            get
            {
                var o = Origin;
                return new Native.POINT(o.X + ParkOffset, o.Y + ParkOffset);
            }
        }

        public static bool IsParked(Native.POINT p) => p.X < Origin.X + ParkOffset / 2 || p.Y < Origin.Y + ParkOffset / 2;

        // Explorer restarted or the view went away: start over with a fresh proxy.
        public static void Reset()
        {
            if (_view != null)
            {
                try { Marshal.ReleaseComObject(_view); } catch (Exception) { }
                _view = null;
            }
            foreach (var pidl in Pidls.Values) Marshal.FreeCoTaskMem(pidl);
            Pidls.Clear();
        }

        static IFolderView2? View => _view ??= Connect();

        // Runs a call against the desktop view; a dead proxy (Explorer restarted) is replaced once.
        static T? Call<T>(Func<IFolderView2, T> call, T? fallback = default)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                var view = View;
                if (view == null) return fallback;
                try { return call(view); }
                catch (Exception ex) when (ex is COMException or InvalidComObjectException or InvalidCastException)
                {
                    Reset();
                }
            }
            return fallback;
        }

        public static bool Available => View != null;

        // Every icon on the desktop with its position (screen pixels, top-left of the icon's picture; see IconGrid).
        public static List<Icon>? Enumerate()
        {
            return Call(view =>
            {
                if (view.ItemCount(SVGIO_ALLVIEW, out int count) != 0) return null;
                var origin = Origin;
                var result = new List<Icon>(count);
                var rects = new List<Native.RECT>(count);
                var fresh = new Dictionary<string, IntPtr>(StringComparer.OrdinalIgnoreCase);
                var indexes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    for (int i = 0; i < count; i++)
                    {
                        if (view.Item(i, out var pidl) != 0 || pidl == IntPtr.Zero) continue;
                        string? name = NameOf(pidl);
                        if (name == null || fresh.ContainsKey(name) || view.GetItemPosition(pidl, out var pos) != 0)
                        {
                            Marshal.FreeCoTaskMem(pidl);
                            continue;
                        }
                        fresh[name] = pidl;
                        indexes[name] = i;
                        var screen = new Native.POINT(pos.X + origin.X, pos.Y + origin.Y);
                        result.Add(new Icon(name, screen));
                        if (!IsParked(screen)) rects.Add(IconGrid.CellRect(screen));
                    }
                }
                catch
                {
                    foreach (var p in fresh.Values) Marshal.FreeCoTaskMem(p);
                    throw;
                }
                foreach (var p in Pidls.Values) Marshal.FreeCoTaskMem(p);
                Pidls.Clear();
                foreach (var (k, v) in fresh) Pidls[k] = v;
                Indexes.Clear();
                foreach (var (k, v) in indexes) Indexes[k] = v;
                IconRects = rects.ToArray();
                return result;
            });
        }

        static string? NameOf(IntPtr pidl)
        {
            if (_desktop == null && SHGetDesktopFolder(out _desktop) != 0) return null;
            if (_desktop!.GetDisplayNameOf(pidl, SHGDN_INFOLDER_FORPARSING, out var ret) != 0) return null;
            if (StrRetToStrW(ref ret, pidl, out var text) != 0) return null;
            try { return Marshal.PtrToStringUni(text); }
            finally { Marshal.FreeCoTaskMem(text); }
        }

        public static bool Has(string name) => Pidls.ContainsKey(name);

        public static int IndexOf(string name) => Indexes.TryGetValue(name, out var index) ? index : -1;

        // Where the given icons are right now: a quick look at a few icons (Enumerate looks at all of them).
        public static List<Icon> PositionsOf(IEnumerable<string> names)
        {
            var result = new List<Icon>();
            Call(view =>
            {
                var origin = Origin;
                foreach (var name in names)
                    if (Pidls.TryGetValue(name, out var pidl) && view.GetItemPosition(pidl, out var pos) == 0)
                        result.Add(new Icon(name, new Native.POINT(pos.X + origin.X, pos.Y + origin.Y)));
                return 0;
            });
            return result;
        }

        // Moves icons to screen positions. Names the view does not know (yet) are skipped.
        // Explorer shows a move made through its view (SelectAndPositionItems) only about 0.3 s later, but one made
        // on its list view (LVM_SETITEMPOSITION) at the next frame: measured 295 ms against 30 ms. So an icon is
        // moved on the list view whenever its index is certain (the view still has this very item there), and
        // through the view otherwise.
        // repost: the watch may have parking moves waiting in Explorer's queue (posted, so handled after any sent
        // message); the moves are posted after them as well, so they are the last word.
        public static void Move(IReadOnlyCollection<(string Name, Native.POINT Screen)> moves, bool repost = false)
        {
            if (moves.Count == 0) return;
            var origin = Origin;
            var listView = DesktopShell.FindListView();
            var pidls = new List<IntPtr>(moves.Count);
            var points = new List<Native.POINT>(moves.Count);
            var fast = new List<(int Index, Native.POINT Client)>(moves.Count);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            long checkedAt = 0, fastAt = 0;
            Call(view =>
            {
                foreach (var (name, screen) in moves)
                {
                    if (!Pidls.TryGetValue(name, out var pidl)) continue;
                    var client = new Native.POINT(screen.X - origin.X, screen.Y - origin.Y);
                    if (Indexes.TryGetValue(name, out int index) && OnListView(view, listView, index, pidl, client)) fast.Add((index, client));
                    else
                    {
                        pidls.Add(pidl);
                        points.Add(client);
                    }
                }
                checkedAt = clock.ElapsedMilliseconds;
                MoveOnListView(listView, fast);
                if (repost)
                    foreach (var (index, client) in fast)
                        Native.PostMessage(listView, LVM_SETITEMPOSITION, new IntPtr(index), new IntPtr((client.Y << 16) | (client.X & 0xFFFF)));
                fastAt = clock.ElapsedMilliseconds;
                if (pidls.Count > 0) view.SelectAndPositionItems((uint)pidls.Count, pidls.ToArray(), points.ToArray(), SVSI_POSITIONITEM);
                return 0;
            });
            Log.Trace($"icons moved: {fast.Count} on the list view, {pidls.Count} through the view; checks {checkedAt} ms, " +
                      $"list view {fastAt - checkedAt} ms, view {clock.ElapsedMilliseconds - fastAt} ms");
        }

        // Has Explorer paint what it has to paint on the desktop now, and returns once it has (about 50 ms).
        public static void PaintNow()
        {
            var listView = DesktopShell.FindListView();
            if (listView != IntPtr.Zero) RedrawWindow(listView, IntPtr.Zero, IntPtr.Zero, RDW_UPDATENOW | RDW_ALLCHILDREN);
        }

        // Desko died in the middle of a batch (drawing switched off on the desktop): switch it back on.
        public static void EnsureRedraw()
        {
            var listView = DesktopShell.FindListView();
            if (listView == IntPtr.Zero) return;
            Native.SendMessage(listView, WM_SETREDRAW, new IntPtr(1), IntPtr.Zero);
            RedrawWindow(listView, IntPtr.Zero, IntPtr.Zero, RDW_INVALIDATE | RDW_ERASE | RDW_ALLCHILDREN);
        }

        // Whether the list view can be asked to move this item: it is still at this index, and the spot fits.
        static bool OnListView(IFolderView2 view, IntPtr listView, int index, IntPtr pidl, Native.POINT client)
        {
            if (listView == IntPtr.Zero || client.X is < short.MinValue or > short.MaxValue || client.Y is < short.MinValue or > short.MaxValue)
                return false;
            if (view.Item(index, out var there) != 0 || there == IntPtr.Zero) return false;
            try { return SamePidl(there, pidl); }
            finally { Marshal.FreeCoTaskMem(there); }
        }

        // The list view repaints after every single move (about 6 ms of Explorer's time each), so more than a
        // few are made with its drawing switched off and the desktop repainted once after (about 50 ms, whatever
        // the number of icons: 42 icons take 50 ms instead of 250).
        static void MoveOnListView(IntPtr listView, List<(int Index, Native.POINT Client)> moves)
        {
            if (moves.Count == 0) return;
            bool batch = moves.Count > 8;
            if (batch) Native.SendMessage(listView, WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);
            try
            {
                foreach (var (index, client) in moves)
                    Native.SendMessage(listView, LVM_SETITEMPOSITION, new IntPtr(index), new IntPtr((client.Y << 16) | (client.X & 0xFFFF)));
            }
            finally
            {
                if (batch)
                {
                    Native.SendMessage(listView, WM_SETREDRAW, new IntPtr(1), IntPtr.Zero);
                    RedrawWindow(listView, IntPtr.Zero, IntPtr.Zero, RDW_INVALIDATE | RDW_ERASE | RDW_ALLCHILDREN);
                }
            }
        }

        static int PidlSize(IntPtr pidl)
        {
            int size = 0;
            for (ushort cb; (cb = (ushort)Marshal.ReadInt16(pidl, size)) != 0; size += cb) { }
            return size;
        }

        static bool SamePidl(IntPtr a, IntPtr b)
        {
            int size = PidlSize(a);
            if (size != PidlSize(b)) return false;
            for (int i = 0; i < size; i++)
                if (Marshal.ReadByte(a, i) != Marshal.ReadByte(b, i)) return false;
            return true;
        }

        // How many icons are selected on the desktop.
        public static int SelectedCount() => Call(view => view.ItemCount(SVGIO_SELECTION, out int n) == 0 ? n : 0);

        // Deselects the given icons that are selected; returns how many were.
        public static int DeselectIfSelected(IEnumerable<string> names)
        {
            var pidls = new List<IntPtr>();
            Call(view =>
            {
                foreach (var name in names)
                    if (Pidls.TryGetValue(name, out var pidl) && view.GetSelectionState(pidl, out uint state) == 0 && (state & SVSI_SELECT) != 0)
                        pidls.Add(pidl);
                if (pidls.Count > 0) view.SelectAndPositionItems((uint)pidls.Count, pidls.ToArray(), null, SVSI_DESELECT);
                return 0;
            });
            return pidls.Count;
        }

        // Selects every icon, or none (for the test channel: what Ctrl+A or a click on empty desktop does).
        public static void SelectAll(bool select)
        {
            var pidls = new IntPtr[Pidls.Count];
            Pidls.Values.CopyTo(pidls, 0);
            if (pidls.Length > 0) Call(view => view.SelectAndPositionItems((uint)pidls.Length, pidls, null, select ? SVSI_SELECT : SVSI_DESELECT));
        }

        // Selects one icon (and only that one), like clicking it.
        public static void Select(string name)
        {
            if (!Pidls.TryGetValue(name, out var pidl)) return;
            Call(view => view.SelectAndPositionItems(1, new[] { pidl }, null,
                SVSI_SELECT | SVSI_DESELECTOTHERS | SVSI_FOCUSED | SVSI_ENSUREVISIBLE));
        }

        // Size of one icon cell on the desktop grid, in pixels.
        public static Native.POINT? Spacing => Call<Native.POINT?>(view =>
            view.GetSpacing(out var s) == 0 && s.X > 0 && s.Y > 0 ? s : null);

        // Fences need icons to stay exactly where they are put: "Auto arrange icons" would pull them back into
        // columns and "Align icons to grid" would snap them (and refuse off-screen spots). Returns the flags
        // that were on, so the caller can tell the user once.
        public static (bool AutoArrange, bool SnapToGrid)? EnsureFreeLayout()
        {
            return Call<(bool, bool)?>(view =>
            {
                if (view.GetCurrentFolderFlags(out uint flags) != 0) return null;
                bool auto = (flags & FWF_AUTOARRANGE) != 0, snap = (flags & FWF_SNAPTOGRID) != 0;
                if (auto || snap) view.SetCurrentFolderFlags(FWF_AUTOARRANGE | FWF_SNAPTOGRID, 0);
                return (auto, snap);
            });
        }

        // Gives the desktop its own arrangement back (uninstall).
        public static void SetArrange(bool autoArrange, bool snapToGrid) =>
            Call(view => view.SetCurrentFolderFlags(FWF_AUTOARRANGE | FWF_SNAPTOGRID,
                (autoArrange ? FWF_AUTOARRANGE : 0) | (snapToGrid ? FWF_SNAPTOGRID : 0)));

        // Icon size the desktop currently uses (32 small, 48 medium, 96 large, or anything set with Ctrl+wheel).
        public static int? GetDesktopIconSize() =>
            Call<int?>(view => view.GetViewModeAndIconSize(out _, out int size) == 0 && size > 0 ? size : null)
            ?? ReadDesktopIconSizeSetting();

        // Cheap check used for polling; Explorer writes it whenever the desktop icon size changes.
        public static int? ReadDesktopIconSizeSetting()
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(DesktopBagKey);
            return key?.GetValue("IconSize") is int size && size > 0 ? size : null;
        }

        static IFolderView2? Connect()
        {
            object? windows = null, dispatch = null;
            IntPtr browserPtr = IntPtr.Zero, viewPtr = IntPtr.Zero;
            try
            {
                var type = Type.GetTypeFromCLSID(new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39"));
                windows = type == null ? null : Activator.CreateInstance(type);
                if (windows is not IShellWindows shellWindows) return null;
                object loc = null!, root = null!;
                dispatch = shellWindows.FindWindowSW(ref loc, ref root, SWC_DESKTOP, out _, SWFO_NEEDDISPATCH);
                if (dispatch is not IOleServiceProvider provider) return null;

                var sidTopLevelBrowser = new Guid("4C96BE40-915C-11CF-99D3-00AA004AE837");
                var iidShellBrowser = typeof(IShellBrowser).GUID;
                if (provider.QueryService(ref sidTopLevelBrowser, ref iidShellBrowser, out browserPtr) != 0) return null;
                var browser = (IShellBrowser)Marshal.GetObjectForIUnknown(browserPtr);
                try
                {
                    if (browser.QueryActiveShellView(out viewPtr) != 0 || viewPtr == IntPtr.Zero) return null;
                    return Marshal.GetObjectForIUnknown(viewPtr) as IFolderView2;
                }
                finally { Marshal.ReleaseComObject(browser); }
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException)
            {
                Log.Error(ex);
                return null;
            }
            finally
            {
                if (viewPtr != IntPtr.Zero) Marshal.Release(viewPtr);
                if (browserPtr != IntPtr.Zero) Marshal.Release(browserPtr);
                if (dispatch != null) Marshal.ReleaseComObject(dispatch);
                if (windows != null) Marshal.ReleaseComObject(windows);
            }
        }

        // An item ID as bytes (it is plain data), for handing to another thread.
        public static byte[]? PidlBytes(string name)
        {
            if (!Pidls.TryGetValue(name, out var pidl)) return null;
            int size = 0;
            for (ushort cb; (cb = (ushort)Marshal.ReadInt16(pidl, size)) != 0; size += cb) { }
            var bytes = new byte[size + 2];
            Marshal.Copy(pidl, bytes, 0, size + 2);
            return bytes;
        }

        // Explorer moves parked icons back on screen by itself, about 0.1 s after icons are dropped on the desktop
        // and when it rebuilds its list. The UI thread only gets to look once a frame and has other work, so this
        // watches on a thread of its own, through a second connection to the desktop view: every 2 ms for a moment
        // it looks at a couple of parked icons and sends them all back the moment they show up, usually before
        // Explorer has painted them. It also reports when a dropped icon has landed, so the desktop can be tidied
        // at once rather than after a guessed delay.
        public static class Watch
        {
            public sealed record Request(List<(string Name, byte[] Pidl, Native.POINT Park, int Index)> Parked,
                                         (string Name, byte[] Pidl, Native.POINT At)? Dropped, int Milliseconds, string Why);

            // Raised on the watch thread.
            public static event Action<Request, List<(string Name, Native.POINT At)>, long>? Reparked;
            public static event Action<Request, long>? Landed;

            static Thread? _thread;
            static Request? _next;
            static int _generation;
            static readonly AutoResetEvent Wake = new(false);

            [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint ms);
            [DllImport("winmm.dll")] static extern uint timeEndPeriod(uint ms);

            public static void Start(Request request)
            {
                Interlocked.Exchange(ref _next, request);
                Interlocked.Increment(ref _generation);
                if (_thread == null)
                {
                    _thread = new Thread(Run) { IsBackground = true, Name = "Desko icon watch" };
                    _thread.SetApartmentState(ApartmentState.MTA);
                    _thread.Start();
                }
                Wake.Set();
            }

            // The UI thread is about to move icons itself: whatever is being watched may be out of date.
            public static void Stop() => Interlocked.Increment(ref _generation);

            static void Run()
            {
                IFolderView2? view = null;
                while (true)
                {
                    Wake.WaitOne();
                    var request = Interlocked.Exchange(ref _next, null);
                    if (request == null) continue;
                    int generation = Volatile.Read(ref _generation);
                    var parked = new List<(string Name, IntPtr Pidl, Native.POINT Park, int Index)>();
                    IntPtr dropped = IntPtr.Zero;
                    timeBeginPeriod(1);
                    try
                    {
                        view ??= Connect();
                        if (view == null) continue;
                        foreach (var (name, bytes, park, index) in request.Parked) parked.Add((name, Alloc(bytes), park, index));
                        if (request.Dropped is { } d) dropped = Alloc(d.Pidl);
                        var samples = parked.Count <= 2 ? parked : new() { parked[0], parked[^1] };
                        var clock = System.Diagnostics.Stopwatch.StartNew();
                        // After a drop on the desktop Explorer pulls the parked icons on screen and paints them as soon
                        // as it is idle; looking first and moving them back after is too late to stop that paint. So
                        // for that while they are also sent back blindly, every few ms: such a message is waiting in
                        // Explorer's queue when it is done pulling, and is handled before it paints. (An icon that is
                        // already there does not move; the indexes are checked every 100 ms.)
                        var pinned = new List<(int Index, Native.POINT Client)>();
                        long pinnedAt = -1000, postedAt = -1000;
                        var listView = DesktopShell.FindListView();
                        while (clock.ElapsedMilliseconds < request.Milliseconds && Volatile.Read(ref _generation) == generation)
                        {
                            var origin = Origin;
                            if (request.Why == "a drop" && listView != IntPtr.Zero && clock.ElapsedMilliseconds < 450)
                            {
                                if (clock.ElapsedMilliseconds - pinnedAt >= 100)
                                {
                                    pinned.Clear();
                                    foreach (var (_, pidl, park, index) in parked)
                                    {
                                        var client = new Native.POINT(park.X - origin.X, park.Y - origin.Y);
                                        if (index >= 0 && OnListView(view, listView, index, pidl, client)) pinned.Add((index, client));
                                    }
                                    pinnedAt = clock.ElapsedMilliseconds;
                                }
                                if (clock.ElapsedMilliseconds - postedAt >= 6)
                                {
                                    foreach (var (index, client) in pinned)
                                        if (Volatile.Read(ref _generation) == generation)
                                            Native.PostMessage(listView, LVM_SETITEMPOSITION, new IntPtr(index), new IntPtr((client.Y << 16) | (client.X & 0xFFFF)));
                                    postedAt = clock.ElapsedMilliseconds;
                                }
                            }
                            if (dropped != IntPtr.Zero && view.GetItemPosition(dropped, out var p) == 0 &&
                                (Math.Abs(p.X + origin.X - request.Dropped!.Value.At.X) > 2 || Math.Abs(p.Y + origin.Y - request.Dropped.Value.At.Y) > 2))
                            {
                                Marshal.FreeCoTaskMem(dropped);
                                dropped = IntPtr.Zero;
                                Landed?.Invoke(request, clock.ElapsedMilliseconds);
                            }
                            bool back = false;
                            foreach (var s in samples)
                                if (view.GetItemPosition(s.Pidl, out var sp) == 0 && !IsParked(new Native.POINT(sp.X + origin.X, sp.Y + origin.Y)))
                                    back = true;
                            if (back)
                            {
                                var found = new List<(string Name, Native.POINT At)>();
                                var pidls = new List<IntPtr>();
                                var points = new List<Native.POINT>();
                                listView = DesktopShell.FindListView();
                                var fast = new List<(int Index, Native.POINT Client)>();
                                foreach (var (name, pidl, park, index) in parked)
                                {
                                    if (Volatile.Read(ref _generation) != generation) break;
                                    if (view.GetItemPosition(pidl, out var at) != 0) continue;
                                    var screen = new Native.POINT(at.X + origin.X, at.Y + origin.Y);
                                    if (IsParked(screen)) continue;
                                    found.Add((name, screen));
                                    var client = new Native.POINT(park.X - origin.X, park.Y - origin.Y);
                                    if (index >= 0 && OnListView(view, listView, index, pidl, client)) { fast.Add((index, client)); continue; }
                                    pidls.Add(pidl);
                                    points.Add(client);
                                }
                                if (Volatile.Read(ref _generation) == generation) MoveOnListView(listView, fast);
                                if (pidls.Count > 0 && Volatile.Read(ref _generation) == generation)
                                    view.SelectAndPositionItems((uint)pidls.Count, pidls.ToArray(), points.ToArray(), SVSI_POSITIONITEM);
                                if (found.Count > 0) Reparked?.Invoke(request, found, clock.ElapsedMilliseconds);
                            }
                            if (dropped == IntPtr.Zero && parked.Count == 0) break;
                            Thread.Sleep(2);
                        }
                    }
                    catch (Exception ex) when (ex is COMException or InvalidComObjectException or InvalidCastException)
                    {
                        view = null; // Explorer restarted: connect again next time
                    }
                    finally
                    {
                        timeEndPeriod(1);
                        foreach (var p in parked) Marshal.FreeCoTaskMem(p.Pidl);
                        if (dropped != IntPtr.Zero) Marshal.FreeCoTaskMem(dropped);
                    }
                }
            }

            static IntPtr Alloc(byte[] bytes)
            {
                var p = Marshal.AllocCoTaskMem(bytes.Length);
                Marshal.Copy(bytes, 0, p, bytes.Length);
                return p;
            }
        }
    }
}
