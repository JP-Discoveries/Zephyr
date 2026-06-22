using System.IO;
using System.Text.Json;

namespace Zephyr.Core.Settings;

public static class SettingsService
{
    private static readonly JsonSerializerOptions _json = new() { WriteIndented = true };

    public static ZephyrSettings Current { get; private set; } = new();

    public static bool IsPortableMode
        => File.Exists(Path.Combine(AppContext.BaseDirectory, "portable"));

    private static string SettingsPath
    {
        get
        {
            if (IsPortableMode)
                return Path.Combine(AppContext.BaseDirectory, "settings.json");
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Zephyr", "settings.json");
        }
    }

    public static void Load()
    {
        try
        {
            var path = SettingsPath;
            if (!File.Exists(path)) return;
            Current = JsonSerializer.Deserialize<ZephyrSettings>(File.ReadAllText(path)) ?? new();
        }
        catch { Current = new(); }
    }

    public static void Save(ZephyrSettings settings)
    {
        Current = settings;
        try
        {
            var path = SettingsPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(settings, _json));
        }
        catch { }
    }

    public static void EnablePortableMode()
    {
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "portable"), "");
        Save(Current);
    }

    public static void DisablePortableMode()
    {
        // Write to AppData first, then remove the portable marker
        var appDataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Zephyr", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(appDataPath)!);
        File.WriteAllText(appDataPath, JsonSerializer.Serialize(Current, _json));
        var marker = Path.Combine(AppContext.BaseDirectory, "portable");
        if (File.Exists(marker)) File.Delete(marker);
    }
}
