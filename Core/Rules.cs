using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Desko.Core
{
    // Auto-organize rules: semicolon separated wildcard patterns, plus <folder> for directories.
    internal static class Rules
    {
        public const string FolderToken = "<folder>";

        public static readonly (string Name, string[] Patterns)[] Presets =
        {
            ("Hình ảnh", new[] { "*.jpg", "*.jpeg", "*.png", "*.gif", "*.bmp", "*.webp", "*.heic", "*.svg", "*.ico", "*.tif", "*.tiff" }),
            ("Tài liệu", new[] { "*.doc", "*.docx", "*.pdf", "*.txt", "*.rtf", "*.odt", "*.xls", "*.xlsx", "*.csv", "*.ppt", "*.pptx", "*.md" }),
            ("Video", new[] { "*.mp4", "*.mkv", "*.avi", "*.mov", "*.wmv", "*.webm", "*.flv" }),
            ("Nhạc", new[] { "*.mp3", "*.wav", "*.flac", "*.aac", "*.ogg", "*.m4a", "*.wma" }),
            ("File nén", new[] { "*.zip", "*.rar", "*.7z", "*.tar", "*.gz", "*.iso" }),
            ("Shortcut & ứng dụng", new[] { "*.lnk", "*.url", "*.exe", "*.msi", "*.bat", "*.cmd" }),
            ("Thư mục", new[] { FolderToken }),
        };

        static readonly ConcurrentDictionary<string, Regex> Cache = new(StringComparer.OrdinalIgnoreCase);

        public static List<string> Parse(string text) =>
            text.Split(new[] { ';', ',', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(Normalize)
                .Where(p => p.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

        // "jpg" or ".jpg" → "*.jpg"
        static string Normalize(string raw)
        {
            var p = raw.Trim();
            if (p.Length == 0 || p.Equals(FolderToken, StringComparison.OrdinalIgnoreCase)) return p.ToLowerInvariant();
            if (p.IndexOfAny(new[] { '*', '?' }) >= 0) return p;
            if (p.StartsWith(".")) return "*" + p;
            return p.Contains('.') ? p : "*." + p;
        }

        public static bool Matches(IEnumerable<string> rules, string path, bool isDirectory)
        {
            string name = Path.GetFileName(path);
            foreach (var rule in rules)
            {
                if (rule.Equals(FolderToken, StringComparison.OrdinalIgnoreCase))
                {
                    if (isDirectory) return true;
                    continue;
                }
                if (!isDirectory && Wildcard(rule).IsMatch(name)) return true;
            }
            return false;
        }

        static Regex Wildcard(string pattern) => Cache.GetOrAdd(pattern, p =>
            new Regex("^" + Regex.Escape(p).Replace(@"\*", ".*").Replace(@"\?", ".") + "$",
                      RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));

        // Things the auto-organizer must never touch (system files, downloads still in progress...).
        public static bool IsIgnorable(FileSystemInfo fsi)
        {
            if ((fsi.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) return true;
            var n = fsi.Name.ToLowerInvariant();
            if (n == "desktop.ini" || n == "thumbs.db" || n.StartsWith("~$")) return true;
            return n.EndsWith(".tmp") || n.EndsWith(".crdownload") || n.EndsWith(".part") ||
                   n.EndsWith(".partial") || n.EndsWith(".download") || n.EndsWith(".!ut");
        }
    }
}
