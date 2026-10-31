using System;
using System.Runtime.InteropServices;
using Desko.Core;

namespace Desko.Shell
{
    // Explorer rebuilds its desktop icon list without telling other apps: F5, a theme or icon-size change, "Auto
    // arrange" switched on from its menu... and a rebuild drags parked icons back on screen. The list view does
    // announce new / removed / reordered items to accessibility tools, so Desko listens there.
    // (Moving an icon raises nothing; the mouse hook covers that.)
    internal static class DesktopEvents
    {
        delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

        [DllImport("user32.dll")]
        static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventProc proc, uint pid, uint tid, uint flags);
        [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hook);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

        const uint EVENT_OBJECT_CREATE = 0x8000, EVENT_OBJECT_REORDER = 0x8004, WINEVENT_OUTOFCONTEXT = 0;
        const uint EVENT_OBJECT_SELECTION = 0x8006, EVENT_OBJECT_SELECTIONWITHIN = 0x8009;

        static IntPtr _hook, _selectionHook, _listView;
        static WinEventProc? _proc; // kept alive while hooked

        // Raised on the thread that called Start (it needs a message loop: the UI thread).
        public static event Action? ItemsChanged;
        // Icons were selected or deselected (a click, Ctrl+A, a rubber band...).
        public static event Action? SelectionChanged;

        public static void Start()
        {
            Stop();
            _listView = DesktopShell.FindListView();
            if (_listView == IntPtr.Zero) return;
            uint thread = GetWindowThreadProcessId(_listView, out uint pid);
            _proc = OnEvent;
            // Create, destroy, show, hide, reorder.
            _hook = SetWinEventHook(EVENT_OBJECT_CREATE, EVENT_OBJECT_REORDER, IntPtr.Zero, _proc, pid, thread, WINEVENT_OUTOFCONTEXT);
            _selectionHook = SetWinEventHook(EVENT_OBJECT_SELECTION, EVENT_OBJECT_SELECTIONWITHIN, IntPtr.Zero, _proc, pid, thread, WINEVENT_OUTOFCONTEXT);
        }

        public static void Stop()
        {
            if (_hook != IntPtr.Zero) UnhookWinEvent(_hook);
            if (_selectionHook != IntPtr.Zero) UnhookWinEvent(_selectionHook);
            _hook = _selectionHook = IntPtr.Zero;
        }

        static void OnEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
        {
            if (hwnd != _listView) return;
            if (eventType >= EVENT_OBJECT_SELECTION) { SelectionChanged?.Invoke(); return; }
            Count(eventType);
            ItemsChanged?.Invoke();
        }

        // For the trace: what each burst of list events was made of (a refresh is one "create" per icon).
        static readonly int[] _counts = new int[5];
        static System.Threading.Timer? _burst;

        static void Count(uint eventType)
        {
            System.Threading.Interlocked.Increment(ref _counts[eventType - EVENT_OBJECT_CREATE]);
            _burst ??= new System.Threading.Timer(_ =>
            {
                var c = new int[5];
                for (int i = 0; i < 5; i++) c[i] = System.Threading.Interlocked.Exchange(ref _counts[i], 0);
                Models.Log.Trace($"explorer list events: {c[0]} create, {c[1]} destroy, {c[2]} show, {c[3]} hide, {c[4]} reorder");
            });
            _burst.Change(80, System.Threading.Timeout.Infinite);
        }
    }
}
