using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Threading;

namespace DeskoGuard
{
    // Usage: DeskoGuard.exe <pid of Desko.exe>
    // Waits for Desko to exit. If it died without cleaning up (crash, Task Manager...), desktop icons may still
    // be parked off screen or switched off: "Desko.exe --restore" (Recovery.RunRestore) brings them back.
    // Stays tiny itself (Native AOT, no COM): the one-off restore runs in the full app.
    internal static class Program
    {
        // Overridable only so tests can run an isolated copy against a scratch config (same variables as Desko).
        static readonly string MutexName = Environment.GetEnvironmentVariable("DESKO_GUARD_MUTEX") ??
            $@"Local\Desko{Environment.GetEnvironmentVariable("DESKO_INSTANCE")}.SingleInstance";
        const int WM_COMMAND = 0x0111, CmdToggleDesktopIcons = 0x7402;

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
                            Path.Combine(Environment.GetEnvironmentVariable("DESKO_DATA_DIR") ??
                                         Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Desko"), "config.json");
            if (!File.Exists(config) || JsonNode.Parse(File.ReadAllText(config)) is not JsonObject root) return;

            bool parked = root["IconsParked"]?.GetValue<bool>() == true;
            bool iconsOff = root["IconsHiddenByUs"]?.GetValue<bool>() == true;
            bool legacy = root["HiddenByUs"] is JsonArray { Count: > 0 } || root["VirtualIcons"] is JsonArray { Count: > 0 };
            if (!parked && !iconsOff && !legacy) return; // a clean exit

            // The icons themselves first: that much works even without the app.
            if (iconsOff) ShowDesktopIconView();
            string app = Path.Combine(AppContext.BaseDirectory, "Desko.exe");
            if (File.Exists(app))
                Process.Start(new ProcessStartInfo(app, "--restore") { UseShellExecute = false, CreateNoWindow = true });
        }

        static void ShowDesktopIconView()
        {
            IntPtr dv = FindWindowEx(FindWindow("Progman", null), IntPtr.Zero, "SHELLDLL_DefView", null);
            for (IntPtr w = FindWindowEx(IntPtr.Zero, IntPtr.Zero, "WorkerW", null); dv == IntPtr.Zero && w != IntPtr.Zero;
                 w = FindWindowEx(IntPtr.Zero, w, "WorkerW", null))
                dv = FindWindowEx(w, IntPtr.Zero, "SHELLDLL_DefView", null);
            IntPtr lv = dv == IntPtr.Zero ? IntPtr.Zero : FindWindowEx(dv, IntPtr.Zero, "SysListView32", null);
            if (lv != IntPtr.Zero && !IsWindowVisible(lv))
                PostMessage(dv, WM_COMMAND, new IntPtr(CmdToggleDesktopIcons), IntPtr.Zero);
        }
    }
}
