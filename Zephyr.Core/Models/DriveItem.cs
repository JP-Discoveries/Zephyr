namespace Zephyr.Core.Models;

public class DriveItem
{
    public string Name { get; set; } = string.Empty;
    public string Letter { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public DriveType DriveType { get; set; }
    public long TotalSize { get; set; }
    public long AvailableFreeSpace { get; set; }
    public bool IsPortableDevice { get; set; }

    public string DisplayName => string.IsNullOrEmpty(Label)
        ? (string.IsNullOrEmpty(Letter) ? Name : Letter)
        : string.IsNullOrEmpty(Letter) ? Label : $"{Label} ({Letter})";
    public string FreeSpaceDisplay => FormatSize(AvailableFreeSpace);

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):F1} GB"
    };
}
