using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Desko.Interop;

namespace Desko.Core
{
    // Global low-level mouse hook on its own thread, so a busy UI thread never lags the cursor.
    //  - double-click on empty desktop          → DesktopDoubleClicked
    //  - right-button drag on empty desktop     → RightDragChanged / RightDragCompleted
    //  - wheel over the icon area of a fence    → Wheel (swallowed; the desktop itself does not scroll)
    //  - a left-button drag passing over fences → DragZoneChanged, then Released
    //  - desktop icons dragged with either button → Released(iconsMoved: true)
    // A plain right click is swallowed and replayed so Explorer's own menu still appears.
    // It cannot ask Explorer anything (that would stall every mouse move), so it works from snapshots:
    // Zones (the fences' icon areas) and DesktopIcons.IconRects.
    internal sealed class DesktopHook : IDisposable
    {
        enum RightState { None, Checking, Background, Dragging, PassThrough }

        public readonly record struct Zone(Native.RECT Body, int Id);

        public volatile bool DoubleClickEnabled = true;
        public volatile bool RightDragEnabled = true;
        public volatile Zone[] Zones = Array.Empty<Zone>();

        public event Action? DesktopDoubleClicked;
        // Any real left/right button press anywhere, with the window under the cursor at that moment.
        public event Action<IntPtr>? MouseDownOn;
        public event Action<Native.RECT>? RightDragChanged;
        public event Action<Native.RECT>? RightDragCompleted;
        // The fence (zone id, -1 = none) under a left-button drag that could be a drag-and-drop.
        public event Action<int>? DragZoneChanged;
        // A button was released after a drag: whether desktop icons were being dragged (Explorer may have moved
        // them), and where the drag started and ended (Explorer moves dropped icons by exactly that much).
        public event Action<bool, Native.POINT, Native.POINT>? Released;
        // Desktop icons were right-dragged: Explorer moves them only once its "Move here" menu is answered.
        public event Action<Native.POINT, Native.POINT>? RightDragReleased;
        // Any real button press on the desktop itself, where it happened (new items appear where the user worked).
        public event Action<Native.POINT>? DesktopPressed;
        public event Action<int, int>? Wheel;

        readonly Native.LowLevelMouseProc _proc;
        readonly object _sync = new();
        RightState _right;
        Native.POINT _rightStart;
        bool _hasLastClick;
        uint _lastClickTime;
        Native.POINT _lastClickPt;

        // A drag of desktop icons or of something from another app is under way (not one of our own windows
        // being moved). Explorer is in the middle of its drag loop then, so icons must not be moved around.
        public bool ForeignDragActive => _leftDown && _leftDragging && !_leftOwn &&
                                         Environment.TickCount64 - _leftPressTime < 60_000;

        // The left button, from press to release.
        volatile bool _leftDown, _leftDragging;
        bool _leftOnIcon, _leftCanDrag, _leftOwn;
        long _leftPressTime;
        readonly uint _ownPid = (uint)Environment.ProcessId;
        Native.POINT _leftStart;
        int _leftZone = -1;
        Native.POINT? _rightIconStart;

        Thread? _thread;
        uint _threadId;
        IntPtr _hook;

        public DesktopHook() => _proc = HookProc;

        public void Start()
        {
            using var ready = new ManualResetEventSlim();
            _thread = new Thread(() =>
            {
                _threadId = Native.GetCurrentThreadId();
                _hook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _proc, Native.GetModuleHandle(null), 0);
                ready.Set();
                while (Native.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
                {
                    Native.TranslateMessage(ref msg);
                    Native.DispatchMessage(ref msg);
                }
                if (_hook != IntPtr.Zero) Native.UnhookWindowsHookEx(_hook);
            })
            { IsBackground = true, Name = "Desko mouse hook" };
            _thread.Start();
            ready.Wait(2000);
        }

        public void Dispose()
        {
            if (_threadId != 0) Native.PostThreadMessage(_threadId, Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        }

        IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                try
                {
                    var info = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
                    if (Handle((int)wParam, info)) return new IntPtr(1);
                }
                catch (Exception) { }
            }
            return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        public static bool IsIconAt(Native.POINT pt)
        {
            foreach (var r in Shell.DesktopIcons.IconRects)
                if (Native.Contains(r, pt)) return true;
            return false;
        }

        int ZoneAt(Native.POINT pt)
        {
            foreach (var z in Zones)
                if (Native.Contains(z.Body, pt)) return z.Id;
            return -1;
        }

        bool Handle(int msg, Native.MSLLHOOKSTRUCT info)
        {
            bool injected = (info.flags & Native.LLMHF_INJECTED) != 0;
            var pt = info.pt;

            switch (msg)
            {
                case Native.WM_LBUTTONDOWN:
                {
                    if (injected) { _hasLastClick = false; return false; }
                    var under = Native.WindowFromPoint(pt);
                    MouseDownOn?.Invoke(under);
                    bool onDesktop = DesktopShell.IsDesktopAt(pt);
                    bool onIcon = onDesktop && IsIconAt(pt);
                    if (onDesktop) DesktopPressed?.Invoke(pt);

                    // Everything but a rubber band on empty desktop may turn into a drag-and-drop.
                    Native.GetWindowThreadProcessId(under, out uint pid);
                    _leftOwn = pid == _ownPid;
                    _leftPressTime = Environment.TickCount64;
                    _leftDown = true;
                    _leftStart = pt;
                    Models.Log.Trace($"hook press desktop={onDesktop} icon={onIcon} own={_leftOwn} at {pt.X},{pt.Y}");
                    _leftOnIcon = onIcon;
                    _leftCanDrag = !onDesktop || onIcon;
                    _leftDragging = false;
                    _leftZone = -1;

                    if (!DoubleClickEnabled) { _hasLastClick = false; return false; }
                    if (_hasLastClick
                        && info.time - _lastClickTime <= Native.GetDoubleClickTime()
                        && Math.Abs(pt.X - _lastClickPt.X) <= Native.GetSystemMetrics(Native.SM_CXDOUBLECLK) / 2
                        && Math.Abs(pt.Y - _lastClickPt.Y) <= Native.GetSystemMetrics(Native.SM_CYDOUBLECLK) / 2)
                    {
                        _hasLastClick = false;
                        if (onDesktop && !onIcon && ZoneAt(pt) < 0) Task.Run(() => DesktopDoubleClicked?.Invoke());
                    }
                    else
                    {
                        _hasLastClick = true;
                        _lastClickTime = info.time;
                        _lastClickPt = pt;
                    }
                    return false;
                }

                case Native.WM_LBUTTONUP:
                {
                    if (injected || !_leftDown) return false;
                    _leftDown = false;
                    bool zoneSet = _leftZone >= 0;
                    _leftZone = -1;
                    if (zoneSet) DragZoneChanged?.Invoke(-1);
                    Models.Log.Trace($"hook release dragging={_leftDragging} icon={_leftOnIcon} at {pt.X},{pt.Y}");
                    if (_leftDragging) Released?.Invoke(_leftOnIcon, _leftStart, pt);
                    return false;
                }

                case Native.WM_MOUSEWHEEL:
                {
                    if (injected || Native.IsKeyDown(Native.VK_CONTROL)) return false; // Ctrl+wheel sizes the icons
                    int zone = ZoneAt(pt);
                    if (zone < 0 || !DesktopShell.IsDesktopAt(pt)) return false;
                    Wheel?.Invoke(zone, (short)(info.mouseData >> 16));
                    return true;
                }

                case Native.WM_RBUTTONDOWN:
                {
                    if (injected) return false;
                    MouseDownOn?.Invoke(Native.WindowFromPoint(pt));
                    bool onDesktop = DesktopShell.IsDesktopAt(pt);
                    // Explorer's own right-drag of icons ("Move here" menu) may move them too.
                    _rightIconStart = onDesktop && IsIconAt(pt) ? pt : null;
                    if (onDesktop) DesktopPressed?.Invoke(pt);
                    if (!RightDragEnabled || !onDesktop || ZoneAt(pt) >= 0) return false;
                    lock (_sync) { _right = RightState.Checking; _rightStart = pt; }
                    Task.Run(() => ResolveRightDown(pt));
                    return true;
                }

                case Native.WM_MOUSEMOVE:
                {
                    if (_leftDown && _leftCanDrag)
                    {
                        if (!_leftDragging && IsFar(pt, _leftStart, 4)) { _leftDragging = true; Models.Log.Trace("hook drag start"); }
                        if (_leftDragging)
                        {
                            int zone = ZoneAt(pt);
                            if (zone != _leftZone)
                            {
                                _leftZone = zone;
                                DragZoneChanged?.Invoke(zone);
                            }
                        }
                    }

                    Native.RECT rect = default;
                    bool raise = false;
                    lock (_sync)
                    {
                        if (_right == RightState.Background && IsFar(pt, _rightStart, 8)) _right = RightState.Dragging;
                        if (_right == RightState.Dragging) { rect = MakeRect(_rightStart, pt); raise = true; }
                    }
                    if (raise) RightDragChanged?.Invoke(rect);
                    return false;
                }

                case Native.WM_RBUTTONUP:
                {
                    if (injected) return false;
                    if (_rightIconStart is Native.POINT iconStart && IsFar(pt, iconStart, 4)) RightDragReleased?.Invoke(iconStart, pt);
                    _rightIconStart = null;
                    RightState prev;
                    Native.POINT start;
                    lock (_sync) { prev = _right; start = _rightStart; _right = RightState.None; }
                    switch (prev)
                    {
                        case RightState.None:
                        case RightState.PassThrough:
                            return false;
                        case RightState.Dragging:
                            RightDragCompleted?.Invoke(MakeRect(start, pt));
                            return true;
                        default:
                            // A plain right click: give it back to Explorer.
                            Task.Run(() => Native.SendMouse(Native.MOUSEEVENTF_RIGHTDOWN, Native.MOUSEEVENTF_RIGHTUP));
                            return true;
                    }
                }
            }
            return false;
        }

        void ResolveRightDown(Native.POINT pt)
        {
            bool onIcon = IsIconAt(pt);
            bool replayDown = false;
            lock (_sync)
            {
                if (_right != RightState.Checking) return;
                if (onIcon) { _right = RightState.PassThrough; replayDown = true; }
                else _right = RightState.Background;
            }
            // Right-drags of icons belong to Explorer: hand it the button-down we swallowed.
            if (replayDown) Native.SendMouse(Native.MOUSEEVENTF_RIGHTDOWN);
        }

        static bool IsFar(Native.POINT a, Native.POINT b, int d) => Math.Abs(a.X - b.X) > d || Math.Abs(a.Y - b.Y) > d;

        static Native.RECT MakeRect(Native.POINT a, Native.POINT b) =>
            new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
    }
}
