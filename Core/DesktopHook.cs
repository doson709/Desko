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
    // A plain right click is swallowed and replayed so Explorer's own menu still appears.
    internal sealed class DesktopHook : IDisposable
    {
        enum RightState { None, Checking, Background, Dragging, PassThrough }

        public volatile bool DoubleClickEnabled = true;
        public volatile bool RightDragEnabled = true;

        public event Action? DesktopDoubleClicked;
        // Any real left/right button press anywhere, with the window under the cursor at that moment.
        public event Action<IntPtr>? MouseDownOn;
        public event Action<Native.RECT>? RightDragChanged;
        public event Action<Native.RECT>? RightDragCompleted;

        readonly Native.LowLevelMouseProc _proc;
        readonly object _sync = new();
        RightState _right;
        Native.POINT _rightStart;
        bool _hasLastClick;
        uint _lastClickTime;
        Native.POINT _lastClickPt;

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

        bool Handle(int msg, Native.MSLLHOOKSTRUCT info)
        {
            bool injected = (info.flags & Native.LLMHF_INJECTED) != 0;
            var pt = info.pt;

            switch (msg)
            {
                case Native.WM_LBUTTONDOWN:
                {
                    if (injected) { _hasLastClick = false; return false; }
                    MouseDownOn?.Invoke(Native.WindowFromPoint(pt));
                    bool onDesktop = DesktopShell.IsDesktopAt(pt);
                    if (!DoubleClickEnabled) { _hasLastClick = false; return false; }
                    if (_hasLastClick
                        && info.time - _lastClickTime <= Native.GetDoubleClickTime()
                        && Math.Abs(pt.X - _lastClickPt.X) <= Native.GetSystemMetrics(Native.SM_CXDOUBLECLK) / 2
                        && Math.Abs(pt.Y - _lastClickPt.Y) <= Native.GetSystemMetrics(Native.SM_CYDOUBLECLK) / 2)
                    {
                        _hasLastClick = false;
                        if (onDesktop)
                            Task.Run(() => { if (!DesktopShell.IsIconAt(pt)) DesktopDoubleClicked?.Invoke(); });
                    }
                    else
                    {
                        _hasLastClick = true;
                        _lastClickTime = info.time;
                        _lastClickPt = pt;
                    }
                    return false;
                }

                case Native.WM_RBUTTONDOWN:
                    if (!injected) MouseDownOn?.Invoke(Native.WindowFromPoint(pt));
                    if (injected || !RightDragEnabled || !DesktopShell.IsDesktopAt(pt)) return false;
                    lock (_sync) { _right = RightState.Checking; _rightStart = pt; }
                    Task.Run(() => ResolveRightDown(pt));
                    return true;

                case Native.WM_MOUSEMOVE:
                {
                    Native.RECT rect = default;
                    bool raise = false;
                    lock (_sync)
                    {
                        if (_right == RightState.Background && IsFar(pt, _rightStart)) _right = RightState.Dragging;
                        if (_right == RightState.Dragging) { rect = MakeRect(_rightStart, pt); raise = true; }
                    }
                    if (raise) RightDragChanged?.Invoke(rect);
                    return false;
                }

                case Native.WM_RBUTTONUP:
                {
                    if (injected) return false;
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
            bool onIcon = DesktopShell.IsIconAt(pt);
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

        static bool IsFar(Native.POINT a, Native.POINT b) => Math.Abs(a.X - b.X) > 8 || Math.Abs(a.Y - b.Y) > 8;

        static Native.RECT MakeRect(Native.POINT a, Native.POINT b) =>
            new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
    }
}
