using System.IO;
using Microsoft.Win32;

namespace Zephyr.UI.Services;

public static class CloudSyncService
{
    private static readonly Lazy<IReadOnlyList<string>> _syncRoots =
        new(DetectSyncRoots, LazyThreadSafetyMode.ExecutionAndPublication);

    public static IReadOnlyList<string> SyncRoots => _syncRoots.Value;

    private static IReadOnlyList<string> DetectSyncRoots()
    {
        var roots = new List<string>();

        // OneDrive personal and business accounts
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\OneDrive\Accounts");
            if (key != null)
            {
                foreach (var sub in key.GetSubKeyNames())
                {
                    using var acct = key.OpenSubKey(sub);
                    var folder = acct?.GetValue("UserFolder") as string;
                    if (folder != null && Directory.Exists(folder))
                        roots.Add(folder.TrimEnd('\\'));
                }
            }
        }
        catch { }

        // Dropbox
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Dropbox\InfoV2");
            var path = key?.GetValue("path") as string;
            if (path != null && Directory.Exists(path))
                roots.Add(path.TrimEnd('\\'));
        }
        catch { }

        // Google Drive for Desktop
        try
        {
            var driveFs = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Google", "DriveFS");
            if (Directory.Exists(driveFs))
            {
                foreach (var acctDir in Directory.EnumerateDirectories(driveFs))
                {
                    var marker = Path.Combine(acctDir, "mount_point_prefix_do_not_delete");
                    if (File.Exists(marker))
                    {
                        var mount = File.ReadAllText(marker).Trim();
                        if (Directory.Exists(mount))
                            roots.Add(mount.TrimEnd('\\'));
                    }
                }
            }
        }
        catch { }

        return roots;
    }

    public static string GetBadge(string path, IReadOnlyList<string> roots)
    {
        foreach (var root in roots)
        {
            if (path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                return "☁";
        }
        return string.Empty;
    }
}
