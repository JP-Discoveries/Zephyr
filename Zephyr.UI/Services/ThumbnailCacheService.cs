using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Zephyr.Core.Settings;

namespace Zephyr.UI.Services;

public static class ThumbnailCacheService
{
    private const int DecodeWidth   = 256;
    private const int JpegQuality   = 85;
    private const int MinEmbedWidth = 128;

    private static string CacheDir =>
        SettingsService.IsPortableMode
            ? Path.Combine(AppContext.BaseDirectory, "thumbnails")
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Zephyr", "thumbnails");

    public static async Task<BitmapSource?> GetOrCreateAsync(
        string path, DateTime lastWriteTime, CancellationToken ct)
    {
        var key       = ComputeKey(path, lastWriteTime);
        var cachePath = ToDiskPath(key);

        if (File.Exists(cachePath))
        {
            var cached = await Task.Run(() => ReadFromDisk(cachePath), ct);
            if (cached != null) return cached;
        }

        ct.ThrowIfCancellationRequested();

        var bitmap = await Task.Run(() => Decode(path), ct);
        if (bitmap != null)
            _ = Task.Run(() => WriteToDisk(bitmap, cachePath), CancellationToken.None);

        return bitmap;
    }

    public static void ClearCache()
    {
        try { if (Directory.Exists(CacheDir)) Directory.Delete(CacheDir, recursive: true); }
        catch { }
    }

    public static long GetCacheBytes()
    {
        try
        {
            if (!Directory.Exists(CacheDir)) return 0;
            return new DirectoryInfo(CacheDir)
                .EnumerateFiles("*.jpg", SearchOption.AllDirectories)
                .Sum(f => f.Length);
        }
        catch { return 0; }
    }

    // key is shared with ThumbnailService so both caches stay in sync
    internal static string ComputeKey(string path, DateTime lwt)
    {
        var input = $"{path.ToUpperInvariant()}|{lwt.Ticks}";
        var hash  = MD5.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string ToDiskPath(string key) =>
        Path.Combine(CacheDir, key[..2], key + ".jpg");

    private static BitmapSource? ReadFromDisk(string path)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource     = new Uri(path);
            bmp.CacheOption   = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.None;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    private static BitmapSource? Decode(string filePath)
    {
        try
        {
            if (WpdProvider.IsWpdPath(filePath)) return DecodeWpd(filePath);

            var embedded = TryEmbeddedThumbnail(filePath);
            if (embedded != null) return embedded;

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource        = new Uri(filePath);
            bmp.CacheOption      = BitmapCacheOption.OnLoad;
            bmp.CreateOptions    = BitmapCreateOptions.None;
            bmp.DecodePixelWidth = DecodeWidth;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    private static BitmapSource? DecodeWpd(string filePath)
    {
        var (deviceId, objectId) = WpdProvider.ParsePath(filePath);
        var bytes = WpdProvider.ReadThumbnailBytes(deviceId, objectId);
        if (bytes == null || bytes.Length == 0) return null;
        try
        {
            using var ms = new MemoryStream(bytes);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.StreamSource     = ms;
            bmp.CacheOption      = BitmapCacheOption.OnLoad;   // fully decode before stream closes
            bmp.CreateOptions    = BitmapCreateOptions.None;
            bmp.DecodePixelWidth = DecodeWidth;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    private static BitmapSource? TryEmbeddedThumbnail(string filePath)
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            var decoder = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);

            var thumb = decoder.Thumbnail;
            if (thumb == null || thumb.PixelWidth < MinEmbedWidth) return null;

            BitmapSource src = thumb;
            if (src.PixelWidth != DecodeWidth)
            {
                var scale = DecodeWidth / (double)src.PixelWidth;
                src = new TransformedBitmap(src, new System.Windows.Media.ScaleTransform(scale, scale));
            }

            if (!src.IsFrozen) src.Freeze();
            return src;
        }
        catch { return null; }
    }

    private static void WriteToDisk(BitmapSource bitmap, string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var encoder = new JpegBitmapEncoder { QualityLevel = JpegQuality };
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var fs = File.Create(path);
            encoder.Save(fs);
        }
        catch { }
    }
}
