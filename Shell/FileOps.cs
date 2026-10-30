using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace Desko.Shell
{
    // File operations through the shell, so users get Explorer's progress/conflict dialogs,
    // UAC prompts, Recycle Bin and Ctrl+Z undo.
    internal static class FileOps
    {
        const uint FO_MOVE = 1, FO_COPY = 2, FO_DELETE = 3, FO_RENAME = 4;
        const ushort FOF_MULTIDESTFILES = 0x1, FOF_SILENT = 0x4, FOF_RENAMEONCOLLISION = 0x8, FOF_NOCONFIRMATION = 0x10,
                     FOF_ALLOWUNDO = 0x40, FOF_NOCONFIRMMKDIR = 0x200, FOF_NOERRORUI = 0x400,
                     FOF_WANTNUKEWARNING = 0x4000;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct SHFILEOPSTRUCT
        {
            public IntPtr hwnd;
            public uint wFunc;
            [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
            [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
            public ushort fFlags;
            [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
            public IntPtr hNameMappings;
            [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

        public static bool Move(IEnumerable<string> sources, string destDir, IntPtr owner,
                                bool quiet = false, bool renameOnCollision = false)
        {
            ushort flags = (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMMKDIR);
            if (renameOnCollision) flags |= FOF_RENAMEONCOLLISION;
            if (quiet) flags |= FOF_SILENT | FOF_NOERRORUI | FOF_RENAMEONCOLLISION;
            return Run(FO_MOVE, sources, destDir, flags, owner);
        }

        // Moves one item to an exact destination path (used to pick a non-colliding name ourselves).
        public static bool MoveTo(string source, string destPath, IntPtr owner) =>
            Run(FO_MOVE, new[] { source }, destPath,
                (ushort)(FOF_MULTIDESTFILES | FOF_ALLOWUNDO | FOF_NOCONFIRMMKDIR | FOF_SILENT | FOF_NOERRORUI), owner);

        public static bool Copy(IEnumerable<string> sources, string destDir, IntPtr owner, bool renameOnCollision = false)
        {
            ushort flags = (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMMKDIR);
            if (renameOnCollision) flags |= FOF_RENAMEONCOLLISION;
            return Run(FO_COPY, sources, destDir, flags, owner);
        }

        public static bool Delete(IEnumerable<string> paths, IntPtr owner, bool permanent = false)
        {
            ushort flags = permanent ? (ushort)0 : (ushort)(FOF_ALLOWUNDO | FOF_WANTNUKEWARNING);
            return Run(FO_DELETE, paths, null, flags, owner);
        }

        public static bool Rename(string path, string newPath, IntPtr owner) =>
            Run(FO_RENAME, new[] { path }, newPath, FOF_ALLOWUNDO, owner);

        static bool Run(uint func, IEnumerable<string> from, string? to, ushort flags, IntPtr owner)
        {
            var list = from.Where(p => !string.IsNullOrEmpty(p)).ToList();
            if (list.Count == 0) return true;
            var op = new SHFILEOPSTRUCT
            {
                hwnd = owner,
                wFunc = func,
                pFrom = string.Join("\0", list) + "\0\0",
                pTo = to == null ? null : to + "\0\0",
                fFlags = flags,
            };
            int result = SHFileOperation(ref op);
            return result == 0 && !op.fAnyOperationsAborted;
        }
    }
}
