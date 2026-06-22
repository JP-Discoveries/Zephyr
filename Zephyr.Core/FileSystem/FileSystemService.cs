using Zephyr.Core.Models;

namespace Zephyr.Core.FileSystem;

public class FileSystemService
{
    public IReadOnlyList<FileItem> GetContents(string path, bool showHidden = false, bool showSystem = false)
    {
        var dir = new DirectoryInfo(path);
        if (!dir.Exists) return [];

        var items = new List<FileItem>();
        try
        {
            foreach (var entry in dir.EnumerateFileSystemInfos())
            {
                if (!showHidden && (entry.Attributes & FileAttributes.Hidden) != 0) continue;
                if (!showSystem && (entry.Attributes & FileAttributes.System) != 0) continue;

                items.Add(new FileItem
                {
                    Name = entry.Name,
                    FullPath = entry.FullName,
                    IsDirectory = entry is DirectoryInfo,
                    Size = entry is FileInfo fi ? fi.Length : 0,
                    LastModified = entry.LastWriteTime,
                    Created = entry.CreationTime,
                    Extension = entry is FileInfo ? Path.GetExtension(entry.Name).ToLowerInvariant() : string.Empty,
                    Attributes = entry.Attributes
                });
            }
        }
        catch (UnauthorizedAccessException) { }

        return items;
    }

    public IReadOnlyList<DriveItem> GetDrives()
    {
        return DriveInfo.GetDrives()
            .Where(d => d.IsReady)
            .Select(d => new DriveItem
            {
                Name = d.Name,
                Letter = d.Name.TrimEnd('\\'),
                Label = d.VolumeLabel,
                DriveType = d.DriveType,
                TotalSize = d.TotalSize,
                AvailableFreeSpace = d.AvailableFreeSpace
            })
            .ToList();
    }

    public string? GetParent(string path) => Directory.GetParent(path)?.FullName;

    public bool DirectoryExists(string path) => Directory.Exists(path);
}
