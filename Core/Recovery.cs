using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Desko.Interop;
using Desko.Models;

namespace Desko.Core
{
    // Guarantees fenced items are back on the desktop whenever the app is not running:
    //  --watchdog <pid> : tiny companion process; un-hides everything the moment the app dies (crash, Task Manager...).
    //  --restore        : registered to run at logon while items are hidden, for power loss / hard reset.
    // Neither touches anything if a (new) app instance is already running.
    internal static class Recovery
    {
        public const string MutexName = @"Local\Desko.SingleInstance";
        public const string ExitEventName = @"Local\Desko.Exit";
        public const string SettingsEventName = @"Local\Desko.ShowSettings";
        public const string WelcomeEventName = @"Local\Desko.ShowWelcome";

        // Asks the running app to quit normally (files shown again, settings saved) and waits for it.
        public static void RequestExit()
        {
            if (!EventWaitHandle.TryOpenExisting(ExitEventName, out var exitEvent)) return;
            using (exitEvent) exitEvent.Set();
            var waited = Stopwatch.StartNew();
            while (IsAppRunning() && waited.Elapsed < TimeSpan.FromSeconds(15)) Thread.Sleep(100);
        }

        public static void StartWatchdog()
        {
            try
            {
                if (Environment.ProcessPath is not string exe) return;
                // Prefer the small dedicated guard shipped next to the app; fall back to a second copy of ourselves.
                string guard = Path.Combine(Path.GetDirectoryName(exe)!, "DeskoGuard.exe");
                if (File.Exists(guard))
                {
                    Process.Start(new ProcessStartInfo(guard, Environment.ProcessId.ToString())
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    });
                    return;
                }
                Process.Start(new ProcessStartInfo(exe, $"--watchdog {Environment.ProcessId}")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
            }
            catch (Exception ex) { Log.Error(ex); }
        }

        public static void RunWatchdog(int pid)
        {
            try
            {
                using var app = Process.GetProcessById(pid);
                app.WaitForExit();
            }
            catch (ArgumentException) { } // already gone
            if (!IsAppRunning()) ReleaseAll();
        }

        public static void RunRestore()
        {
            if (!IsAppRunning()) ReleaseAll();
        }

        static bool IsAppRunning()
        {
            if (!Mutex.TryOpenExisting(MutexName, out var mutex)) return false;
            mutex.Dispose();
            return true;
        }

        static void ReleaseAll()
        {
            var config = ConfigStore.Load();
            ShowVirtualIcons(config);
            if (config.HiddenByUs.Count > 0 || config.IconsHiddenByUs)
            {
                foreach (var path in config.HiddenByUs) SetHidden(path, false);
                config.HiddenByUs.Clear();
                if (config.IconsHiddenByUs)
                {
                    DesktopShell.SetIconsVisible(true);
                    config.IconsHiddenByUs = false;
                }
                ConfigStore.Save(config);
            }
            AutoStart.SetRestoreEntry(false);
        }

        // Brings back the original Recycle Bin / This PC... icons. Their fence shortcuts stay hidden,
        // so the desktop never shows both, and the next start moves them back into the fence.
        public static void ShowVirtualIcons(AppConfig config)
        {
            bool changed = false;
            foreach (var v in config.VirtualIcons) changed |= Shell.VirtualItems.SetDesktopIconHidden(v.Clsid, false);
            if (changed) Shell.VirtualItems.RefreshDesktop();
        }

        public static void SetHidden(string path, bool hidden)
        {
            try
            {
                if (!File.Exists(path) && !Directory.Exists(path)) return;
                var attrs = File.GetAttributes(path);
                var next = hidden ? attrs | FileAttributes.Hidden : attrs & ~FileAttributes.Hidden;
                if (next == attrs) return;
                File.SetAttributes(path, next);
                Native.NotifyAttributesChanged(path);
            }
            catch (Exception ex) { Log.Error(ex); }
        }
    }
}
