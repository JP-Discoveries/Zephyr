using Zephyr.Core.Models;

namespace Zephyr.Core.FileSystem;

/// <summary>
/// Compares the visible contents of two folders by name and tags each item with a
/// <see cref="CompareStatus"/>. Files are compared by size and last-write time;
/// folders are matched by name only (no recursion). Timestamps within a small
/// tolerance count as equal to absorb FAT/exFAT resolution differences.
/// </summary>
public static class PaneComparer
{
    private static readonly TimeSpan TimeTolerance = TimeSpan.FromSeconds(2);

    public static void Compare(IReadOnlyList<FileItem> left, IReadOnlyList<FileItem> right)
    {
        var rightByName = BuildIndex(right);
        var leftByName  = BuildIndex(left);

        foreach (var item in left)  item.CompareStatus = StatusOf(item, rightByName);
        foreach (var item in right) item.CompareStatus = StatusOf(item, leftByName);
    }

    public static void Clear(IEnumerable<FileItem> items)
    {
        foreach (var item in items) item.CompareStatus = CompareStatus.None;
    }

    private static Dictionary<string, FileItem> BuildIndex(IReadOnlyList<FileItem> items)
    {
        var map = new Dictionary<string, FileItem>(items.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var item in items) map[item.Name] = item; // last wins; names are unique per folder
        return map;
    }

    private static CompareStatus StatusOf(FileItem item, Dictionary<string, FileItem> other)
    {
        if (!other.TryGetValue(item.Name, out var match))
            return CompareStatus.Unique;

        // A file and a folder sharing a name are not the same thing.
        if (item.IsDirectory != match.IsDirectory)
            return CompareStatus.Different;

        // Folders: present on both sides — treat as identical (we don't recurse).
        if (item.IsDirectory)
            return CompareStatus.Identical;

        bool sameTime = (item.LastModified - match.LastModified).Duration() <= TimeTolerance;
        if (item.Size == match.Size && sameTime)
            return CompareStatus.Identical;

        if (sameTime)
            return CompareStatus.Different; // same timestamp, different size

        return item.LastModified > match.LastModified ? CompareStatus.Newer : CompareStatus.Older;
    }
}
