using System.IO;
using System.Text.Json;
using Zephyr.Core.Models;
using Zephyr.Core.Settings;

namespace Zephyr.UI.Services;

/// <summary>
/// Persists per-path colour labels (Finder-style). Keyed by full path; entries are
/// written eagerly on every change so labels survive a crash. Renames/moves orphan the
/// label — that is pruned lazily on load when the path no longer exists.
/// </summary>
public static class FileLabelService
{
    private static Dictionary<string, string> _labels = new(StringComparer.OrdinalIgnoreCase);

    private static string DataPath =>
        SettingsService.IsPortableMode
            ? Path.Combine(AppContext.BaseDirectory, "labels.json")
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Zephyr", "labels.json");

    public static void Load()
    {
        try
        {
            var path = DataPath;
            if (!File.Exists(path)) return;
            var raw = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
            if (raw == null) return;
            _labels = new Dictionary<string, string>(raw, StringComparer.OrdinalIgnoreCase);
        }
        catch { _labels = new(StringComparer.OrdinalIgnoreCase); }
    }

    public static void Save()
    {
        try
        {
            var path = DataPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(_labels));
        }
        catch { }
    }

    /// <summary>The label key assigned to a path, or null if none.</summary>
    public static string? GetKey(string path) =>
        _labels.TryGetValue(path, out var key) ? key : null;

    /// <summary>The hex colour assigned to a path, or "" if none.</summary>
    public static string GetHex(string path) =>
        FileLabels.HexFor(GetKey(path)) ?? "";

    /// <summary>Assigns a label key to a path; a null/empty key clears it. Saves immediately.</summary>
    public static void Set(string path, string? key)
    {
        if (string.IsNullOrEmpty(key)) _labels.Remove(path);
        else                          _labels[path] = key;
        Save();
    }

    public static bool HasAny => _labels.Count > 0;
}
