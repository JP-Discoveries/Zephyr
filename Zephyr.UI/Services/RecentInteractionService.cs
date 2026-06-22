using System.IO;
using System.Text.Json;
using Zephyr.Core.Settings;

namespace Zephyr.UI.Services;

public static class RecentInteractionService
{
    private static Dictionary<string, DateTime> _records = [];
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);
    private const int MaxEntries = 1000;

    private static string DataPath =>
        SettingsService.IsPortableMode
            ? Path.Combine(AppContext.BaseDirectory, "interactions.json")
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Zephyr", "interactions.json");

    public static void Load()
    {
        try
        {
            var path = DataPath;
            if (!File.Exists(path)) return;
            var raw = JsonSerializer.Deserialize<Dictionary<string, DateTime>>(File.ReadAllText(path));
            if (raw == null) return;
            var cutoff = DateTime.Now - MaxAge;
            _records = raw.Where(kv => kv.Value >= cutoff)
                          .ToDictionary(kv => kv.Key, kv => kv.Value);
        }
        catch { _records = []; }
    }

    public static void Save()
    {
        try
        {
            var path = DataPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var cutoff = DateTime.Now - MaxAge;
            var toSave = _records.Where(kv => kv.Value >= cutoff)
                                 .ToDictionary(kv => kv.Key, kv => kv.Value);
            File.WriteAllText(path, JsonSerializer.Serialize(toSave));
        }
        catch { }
    }

    public static void Record(string path)
    {
        _records[path] = DateTime.Now;
        if (_records.Count > MaxEntries)
        {
            var oldest = _records.OrderBy(kv => kv.Value).First().Key;
            _records.Remove(oldest);
        }
    }

    public static bool IsRecent(string path) =>
        _records.TryGetValue(path, out var time) && (DateTime.Now - time) <= MaxAge;

    public static DateTime? GetTime(string path) =>
        _records.TryGetValue(path, out var time) ? time : null;
}
