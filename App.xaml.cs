using System;
using System.Diagnostics;
using System.Threading;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Desko.Core;
using Desko.Interop;
using Desko.Models;
using Desko.UI;

namespace Desko
{
    public partial class App : Application
    {
        Mutex? _mutex;
        EventWaitHandle? _exitEvent;
        FenceManager? _manager;
        TrayIcon? _tray;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            _mutex = new Mutex(true, Recovery.MutexName, out bool created);
            if (!created)
            {
                // Already running: launching it again opens Settings (or the welcome) in the running copy.
                string name = e.Args.Contains("--welcome") ? Recovery.WelcomeEventName : Recovery.SettingsEventName;
                if (EventWaitHandle.TryOpenExisting(name, out var request))
                {
                    // We were just launched by the user, so we may bring windows forward: pass that on.
                    Native.AllowSetForegroundWindow(-1);
                    using (request) request.Set();
                }
                Shutdown();
                return;
            }

            // "Desko.exe --exit" (used by setup / uninstall) signals this to quit cleanly.
            _exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Recovery.ExitEventName);
            var settingsEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Recovery.SettingsEventName);
            var welcomeEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Recovery.WelcomeEventName);
            new Thread(() =>
            {
                var events = new WaitHandle[] { _exitEvent, settingsEvent, welcomeEvent };
                while (true)
                {
                    int which = WaitHandle.WaitAny(events);
                    if (which == 0) { Dispatcher.BeginInvoke(Shutdown); return; }
                    Dispatcher.BeginInvoke(() => { if (which == 1) _manager?.ShowSettings(); else _manager?.ShowWelcome(); });
                }
            }) { IsBackground = true, Name = "Desko requests" }.Start();

            DispatcherUnhandledException += (_, args) =>
            {
                Log.Error(args.Exception);
                args.Handled = true;
            };
            AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Error(args.ExceptionObject as Exception);

            var config = ConfigStore.Load();
            // Mostly static content: CPU rendering halves the memory (no Direct3D resources per fence window)
            // and the frosted backdrop still shows through. Must be decided before the first window exists.
            if (!config.GpuRendering)
                System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
            _manager = new FenceManager(config);
            WhenDesktopReady(() => StartFences(showWelcome: !config.FirstRunDone || e.Args.Contains("--welcome")));
        }

        // Launched at logon, the app can be up before Explorer has built the desktop; fences need it.
        void WhenDesktopReady(Action start)
        {
            if (DesktopShell.FindListView() != IntPtr.Zero)
            {
                start();
                return;
            }
            var waited = Stopwatch.StartNew();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            timer.Tick += (_, _) =>
            {
                if (DesktopShell.FindListView() == IntPtr.Zero && waited.Elapsed < TimeSpan.FromSeconds(60)) return;
                timer.Stop();
                start();
            };
            timer.Start();
        }

        // Fences first; everything the user does not see right away comes after they are on screen.
        void StartFences(bool showWelcome)
        {
            _manager!.Start();
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
            {
                _tray = new TrayIcon(_manager);
                _manager.Notify = (title, text) => _tray?.ShowTip(title, text);
                if (showWelcome) _manager.ShowWelcome();

                // The watchdog is a second .NET process; starting it later keeps it from competing
                // for the disk with our own startup right after logon.
                var later = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                later.Tick += (_, _) =>
                {
                    later.Stop();
                    Recovery.StartWatchdog();
                    // Off the UI thread: these run schtasks.exe.
                    Task.Run(() =>
                    {
                        AutoStart.Maintain();
                        AutoStart.Prime();
                        Dispatcher.BeginInvoke(() => _manager.UpdateRestoreEntry());
                        Shell.IconDiskCache.Purge();
                    });
                    // Startup leaves a lot of short-lived garbage behind; give that memory back once.
                    System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                    GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
                };
                later.Start();
            });
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _manager?.Shutdown();
            _tray?.Dispose();
            _mutex?.Dispose();
            base.OnExit(e);
        }
    }
}