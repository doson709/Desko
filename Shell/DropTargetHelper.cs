using System;
using System.Runtime.InteropServices;
using System.Windows;
using Desko.Interop;
using ComDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace Desko.Shell
{
    // Lets the shell keep drawing the drag image (file icons + "Move to …" label) while it is over a fence.
    // Without this the image disappears as soon as the cursor enters our window.
    internal static class DropTargetHelper
    {
        [ComImport, Guid("4657278B-411B-11D2-839A-00C04FD918D0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IDropTargetHelper
        {
            void DragEnter(IntPtr hwndTarget, ComDataObject dataObject, ref Native.POINT pt, int effect);
            void DragLeave();
            void DragOver(ref Native.POINT pt, int effect);
            void Drop(ComDataObject dataObject, ref Native.POINT pt, int effect);
            void Show(bool show);
        }

        [ComImport, Guid("4657278A-411B-11D2-839A-00C04FD918D0")]
        class DragDropHelper { }

        static IDropTargetHelper? _helper;
        static bool _failed;

        static IDropTargetHelper? Helper
        {
            get
            {
                if (_helper == null && !_failed)
                {
                    try { _helper = (IDropTargetHelper)new DragDropHelper(); }
                    catch (Exception) { _failed = true; }
                }
                return _helper;
            }
        }

        public static void Enter(IntPtr hwnd, IDataObject data, DragDropEffects effect) => Call(h =>
        {
            if (data is not ComDataObject com) return;
            Native.GetCursorPos(out var pt);
            h.DragEnter(hwnd, com, ref pt, (int)effect);
        });

        public static void Over(DragDropEffects effect) => Call(h =>
        {
            Native.GetCursorPos(out var pt);
            h.DragOver(ref pt, (int)effect);
        });

        public static void Leave() => Call(h => h.DragLeave());

        public static void Drop(IDataObject data, DragDropEffects effect) => Call(h =>
        {
            if (data is not ComDataObject com) { h.DragLeave(); return; }
            Native.GetCursorPos(out var pt);
            h.Drop(com, ref pt, (int)effect);
        });

        static void Call(Action<IDropTargetHelper> action)
        {
            var h = Helper;
            if (h == null) return;
            try { action(h); }
            catch (Exception) { }
        }
    }
}
