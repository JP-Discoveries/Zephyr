using System.Windows.Media;

namespace Zephyr.UI.Services;

/// <summary>
/// Assigns a stable colour to a file extension for the disk-usage treemap. Common
/// extensions get hand-picked colours; anything else gets a deterministic pleasant
/// colour derived from the extension text, so the same type is always the same colour.
/// </summary>
public static class FileTypeColorService
{
    // Files with no extension get their own strong colour (WizTree uses orange here) — never grey.
    private static readonly Color NoExtension = Color.FromRgb(0xF7, 0x90, 0x2A);

    // Hand-picked vivid colours. Executable kinds are split so .exe/.dll/.sys never collide,
    // and nothing is grey (grey reads as "uncategorised" and is hard to tell apart).
    private static readonly Dictionary<string, Color> Curated = Build(new()
    {
        [Color.FromRgb(0x3F, 0xD0, 0x6B)] = "jpg jpeg png gif bmp tif tiff webp heic heif svg ico raw cr2 nef dng psd",   // green   – images
        [Color.FromRgb(0xF0, 0x46, 0x3C)] = "mp4 mkv avi mov wmv flv webm m4v mpg mpeg 3gp",                              // red     – video
        [Color.FromRgb(0xB0, 0x6F, 0xE8)] = "mp3 wav flac aac ogg wma m4a opus aiff",                                     // purple  – audio
        [Color.FromRgb(0x3A, 0x9B, 0xF5)] = "pdf doc docx xls xlsx ppt pptx txt rtf odt ods odp md csv tsv",             // blue    – documents
        [Color.FromRgb(0x1F, 0xD4, 0xD0)] = "cs js ts jsx tsx py java cpp cc c h hpp html htm css scss json xml yaml yml go rs rb php sql sh ps1 lua kt swift", // cyan – code
        [Color.FromRgb(0xF2, 0xC0, 0x18)] = "dll lib so dylib o a",                                                       // yellow  – libraries
        [Color.FromRgb(0xE8, 0x44, 0xA8)] = "exe msi appx bat cmd com scr",                                              // magenta – executables
        [Color.FromRgb(0x5B, 0x9B, 0xD5)] = "sys drv vxd",                                                                // steel   – system
        [Color.FromRgb(0xE8, 0x8B, 0x2A)] = "zip rar 7z tar gz bz2 xz iso cab tgz vhd vhdx",                              // orange  – archives/images
        [Color.FromRgb(0xC8, 0x3A, 0x5B)] = "bin dat db sqlite safetensors gguf body uasset pak",                        // crimson – data blobs
    });

    private static readonly Dictionary<string, Brush> _brushCache = [];

    public static Color GetColor(string extension)
    {
        var ext = Normalize(extension);
        if (ext.Length == 0) return NoExtension;
        if (Curated.TryGetValue(ext, out var c)) return c;
        return FromHash(ext);
    }

    public static Brush GetBrush(string extension)
    {
        var ext = Normalize(extension);
        if (_brushCache.TryGetValue(ext, out var cached)) return cached;
        var brush = new SolidColorBrush(GetColor(ext));
        brush.Freeze();
        _brushCache[ext] = brush;
        return brush;
    }

    private static string Normalize(string ext) =>
        string.IsNullOrEmpty(ext) ? "" : ext.TrimStart('.').ToLowerInvariant();

    // Deterministic hue from the extension; fixed saturation/value keep colours readable on dark.
    private static Color FromHash(string ext)
    {
        uint hash = 2166136261;
        foreach (char ch in ext) { hash ^= ch; hash *= 16777619; }
        // Spread hues around the wheel and keep them vivid so types stay distinct.
        double hue = hash % 360;
        double sat = 0.70 + (hash >> 9) % 20 / 100.0;  // 0.70–0.89
        double val = 0.82 + (hash >> 17) % 14 / 100.0; // 0.82–0.95
        return FromHsv(hue, sat, val);
    }

    private static Color FromHsv(double h, double s, double v)
    {
        double c = v * s;
        double x = c * (1 - Math.Abs((h / 60) % 2 - 1));
        double m = v - c;
        (double r, double g, double b) = h switch
        {
            < 60  => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _     => (c, 0.0, x),
        };
        return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }

    private static Dictionary<string, Color> Build(Dictionary<Color, string> families)
    {
        var map = new Dictionary<string, Color>();
        foreach (var (color, exts) in families)
            foreach (var ext in exts.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                map[ext] = color;
        return map;
    }
}
