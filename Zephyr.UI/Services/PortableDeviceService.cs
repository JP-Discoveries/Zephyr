using System.IO;
using Zephyr.Core.Models;

namespace Zephyr.UI.Services;

public static class PortableDeviceService
{
    /// <param name="knownDrives">
    /// Logical drives already shown in the sidebar's Drives section. WPD also enumerates
    /// fixed internal volumes that sit on hot-pluggable SATA ports (Windows surfaces them as
    /// portable devices with their volume label as the friendly name). Those would duplicate
    /// the Drives entry, so we suppress any WPD device whose name matches a non-removable
    /// drive's label. Genuinely removable media (card readers, USB sticks) and real MTP
    /// devices (phones, cameras) are unaffected.
    /// </param>
    public static IEnumerable<DriveItem> GetPortableDevices(IEnumerable<DriveItem>? knownDrives = null)
    {
        var results   = new List<DriveItem>();
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var fixedDriveLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (knownDrives != null)
            foreach (var d in knownDrives)
                if (d.DriveType == DriveType.Fixed && !string.IsNullOrWhiteSpace(d.Label))
                    fixedDriveLabels.Add(d.Label);

        // ── CrossDevice (Windows Phone Link / Google integration) ─────────────
        var crossDevicePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "CrossDevice");
        if (Directory.Exists(crossDevicePath))
        {
            foreach (var dir in Directory.EnumerateDirectories(crossDevicePath))
            {
                string name = Path.GetFileName(dir);
                seenNames.Add(name);
                results.Add(new DriveItem
                {
                    Name             = dir,
                    Letter           = string.Empty,
                    Label            = name,
                    DriveType        = DriveType.Removable,
                    IsPortableDevice = true,
                });
            }
        }

        // ── WPD devices (cameras, traditional MTP phones) ─────────────────────
        foreach (var (deviceId, friendlyName) in WpdProvider.GetDevices())
        {
            if (string.IsNullOrEmpty(friendlyName)) continue;
            if (fixedDriveLabels.Contains(friendlyName)) continue; // a fixed drive already in Drives
            if (!seenNames.Add(friendlyName)) continue; // already added via CrossDevice

            results.Add(new DriveItem
            {
                Name             = WpdProvider.MakeRootPath(deviceId),
                Letter           = string.Empty,
                Label            = friendlyName,
                DriveType        = DriveType.Removable,
                IsPortableDevice = true,
            });
        }

        return results;
    }
}
