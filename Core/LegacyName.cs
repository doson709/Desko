using System;
using System.IO;
using Desko.Models;

namespace Desko.Core
{
    // Before 2.0 the app was called DeskFences. This carries an existing installation over to the new name;
    // it does nothing once there is nothing left under the old name.
    internal static class LegacyName
    {
        public const string Old = "DeskFences";

        // %APPDATA%\DeskFences (config, logs) → %APPDATA%\Desko. The old icon cache is simply dropped.
        public static void MigrateData()
        {
            try
            {
                string oldDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Old);
                if (Directory.Exists(oldDir) && !File.Exists(Path.Combine(ConfigStore.AppDataDir, "config.json")))
                {
                    if (!Directory.Exists(ConfigStore.AppDataDir)) Directory.Move(oldDir, ConfigStore.AppDataDir);
                    else
                    {
                        foreach (var file in Directory.GetFiles(oldDir))
                        {
                            string target = Path.Combine(ConfigStore.AppDataDir, Path.GetFileName(file));
                            if (!File.Exists(target)) File.Copy(file, target);
                        }
                        Directory.Delete(oldDir, recursive: true);
                    }
                }

                string oldCache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Old);
                if (Directory.Exists(oldCache)) Directory.Delete(oldCache, recursive: true);
            }
            catch (Exception ex) { Log.Error(ex); }
        }
    }
}
