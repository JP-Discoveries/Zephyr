namespace Zephyr.Core.Archives;

/// <summary>
/// Virtual path scheme for browsing inside an archive without extracting it:
///   <c>zip::C:\stuff\foo.zip::docs/readme.txt</c>
/// The part before the first "::" boundary is the real archive file on disk; the
/// remainder is the forward-slash internal path ("" = the archive root).
/// </summary>
public static class ArchivePath
{
    private const string Scheme = "zip::";
    private const string Sep    = "::";

    public static bool IsArchivePath(string path)
        => path.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase);

    /// <summary>Builds a virtual path for an internal location (inner "" = root).</summary>
    public static string Make(string archiveFile, string inner = "")
        => $"{Scheme}{archiveFile}{Sep}{NormalizeInner(inner)}";

    /// <summary>Splits a virtual path into (real archive file, internal path).</summary>
    public static (string Archive, string Inner) Parse(string path)
    {
        var rest = path[Scheme.Length..];
        int idx = rest.IndexOf(Sep, StringComparison.Ordinal);
        return idx < 0 ? (rest, "") : (rest[..idx], NormalizeInner(rest[(idx + Sep.Length)..]));
    }

    /// <summary>Normalizes an internal path to forward slashes with no leading/trailing slash.</summary>
    public static string NormalizeInner(string inner)
        => inner.Replace('\\', '/').Trim('/');
}
