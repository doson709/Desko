using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Desko.Interop;
using Desko.Models;
using Desko.Shell;

namespace Desko.Core
{
    // Guarantees the desktop is whole whenever the app is not running: no icon left parked off screen,
    // the icon view switched back on.
    //  --watchdog <pid> : companion process (when DeskoGuard.exe is missing); restores the moment the app dies.
    //  --restore        : run by DeskoGuard after a crash, and registered to run at logon while icons are
    //                     parked and the app won't start by itself (power loss / hard reset).
    // Neither touches anything if a (new) app instance is already running.
    internal static class Recovery
    {
        // A suffix only tests set, to run an isolated copy next to the installed one.
        static readonly string Instance = Environment.GetEnvironmentVariable("DESKO_INSTANCE") ?? "";
        public static readonly string MutexName = $@"Local\Desko{Instance}.SingleInstance";
        public static readonly string ExitEventName = $@"Local\Desko{Instance}.Exit";
        public static readonly string SettingsEventName = $@"Local\Desko{Instance}.ShowSettings";
        public static readonly string WelcomeEventName = $@"Local\Desko{Instance}.ShowWelcome";
        // Held while a restore moves icons, so a Desko starting at that moment waits instead of fighting it.
        static readonly string RestoringName = $@"Local\Desko{Instance}.Restoring";

        // Called by the app before it touches the desktop: lets a restore that is still running finish first.
        public static void WaitForRestore(TimeSpan timeout)
        {
            if (!Mutex.TryOpenExisting(RestoringName, out var busy)) return;
            using (busy)
            {
                try
                {
                    if (busy.WaitOne(timeout)) busy.ReleaseMutex();
                }
                catch (AbandonedMutexException) { busy.ReleaseMutex(); }
            }
        }

        // Asks the running app to quit normally (icons put back, settings saved) and waits for it.
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
            if (!IsAppRunning()) { Shell.DesktopIcons.EnsureRedraw(); ReleaseAll(); }
        }

        public static void RunRestore()
        {
            if (!IsAppRunning()) { Shell.DesktopIcons.EnsureRedraw(); ReleaseAll(); }
        }

        static bool IsAppRunning()
        {
            if (!Mutex.TryOpenExisting(MutexName, out var mutex)) return false;
            mutex.Dispose();
            return true;
        }

        static void ReleaseAll()
        {
            using var busy = new Mutex(true, RestoringName, out _);
            try { ReleaseAllCore(); }
            finally { busy.ReleaseMutex(); }
        }

        static void ReleaseAllCore()
        {
            var config = ConfigStore.Load();
            bool changed = false;

            // Leftovers of version 2 (fenced files hidden, Recycle Bin & co. replaced by shortcuts).
            if (config.VirtualIcons.Count > 0)
            {
                bool refresh = false;
                foreach (var v in config.VirtualIcons) refresh |= VirtualItems.SetDesktopIconHidden(v.Clsid, false);
                if (refresh) VirtualItems.RefreshDesktop();
            }
            if (config.HiddenByUs.Count > 0)
            {
                foreach (var path in config.HiddenByUs) SetHidden(path, false);
                config.HiddenByUs.Clear();
                changed = true;
            }

            if (config.IconsHiddenByUs)
            {
                DesktopShell.SetIconsVisible(true);
                config.IconsHiddenByUs = false;
                changed = true;
            }
            if (config.IconsParked && UnparkIcons(config))
            {
                config.IconsParked = false;
                changed = true;
            }
            // Never write over the settings of a Desko that started meanwhile: it owns them now.
            if (IsAppRunning()) return;
            if (changed) ConfigStore.Save(config);
            if (!config.IconsParked) AutoStart.SetRestoreEntry(false);
        }

        // Icons parked off screen (rolled-up fences, other tabs, scrolled rows) come back to free spots.
        // At logon Explorer may still be starting, so wait for its desktop a while.
        static bool UnparkIcons(AppConfig config)
        {
            List<DesktopIcons.Icon>? icons = null;
            var waited = Stopwatch.StartNew();
            while (waited.Elapsed < TimeSpan.FromSeconds(60))
            {
                if (DesktopShell.FindListView() != IntPtr.Zero && (icons = DesktopIcons.Enumerate()) != null) break;
                DesktopIcons.Reset();
                Thread.Sleep(1000);
            }
            if (icons == null) return false;
            IconGrid.Refresh();
            var visible = icons.Where(i => !DesktopIcons.IsParked(i.Position)).Select(i => IconGrid.CellRect(i.Position));
            // A test copy (DESKO_INSTANCE) only ever brings back its own icons, never those of the real Desko.
            var own = config.Fences.SelectMany(f => f.Members).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var parked = icons.Where(i => DesktopIcons.IsParked(i.Position) && (Instance.Length == 0 || own.Contains(i.Name))).ToList();
            var home = Native.WorkAreaFromPoint(new Native.POINT(0, 0));
            var spots = IconGrid.FreeCells(IconGrid.InCell(home.Left, home.Top), parked.Count, Array.Empty<Native.RECT>(), visible);
            // Desko came back meanwhile (a restart right after a crash): it lays the icons out itself.
            if (IsAppRunning()) return false;
            DesktopIcons.Move(parked.Select((icon, i) => (icon.Name, spots[i])).ToList());
            return true;
        }

        // Uninstall: "Auto arrange icons" / "Align icons to grid" back the way they were before Desko.
        public static void RestoreDesktopArrange()
        {
            var config = ConfigStore.Load();
            if (config.DesktopArrangeBefore is not int before) return;
            DesktopIcons.SetArrange((before & 1) != 0, (before & 2) != 0);
            config.DesktopArrangeBefore = null;
            ConfigStore.Save(config);
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
