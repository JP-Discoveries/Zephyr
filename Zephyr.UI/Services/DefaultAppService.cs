using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Zephyr.UI.Services;

/// <summary>
/// Reads and changes the app a file extension opens with, the way the Explorer
/// properties sheet does: the friendly name comes from the shell association API,
/// and changing it hands off to the system "Open with" picker (Windows guards the
/// UserChoice registration, so the picker is the only reliable way to set it).
/// </summary>
public static class DefaultAppService
{
    /// <summary>Extensions the shell runs directly — there is no default app to reassign.</summary>
    private static readonly HashSet<string> SelfExecuting = new(StringComparer.OrdinalIgnoreCase)
        { ".exe", ".com", ".bat", ".cmd", ".lnk", ".msi", ".msc", ".cpl", ".scr", ".pif" };

    public static bool CanChange(string path)
    {
        if (Directory.Exists(path)) return false;
        var ext = Path.GetExtension(path);
        return ext.Length > 1 && !SelfExecuting.Contains(ext);
    }

    /// <summary>Friendly name of the current default app (e.g. "Notepad"), or null if none is set.</summary>
    public static string? GetFriendlyName(string path)
    {
        var exe = ResolveExecutable(path);
        if (exe == null) return null;   // unassociated — the shell would show the picker

        var name = Query(Path.GetExtension(path), ASSOCSTR_FRIENDLYAPPNAME);
        // Fall back to the executable's file name when no friendly name is registered.
        return string.IsNullOrWhiteSpace(name) ? Path.GetFileName(exe) : name;
    }

    /// <summary>Full path of the executable handling this extension, or null if it exists no more.</summary>
    public static string? GetExecutablePath(string path)
    {
        var exe = ResolveExecutable(path);
        return exe != null && File.Exists(exe) ? exe : null;
    }

    /// <summary>Executable registered for the extension, or null when nothing is associated.</summary>
    private static string? ResolveExecutable(string path)
    {
        var ext = Path.GetExtension(path);
        if (ext.Length <= 1) return null;

        var exe = Query(ext, ASSOCSTR_EXECUTABLE);
        if (string.IsNullOrWhiteSpace(exe)) return null;
        // Unassociated extensions resolve to the picker itself ("Pick an app").
        return exe.EndsWith("openwith.exe", StringComparison.OrdinalIgnoreCase) ? null : exe;
    }

    /// <summary>
    /// Shows the system "Open with" picker so the user can pick a new default. The chosen
    /// app is registered for the extension but not launched. Returns true if the picker was
    /// shown and the user made a choice.
    /// </summary>
    public static bool ChangeDefault(IntPtr owner, string path)
    {
        var info = new OPENASINFO
        {
            pcszFile    = path,
            pcszClass   = null,
            // Register the choice for the extension; deliberately no OAIF_EXEC so nothing opens.
            oaifInFlags = OAIF_ALLOW_REGISTRATION | OAIF_REGISTER_EXT | OAIF_FORCE_REGISTRATION
        };
        return SHOpenWithDialog(owner, ref info) == 0;
    }

    private static string? Query(string dotExt, uint str)
    {
        try
        {
            uint len = 0;
            AssocQueryString(ASSOCF_NONE, str, dotExt, null, null, ref len);
            if (len == 0) return null;

            var sb = new StringBuilder((int)len);
            int hr = AssocQueryString(ASSOCF_NONE, str, dotExt, null, sb, ref len);
            return hr == 0 && sb.Length > 0 ? sb.ToString() : null;
        }
        catch { return null; }
    }

    private const uint ASSOCF_NONE                = 0;
    private const uint ASSOCSTR_EXECUTABLE        = 2;
    private const uint ASSOCSTR_FRIENDLYAPPNAME   = 4;

    private const uint OAIF_ALLOW_REGISTRATION = 0x00000001;
    private const uint OAIF_REGISTER_EXT       = 0x00000002;
    private const uint OAIF_FORCE_REGISTRATION = 0x00000008;

    [DllImport("Shlwapi.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern int AssocQueryString(
        uint flags, uint str, string assoc, string? extra, StringBuilder? outBuffer, ref uint outBufferSize);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHOpenWithDialog(IntPtr hwndParent, ref OPENASINFO poainfo);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OPENASINFO
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string  pcszFile;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pcszClass;
        public uint oaifInFlags;
    }
}
