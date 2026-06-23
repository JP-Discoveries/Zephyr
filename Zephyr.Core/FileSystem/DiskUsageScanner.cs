namespace Zephyr.Core.FileSystem;

/// <summary>
/// One node in a disk-usage tree: a file (leaf) or a folder (with children). Byte sizes
/// are aggregated bottom-up so a folder's <see cref="Bytes"/> is the total of its subtree.
/// </summary>
public sealed class UsageNode
{
    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public bool IsDirectory { get; init; }
    public long Bytes { get; set; }
    public long Files { get; set; }            // count of files in this subtree
    public string Extension { get; init; } = string.Empty;
    public UsageNode? Parent { get; set; }
    public double PercentOfParent { get; set; }
    public List<UsageNode> Children { get; } = [];

    public bool HasChildren => Children.Count > 0;

    /// <summary>Sub-folders only, largest first — drives the folder tree view.</summary>
    public IEnumerable<UsageNode> SubFolders =>
        Children.Where(c => c.IsDirectory).OrderByDescending(c => c.Bytes);

    public bool HasSubFolders => Children.Any(c => c.IsDirectory);

    public string SizeDisplay    => FormatSize(Bytes);
    public string FilesDisplay   => Files.ToString("N0");
    public string PercentDisplay => PercentOfParent.ToString("0.0") + "%";

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):F2} GB"
    };
}

public static class DiskUsageScanner
{
    /// <summary>
    /// Recursively measures <paramref name="root"/> and returns its usage tree. Reports the
    /// running file count via <paramref name="progress"/>. Honours cancellation between entries.
    /// </summary>
    public static Task<UsageNode> ScanAsync(
        string root, IProgress<long>? progress, CancellationToken ct) =>
        Task.Run(() =>
        {
            long scanned = 0;
            var node = Scan(root, progress, ref scanned, ct);
            node.PercentOfParent = 100;
            progress?.Report(scanned);
            return node;
        }, ct);

    private static UsageNode Scan(string path, IProgress<long>? progress, ref long scanned, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var node = new UsageNode
        {
            Name        = Path.GetFileName(path.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : path,
            FullPath    = path,
            IsDirectory = true,
        };

        IEnumerable<string> entries;
        try { entries = Directory.EnumerateFileSystemEntries(path); }
        catch { return node; } // access denied / gone — count it as empty

        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            FileAttributes attr;
            try { attr = File.GetAttributes(entry); }
            catch { continue; }

            // Don't follow junctions/symlinks: avoids cycles and double-counting.
            if ((attr & FileAttributes.ReparsePoint) != 0) continue;

            if ((attr & FileAttributes.Directory) != 0)
            {
                var child = Scan(entry, progress, ref scanned, ct);
                node.Bytes += child.Bytes;
                node.Files += child.Files;
                if (child.Bytes > 0) { child.Parent = node; node.Children.Add(child); }
            }
            else
            {
                long len;
                try { len = new FileInfo(entry).Length; }
                catch { continue; }

                if (len > 0)
                    node.Children.Add(new UsageNode
                    {
                        Name      = Path.GetFileName(entry),
                        FullPath  = entry,
                        Bytes     = len,
                        Files     = 1,
                        Extension = Path.GetExtension(entry).ToLowerInvariant(),
                        Parent    = node,
                    });
                node.Bytes += len;
                node.Files++;

                if (++scanned % 2000 == 0) progress?.Report(scanned);
            }
        }

        // Now that this folder's total is known, record each child's share of it.
        foreach (var child in node.Children)
            child.PercentOfParent = node.Bytes > 0 ? child.Bytes * 100.0 / node.Bytes : 0;

        return node;
    }
}
