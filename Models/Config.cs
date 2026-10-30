using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Desko.Models
{
    public enum SortMode { Custom, Name, Type, Date, Size }

    public sealed class FenceConfig
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Title { get; set; } = "Fence mới";

        // Tabs: every tab is a FenceConfig; tabs of one fence share a GroupId (empty = a fence with one tab).
        // Tab order is their order in AppConfig.Fences. Placement and look are kept identical across a group.
        public string GroupId { get; set; } = "";
        public bool ActiveTab { get; set; }

        // Normal fences list Desktop items by name (Members); portals show FolderPath.
        public bool IsPortal { get; set; }
        public string FolderPath { get; set; } = "";
        public List<string> Members { get; set; } = new();

        // Physical screen pixels; Height is the expanded height even while rolled up.
        public int X { get; set; } = 100;
        public int Y { get; set; } = 100;
        public int Width { get; set; } = 340;
        public int Height { get; set; } = 220;

        public bool RolledUp { get; set; }
        public bool Locked { get; set; }
        // "#RRGGBB", "auto" (follows Windows light/dark) or "accent" (the Windows accent colour).
        public string Color { get; set; } = "auto";
        public double Opacity { get; set; } = 0.4;
        // 0 = same size as the desktop's icons.
        public int IconSize { get; set; }
        public SortMode SortMode { get; set; } = SortMode.Custom;
        public List<string> Order { get; set; } = new();
        public List<string> Rules { get; set; } = new();
    }

    public sealed class AppConfig
    {
        public bool FirstRunDone { get; set; }
        public bool DoubleClickToHide { get; set; } = true;
        public bool HideIconsToo { get; set; } = true;
        public bool RightDragToCreate { get; set; } = true;
        public bool AutoApplyRules { get; set; }
        public bool SingleClickOpen { get; set; }
        public bool SnapToGrid { get; set; } = true;
        public int GridSize { get; set; } = 10;
        public bool BlurBehind { get; set; } = true; // superseded by Backdrop; kept so old configs still read
        // "Acrylic" (frosted glass), "Blur" or "Clear".
        public string Backdrop { get; set; } = "Acrylic";
        // GPU rendering costs ~60 MB more for no visible gain on mostly static fences; applies after a restart.
        public bool GpuRendering { get; set; }
        public bool IconsHiddenByUs { get; set; }

        // Fences created before "follow the desktop icon size" existed were fixed at 48; switch them over once.
        public bool FollowDesktopIconSizeMigrated { get; set; }

        // Desktop items we set the Hidden attribute on; persisted so a crash never strands them.
        public List<string> HiddenByUs { get; set; } = new();

        // Standard desktop icons (Recycle Bin, This PC...) moved into a fence as a shortcut.
        public List<VirtualIcon> VirtualIcons { get; set; } = new();

        public List<FenceConfig> Fences { get; set; } = new();

        // Display signature → fence group → rectangle (see DisplayLayouts).
        public Dictionary<string, Dictionary<string, FenceRect>> Layouts { get; set; } = new();
    }

    public sealed class FenceRect
    {
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
    }

    public sealed class VirtualIcon
    {
        public string Clsid { get; set; } = "";
        public string ShortcutName { get; set; } = "";
    }

    internal static class ConfigStore
    {
        public static readonly string AppDataDir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Desko");

        public static readonly string ConfigPath = Path.Combine(AppDataDir, "config.json");

        // Where version 1.0 moved fenced files; only read when migrating them back to the Desktop.
        public static readonly string LegacyStorageRoot =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "DeskFences");

        public static AppConfig Load()
        {
            try
            {
                if (File.Exists(ConfigPath))
                    return JsonSerializer.Deserialize(File.ReadAllText(ConfigPath), ConfigJson.Default.AppConfig) ?? new AppConfig();
            }
            catch (Exception ex)
            {
                Log.Error(ex);
                try { File.Copy(ConfigPath, ConfigPath + ".broken", true); } catch { }
            }
            return new AppConfig();
        }

        public static void Save(AppConfig config)
        {
            try
            {
                Directory.CreateDirectory(AppDataDir);
                string tmp = ConfigPath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(config, ConfigJson.Default.AppConfig));
                File.Move(tmp, ConfigPath, true);
            }
            catch (Exception ex) { Log.Error(ex); }
        }
    }

    // Compile-time JSON serializer: no reflection warm-up when the config is read at startup.
    [JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
    [JsonSerializable(typeof(AppConfig))]
    internal partial class ConfigJson : JsonSerializerContext { }

    internal static class Log
    {
        static readonly object Gate = new();

        public static void Error(Exception? ex)
        {
            if (ex == null) return;
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(ConfigStore.AppDataDir);
                    string path = Path.Combine(ConfigStore.AppDataDir, "error.log");
                    // Keep it bounded: past 1 MB the log is rotated to error.old.log.
                    if (File.Exists(path) && new FileInfo(path).Length > 1024 * 1024)
                        File.Move(path, Path.Combine(ConfigStore.AppDataDir, "error.old.log"), true);
                    File.AppendAllText(path,
                        $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\r\n\r\n");
                }
            }
            catch { }
        }
    }
}
