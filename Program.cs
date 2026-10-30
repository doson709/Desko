using System;
using Desko.Core;

namespace Desko
{
    public static class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            // Helper modes run without any UI (see Recovery); the last three are used by setup.bat / uninstall.bat.
            LegacyName.MigrateData(); // data of the pre-2.0 "DeskFences" name, before anything reads the config
            string mode = args.Length > 0 ? args[0] : "";
            switch (mode)
            {
                case "--watchdog" when args.Length > 1 && int.TryParse(args[1], out int pid):
                    Recovery.RunWatchdog(pid);
                    return;
                case "--restore":
                    Recovery.RunRestore();
                    return;
                case "--exit":
                    Recovery.RequestExit();
                    return;
                case "--autostart":
                    AutoStart.Enabled = args.Length > 1 && args[1] == "on";
                    return;
                case "--uninstall":
                    Recovery.RequestExit();
                    Recovery.RunRestore();
                    AutoStart.Enabled = false;
                    AutoStart.SetRestoreEntry(false);
                    return;
            }

            var app = new App();
            app.InitializeComponent();
            app.Run();
        }
    }
}
