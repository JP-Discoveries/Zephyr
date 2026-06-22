using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Zephyr.Core.Archives;

namespace Zephyr.UI.Services;

public static class ShellIconService
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes,
        ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("shell32.dll")]
    private static extern int SHGetImageList(int iImageList, ref Guid riid, out IntPtr ppv);

    [DllImport("comctl32.dll")]
    private static extern IntPtr ImageList_GetIcon(IntPtr himl, int i, uint flags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private const uint SHGFI_ICON             = 0x000000100;
    private const uint SHGFI_SMALLICON        = 0x000000001;
    private const uint SHGFI_SYSICONINDEX     = 0x000004000;
    private const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;
    private const uint FILE_ATTRIBUTE_NORMAL  = 0x00000080;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;
    private const int  SHIL_JUMBO             = 4;

    private static readonly Guid IImageListGuid = new("46EB5926-582E-4017-9FDF-E8998DAA0950");
    private static IntPtr _jumboImageList;
    private static readonly object _imgListLock = new();

    private static readonly ConcurrentDictionary<string, ImageSource?> _smallCache = new();
    private static readonly ConcurrentDictionary<string, ImageSource?> _jumboCache = new();

    public static ImageSource? GetSmallIcon(string path, bool isDirectory, string? extOverride = null)
        => _smallCache.GetOrAdd(CacheKey(path, isDirectory, extOverride), _ => LoadSmall(path, isDirectory, extOverride));

    public static ImageSource? GetLargeIcon(string path, bool isDirectory, string? extOverride = null)
        => _jumboCache.GetOrAdd(CacheKey(path, isDirectory, extOverride), _ => LoadJumbo(path, isDirectory, extOverride));

    // WPD paths end in an object ID with no extension, so callers pass the real extension.
    private static string ResolveExt(string path, string? extOverride) =>
        !string.IsNullOrEmpty(extOverride) ? extOverride.ToLowerInvariant() : Path.GetExtension(path).ToLowerInvariant();

    // Paths that don't exist on disk (phone/WPD objects, archive entries) — resolve icons
    // by file attributes/extension instead of querying the real path.
    private static bool IsSynthetic(string path) =>
        WpdProvider.IsWpdPath(path) || ArchivePath.IsArchivePath(path);

    private static string CacheKey(string path, bool isDirectory, string? extOverride)
    {
        if (string.IsNullOrEmpty(path)) return string.Empty;
        if (isDirectory) return "DIR:" + (IsSynthetic(path) ? "GENERIC" : path.ToUpperInvariant());
        string ext = ResolveExt(path, extOverride);
        // Per-file embedded icons for real executables and icon files
        return !IsSynthetic(path) && ext is ".exe" or ".dll" or ".ico"
            ? path.ToUpperInvariant()
            : ext.Length > 0 ? ext : "NOEXT";
    }

    private static ImageSource? LoadSmall(string path, bool isDirectory, string? extOverride)
    {
        try
        {
            if (string.IsNullOrEmpty(path)) return null;
            var shfi = new SHFILEINFO();
            uint flags = SHGFI_ICON | SHGFI_SMALLICON;
            string queryPath;
            uint fileAttr;

            if (isDirectory)
            {
                if (IsSynthetic(path))
                {
                    // Phone/archive folders don't exist on disk — synthesize a generic folder icon.
                    queryPath = "folder";
                    fileAttr  = FILE_ATTRIBUTE_DIRECTORY;
                    flags    |= SHGFI_USEFILEATTRIBUTES;
                }
                else
                {
                    queryPath = path.EndsWith('\\') ? path : path + "\\";
                    fileAttr  = FILE_ATTRIBUTE_DIRECTORY;
                }
            }
            else
            {
                string ext = ResolveExt(path, extOverride);
                if (!IsSynthetic(path) && ext is ".exe" or ".dll" or ".ico")
                {
                    queryPath = path;
                    fileAttr = FILE_ATTRIBUTE_NORMAL;
                }
                else
                {
                    queryPath = "x" + ext;
                    fileAttr = FILE_ATTRIBUTE_NORMAL;
                    flags |= SHGFI_USEFILEATTRIBUTES;
                }
            }

            IntPtr r = SHGetFileInfo(queryPath, fileAttr, ref shfi, (uint)Marshal.SizeOf<SHFILEINFO>(), flags);
            if (r == IntPtr.Zero || shfi.hIcon == IntPtr.Zero) return null;

            try
            {
                var src = Imaging.CreateBitmapSourceFromHIcon(shfi.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return src;
            }
            finally { DestroyIcon(shfi.hIcon); }
        }
        catch { return null; }
    }

    // Returns a 256×256 jumbo icon from the system image list — crisp at any thumbnail size.
    private static ImageSource? LoadJumbo(string path, bool isDirectory, string? extOverride)
    {
        try
        {
            if (string.IsNullOrEmpty(path)) return null;

            var shfi = new SHFILEINFO();
            uint sysFlags = SHGFI_SYSICONINDEX;
            string queryPath;
            uint fileAttr;
            if (IsSynthetic(path))
            {
                // Phone/archive paths don't exist on disk — query a synthetic name by attributes.
                queryPath = isDirectory ? "folder" : "x" + ResolveExt(path, extOverride);
                fileAttr  = isDirectory ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL;
                sysFlags |= SHGFI_USEFILEATTRIBUTES;
            }
            else
            {
                queryPath = isDirectory ? (path.EndsWith('\\') ? path : path + "\\") : path;
                fileAttr  = isDirectory ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL;
            }

            IntPtr r = SHGetFileInfo(queryPath, fileAttr, ref shfi, (uint)Marshal.SizeOf<SHFILEINFO>(), sysFlags);
            if (r == IntPtr.Zero) return null;

            IntPtr imgList = EnsureJumboImageList();
            if (imgList == IntPtr.Zero) return null;

            IntPtr hIcon = ImageList_GetIcon(imgList, shfi.iIcon, 0);
            if (hIcon == IntPtr.Zero) return null;

            try
            {
                var src = Imaging.CreateBitmapSourceFromHIcon(hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return src;
            }
            finally { DestroyIcon(hIcon); }
        }
        catch { return null; }
    }

    private static IntPtr EnsureJumboImageList()
    {
        if (_jumboImageList != IntPtr.Zero) return _jumboImageList;
        lock (_imgListLock)
        {
            if (_jumboImageList != IntPtr.Zero) return _jumboImageList;
            var guid = IImageListGuid;
            SHGetImageList(SHIL_JUMBO, ref guid, out _jumboImageList);
            return _jumboImageList;
        }
    }
}
