using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using Microsoft.Win32;

namespace DeskoGuard
{
    // Usage: DeskoGuard.exe <pid of Desko.exe>
    // Mirrors Desko's Recovery.ReleaseAll, but only edits the fields it owns in config.json.
    internal static class Program
    {
        // Overridable only so tests can run the guard against a scratch config instead of the real one.
        static readonly string MutexName = Environment.GetEnvironmentVariable("DESKO_GUARD_MUTEX") ?? @"Local\Desko.SingleInstance";
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string HideIconsKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\NewStartPanel";
        const int SHCNE_ATTRIBUTES = 0x800, SHCNE_ASSOCCHANGED = 0x08000000;
        const uint SHCNF_PATHW = 0x5, SHCNF_FLUSHNOWAIT = 0x3000;
        const int WM_COMMAND = 0x0111, WM_KEYDOWN = 0x0100, VK_F5 = 0x74, CmdToggleDesktopIcons = 0x7402;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern void SHChangeNotify(int e, uint flags, string? a, IntPtr b);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? cls, string? name);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string? cls, string? name);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr w, IntPtr l);

        static int Main(string[] args)
        {
            if (args.Length < 1 || !int.TryParse(args[0], out int pid)) return 1;
            try
            {
                using var app = Process.GetProcessById(pid);
                app.WaitForExit();
            }
            catch (ArgumentException) { } // already gone

            // A new instance already took over (e.g. setup restarted the app): nothing to undo.
            if (Mutex.TryOpenExisting(MutexName, out var mutex))
            {
                mutex.Dispose();
                return 0;
            }
            try { Release(); } catch (Exception) { }
            return 0;
        }

        static void Release()
        {
            string config = Environment.GetEnvironmentVariable("DESKO_GUARD_CONFIG") ??
                            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Desko", "config.json");
            if (File.Exists(config) && JsonNode.Parse(File.ReadAllText(config)) is JsonObject root)
            {
                bool changed = false, refresh = false;

                if (root["VirtualIcons"] is JsonArray virtualIcons)
                    foreach (var v in virtualIcons)
                        if (v?["Clsid"]?.GetValue<string>() is string clsid) refresh |= ShowDesktopIcon(clsid);

                if (root["HiddenByUs"] is JsonArray hidden && hidden.Count > 0)
                {
                    foreach (var h in hidden)
                        if (h?.GetValue<string>() is string path) Unhide(path);
                    root["HiddenByUs"] = new JsonArray();
                    changed = true;
                }

                if (root["IconsHiddenByUs"]?.GetValue<bool>() == true)
                {
                    ShowDesktopIconView();
                    root["IconsHiddenByUs"] = false;
                    changed = true;
                }

                if (refresh) RefreshDesktop();
                if (changed)
                {
                    string tmp = config + ".tmp";
                    File.WriteAllText(tmp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                    File.Move(tmp, config, true);
                }
            }

            using var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            run?.DeleteValue("DeskoRestore", false);
        }

        static void Unhide(string path)
        {
            try
            {
                if (!File.Exists(path) && !Directory.Exists(path)) return;
                var attrs = File.GetAttributes(path);
                if ((attrs & FileAttributes.Hidden) == 0) return;
                File.SetAttributes(path, attrs & ~FileAttributes.Hidden);
                SHChangeNotify(SHCNE_ATTRIBUTES, SHCNF_PATHW | SHCNF_FLUSHNOWAIT, path, IntPtr.Zero);
            }
            catch (Exception) { }
        }

        static bool ShowDesktopIcon(string clsid)
        {
            using var key = Registry.CurrentUser.OpenSubKey(HideIconsKey, writable: true);
            if (key?.GetValue(clsid) is not int value || value == 0) return false;
            key.SetValue(clsid, 0, RegistryValueKind.DWord);
            return true;
        }

        static IntPtr FindDefView()
        {
            IntPtr dv = FindWindowEx(FindWindow("Progman", null), IntPtr.Zero, "SHELLDLL_DefView", null);
            for (IntPtr w = FindWindowEx(IntPtr.Zero, IntPtr.Zero, "WorkerW", null); dv == IntPtr.Zero && w != IntPtr.Zero;
                 w = FindWindowEx(IntPtr.Zero, w, "WorkerW", null))
                dv = FindWindowEx(w, IntPtr.Zero, "SHELLDLL_DefView", null);
            return dv;
        }

        static void ShowDesktopIconView()
        {
            IntPtr dv = FindDefView();
            IntPtr lv = dv == IntPtr.Zero ? IntPtr.Zero : FindWindowEx(dv, IntPtr.Zero, "SysListView32", null);
            if (lv != IntPtr.Zero && !IsWindowVisible(lv))
                PostMessage(dv, WM_COMMAND, new IntPtr(CmdToggleDesktopIcons), IntPtr.Zero);
        }

        static void RefreshDesktop()
        {
            SHChangeNotify(SHCNE_ASSOCCHANGED, 0, null, IntPtr.Zero);
            IntPtr dv = FindDefView();
            IntPtr lv = dv == IntPtr.Zero ? IntPtr.Zero : FindWindowEx(dv, IntPtr.Zero, "SysListView32", null);
            if (lv != IntPtr.Zero) PostMessage(lv, WM_KEYDOWN, new IntPtr(VK_F5), IntPtr.Zero);
        }
    }
}
