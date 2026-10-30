using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Desko.Core;
using Desko.Interop;
using Desko.Models;

namespace Desko.Shell
{
    // Puts desktop icons at a given screen point through the desktop's own IFolderView
    // (ShellWindows → desktop browser → active shell view), the documented way to move desktop icons.
    internal static class DesktopIconPlacer
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

        [ComImport, Guid("CDE725B0-CCC9-4519-917E-325D72FAB4CE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IFolderView
        {
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
                                       [MarshalAs(UnmanagedType.LPArray)] Native.POINT[] points, uint flags);
        }

        [ComImport, Guid("1AF3A467-214F-4298-908E-06B03E0B39F9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IFolderView2
        {
            // IFolderView (14 methods)
            void _1(); void _2(); void _3(); void _4(); void _5(); void _6(); void _7();
            void _8(); void _9(); void _10(); void _11(); void _12(); void _13(); void _14();
            // IFolderView2, up to GetViewModeAndIconSize
            void _SetGroupBy(); void _GetGroupBy(); void _SetViewProperty(); void _GetViewProperty();
            void _SetTileViewProperties(); void _SetExtendedTileViewProperties(); void _SetText();
            void _SetCurrentFolderFlags(); void _GetCurrentFolderFlags(); void _GetSortColumnCount();
            void _SetSortColumns(); void _GetSortColumns(); void _GetItem(); void _GetVisibleItem();
            void _GetSelectedItem(); void _GetSelection(); void _GetSelectionState(); void _InvokeVerbOnSelection();
            void _SetViewModeAndIconSize();
            [PreserveSig] int GetViewModeAndIconSize(out int viewMode, out int iconSize);
        }

        const string DesktopBagKey = @"Software\Microsoft\Windows\Shell\Bags\1\Desktop";

        // Icon size the desktop currently uses (32 small, 48 medium, 96 large, or anything set with Ctrl+wheel).
        public static int? GetDesktopIconSize()
        {
            try
            {
                var view = GetDesktopFolderView();
                if (view != null)
                {
                    try
                    {
                        if (view is IFolderView2 v2 && v2.GetViewModeAndIconSize(out _, out int size) == 0 && size > 0)
                            return size;
                    }
                    finally { Marshal.ReleaseComObject(view); }
                }
            }
            catch (Exception ex) { Log.Error(ex); }
            return ReadDesktopIconSizeSetting();
        }

        // Cheap check used for polling; Explorer writes it whenever the desktop icon size changes.
        public static int? ReadDesktopIconSizeSetting()
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(DesktopBagKey);
            return key?.GetValue("IconSize") is int size && size > 0 ? size : null;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern int SHParseDisplayName(string name, IntPtr bindCtx, out IntPtr pidl, uint sfgaoIn, out uint sfgaoOut);

        [DllImport("shell32.dll")]
        static extern IntPtr ILFindLastID(IntPtr pidl);

        [DllImport("user32.dll")]
        static extern bool ScreenToClient(IntPtr hwnd, ref Native.POINT pt);

        const int SWC_DESKTOP = 8, SWFO_NEEDDISPATCH = 1;
        const uint SVSI_SELECT = 0x1, SVSI_DESELECTOTHERS = 0x4, SVSI_POSITIONITEM = 0x80;

        // Is there a desktop icon under this screen point? Asks the desktop view for its item positions;
        // null when the view can't be reached. (Replaces UI Automation: no extra libraries, no first-use lag.)
        public static bool? IsIconAt(Native.POINT screenPoint)
        {
            const uint SVGIO_ALLVIEW = 0x2;
            IFolderView? view = null;
            try
            {
                view = GetDesktopFolderView();
                if (view == null) return null;
                var listView = DesktopShell.FindListView();
                var pt = screenPoint;
                if (listView != IntPtr.Zero) ScreenToClient(listView, ref pt);
                if (view.GetSpacing(out var cell) != 0 || cell.X <= 0) cell = new Native.POINT(76, 100);
                if (view.ItemCount(SVGIO_ALLVIEW, out int count) != 0) return null;
                for (int i = 0; i < count; i++)
                {
                    if (view.Item(i, out var pidl) != 0 || pidl == IntPtr.Zero) continue;
                    try
                    {
                        if (view.GetItemPosition(pidl, out var pos) == 0 &&
                            pt.X >= pos.X && pt.X < pos.X + cell.X && pt.Y >= pos.Y && pt.Y < pos.Y + cell.Y)
                            return true;
                    }
                    finally { Marshal.FreeCoTaskMem(pidl); }
                }
                return false;
            }
            catch (Exception) { return null; }
            finally
            {
                if (view != null) Marshal.ReleaseComObject(view);
            }
        }

        // The icons only exist in the view once Explorer has processed the un-hide, so try a few times.
        public static void PlaceLater(IReadOnlyList<string> paths, Native.POINT screenPoint)
        {
            int attempts = 0;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            timer.Tick += (_, _) =>
            {
                attempts++;
                try { Place(paths, screenPoint); }
                catch (Exception ex) { Log.Error(ex); attempts = int.MaxValue; }
                if (attempts >= 3) timer.Stop();
            };
            timer.Start();
        }

        static void Place(IReadOnlyList<string> paths, Native.POINT screenPoint)
        {
            var view = GetDesktopFolderView();
            if (view == null) return;
            var pidls = new List<IntPtr>();
            try
            {
                var listView = DesktopShell.FindListView();
                var pt = screenPoint;
                if (listView != IntPtr.Zero) ScreenToClient(listView, ref pt);
                if (view.GetSpacing(out var spacing) != 0 || spacing.X <= 0) spacing = new Native.POINT(76, 100);

                var children = new List<IntPtr>();
                var points = new List<Native.POINT>();
                foreach (var path in paths)
                {
                    if (SHParseDisplayName(path, IntPtr.Zero, out var pidl, 0, out _) != 0 || pidl == IntPtr.Zero) continue;
                    pidls.Add(pidl);
                    children.Add(ILFindLastID(pidl));
                    // Icons in a row, the first one centred under the cursor.
                    int i = points.Count;
                    points.Add(new Native.POINT(pt.X - spacing.X / 2 + i * spacing.X, pt.Y - spacing.Y / 3));
                }
                if (children.Count > 0)
                    view.SelectAndPositionItems((uint)children.Count, children.ToArray(), points.ToArray(),
                        SVSI_POSITIONITEM | SVSI_SELECT | SVSI_DESELECTOTHERS);
            }
            finally
            {
                foreach (var p in pidls) Marshal.FreeCoTaskMem(p);
                Marshal.ReleaseComObject(view);
            }
        }

        static IFolderView? GetDesktopFolderView()
        {
            var type = Type.GetTypeFromCLSID(new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39"));
            if (type == null || Activator.CreateInstance(type) is not IShellWindows windows) return null;
            object? dispatch = null;
            IntPtr browserPtr = IntPtr.Zero, viewPtr = IntPtr.Zero;
            try
            {
                object loc = null!, root = null!;
                dispatch = windows.FindWindowSW(ref loc, ref root, SWC_DESKTOP, out _, SWFO_NEEDDISPATCH);
                if (dispatch is not IOleServiceProvider provider) return null;

                var sidTopLevelBrowser = new Guid("4C96BE40-915C-11CF-99D3-00AA004AE837");
                var iidShellBrowser = typeof(IShellBrowser).GUID;
                if (provider.QueryService(ref sidTopLevelBrowser, ref iidShellBrowser, out browserPtr) != 0) return null;
                var browser = (IShellBrowser)Marshal.GetObjectForIUnknown(browserPtr);
                try
                {
                    if (browser.QueryActiveShellView(out viewPtr) != 0 || viewPtr == IntPtr.Zero) return null;
                    return Marshal.GetObjectForIUnknown(viewPtr) as IFolderView;
                }
                finally { Marshal.ReleaseComObject(browser); }
            }
            finally
            {
                if (viewPtr != IntPtr.Zero) Marshal.Release(viewPtr);
                if (browserPtr != IntPtr.Zero) Marshal.Release(browserPtr);
                if (dispatch != null) Marshal.ReleaseComObject(dispatch);
                Marshal.ReleaseComObject(windows);
            }
        }
    }
}
