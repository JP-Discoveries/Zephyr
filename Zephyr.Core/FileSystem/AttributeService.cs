namespace Zephyr.Core.FileSystem;

/// <summary>
/// A batch edit to apply to a set of files/folders. Null attribute flags and null
/// timestamps mean "leave unchanged". When <see cref="Recursive"/> is set, the edit
/// also descends into the contents of any selected folder.
/// </summary>
public sealed class AttributeEdit
{
    public bool? ReadOnly { get; set; }
    public bool? Hidden   { get; set; }
    public bool? System   { get; set; }
    public bool? Archive  { get; set; }

    public DateTime? Created  { get; set; }
    public DateTime? Modified { get; set; }
    public DateTime? Accessed { get; set; }

    public bool Recursive { get; set; }

    public bool TouchesAttributes => ReadOnly.HasValue || Hidden.HasValue || System.HasValue || Archive.HasValue;
    public bool TouchesTimestamps => Created.HasValue || Modified.HasValue || Accessed.HasValue;
    public bool HasWork => TouchesAttributes || TouchesTimestamps;
}

public static class AttributeService
{
    /// <summary>Applies the edit to every target (and descendants when recursive). Returns counts.</summary>
    public static (int Changed, int Failed) Apply(IEnumerable<string> targets, AttributeEdit edit)
    {
        int changed = 0, failed = 0;
        foreach (var target in targets)
            foreach (var path in Expand(target, edit.Recursive))
            {
                try   { ApplyOne(path, edit); changed++; }
                catch { failed++; }
            }
        return (changed, failed);
    }

    private static IEnumerable<string> Expand(string root, bool recursive)
    {
        yield return root;
        if (!recursive || !Directory.Exists(root)) yield break;

        IEnumerable<string> children;
        try { children = Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories); }
        catch { yield break; }

        foreach (var child in children) yield return child;
    }

    private static void ApplyOne(string path, AttributeEdit edit)
    {
        bool isDir = Directory.Exists(path);

        // Timestamps first — setting them is unaffected by the read-only flag, and doing
        // this before (re)applying attributes avoids any ordering surprises.
        if (edit.Created is { } c)
        {
            if (isDir) Directory.SetCreationTime(path, c); else File.SetCreationTime(path, c);
        }
        if (edit.Modified is { } m)
        {
            if (isDir) Directory.SetLastWriteTime(path, m); else File.SetLastWriteTime(path, m);
        }
        if (edit.Accessed is { } a)
        {
            if (isDir) Directory.SetLastAccessTime(path, a); else File.SetLastAccessTime(path, a);
        }

        if (!edit.TouchesAttributes) return;

        var attr = File.GetAttributes(path);
        attr = Set(attr, FileAttributes.ReadOnly, edit.ReadOnly);
        attr = Set(attr, FileAttributes.Hidden,   edit.Hidden);
        attr = Set(attr, FileAttributes.System,   edit.System);
        attr = Set(attr, FileAttributes.Archive,  edit.Archive);
        File.SetAttributes(path, attr);
    }

    private static FileAttributes Set(FileAttributes current, FileAttributes flag, bool? state) => state switch
    {
        true  => current | flag,
        false => current & ~flag,
        null  => current,
    };
}
