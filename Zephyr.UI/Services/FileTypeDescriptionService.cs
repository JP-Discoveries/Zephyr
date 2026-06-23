using System.Runtime.InteropServices;
using System.Text;

namespace Zephyr.UI.Services;

/// <summary>
/// Resolves a file extension to its friendly type name the way Explorer does (e.g. ".dll"
/// → "Application extension"), via the shell association API. Results are cached.
/// </summary>
public static class FileTypeDescriptionService
{
    private static readonly Dictionary<string, string> _cache = new(StringComparer.OrdinalIgnoreCase);

    public static string Describe(string extension)
    {
        var ext = string.IsNullOrEmpty(extension) ? "" : extension.TrimStart('.').ToLowerInvariant();
        if (ext.Length == 0) return "(No extension)";
        if (_cache.TryGetValue(ext, out var cached)) return cached;

        string result = Query("." + ext) ?? $"{ext.ToUpperInvariant()} File";
        _cache[ext] = result;
        return result;
    }

    private static string? Query(string dotExt)
    {
        try
        {
            uint len = 0;
            // First call sizes the buffer.
            AssocQueryString(ASSOCF_NONE, ASSOCSTR_FRIENDLYDOCNAME, dotExt, null, null, ref len);
            if (len == 0) return null;

            var sb = new StringBuilder((int)len);
            int hr = AssocQueryString(ASSOCF_NONE, ASSOCSTR_FRIENDLYDOCNAME, dotExt, null, sb, ref len);
            return hr == 0 && sb.Length > 0 ? sb.ToString() : null;
        }
        catch { return null; }
    }

    private const uint ASSOCF_NONE = 0;
    private const uint ASSOCSTR_FRIENDLYDOCNAME = 3;

    [DllImport("Shlwapi.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern int AssocQueryString(
        uint flags, uint str, string assoc, string? extra, StringBuilder? outBuffer, ref uint outBufferSize);
}
