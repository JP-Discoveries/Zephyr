using System.Windows.Media.Imaging;
using Zephyr.Core.Models;

namespace Zephyr.UI.Services;

public static class ThumbnailService
{
    private static readonly HashSet<string> _imageExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".jpe", ".jfif",
        ".png", ".bmp", ".gif",
        ".tiff", ".tif",
        ".webp", ".avif",
        ".ico",
        ".heic", ".heif",
        ".raw", ".cr2", ".cr3", ".nef", ".arw", ".dng",
        ".orf", ".rw2", ".pef", ".srw", ".x3f", ".raf",
    };

    // ── L1 in-memory LRU cache (500 entries ≈ 100 MB) ────────────────────
    private static readonly object _lock = new();
    private static readonly Dictionary<string, BitmapSource?> _mem   = new(StringComparer.Ordinal);
    private static readonly LinkedList<string>                _order = new();
    private static readonly Dictionary<string, LinkedListNode<string>> _idx = new(StringComparer.Ordinal);
    private const int MemMax = 500;

    public static bool IsImage(string extension) =>
        !string.IsNullOrEmpty(extension) && _imageExts.Contains(extension);

    public static async Task LoadBatchAsync(IEnumerable<FileItem> items, CancellationToken ct)
    {
        var targets = items.Where(i => IsImage(i.Extension)).ToList();
        if (targets.Count == 0) return;

        await Parallel.ForEachAsync(
            targets,
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
            async (item, token) =>
            {
                if (item.ThumbnailImage != null) return;

                var key    = ThumbnailCacheService.ComputeKey(item.FullPath, item.LastModified);
                var cached = GetMem(key);
                if (cached != null) { item.ThumbnailImage = cached; return; }

                var bitmap = await ThumbnailCacheService.GetOrCreateAsync(
                    item.FullPath, item.LastModified, token);

                if (bitmap != null)
                {
                    AddMem(key, bitmap);
                    item.ThumbnailImage = bitmap;
                }
            });
    }

    public static void ClearMemCache()
    {
        lock (_lock) { _mem.Clear(); _order.Clear(); _idx.Clear(); }
    }

    // ── LRU helpers ───────────────────────────────────────────────────────

    private static BitmapSource? GetMem(string key)
    {
        lock (_lock)
        {
            if (!_mem.TryGetValue(key, out var bmp)) return null;
            var node = _idx[key];
            _order.Remove(node);
            _order.AddFirst(node);
            return bmp;
        }
    }

    private static void AddMem(string key, BitmapSource bmp)
    {
        lock (_lock)
        {
            if (_mem.ContainsKey(key)) return;
            while (_order.Count >= MemMax)
            {
                var lruKey = _order.Last!.Value;
                _order.RemoveLast();
                _mem.Remove(lruKey);
                _idx.Remove(lruKey);
            }
            _mem[key] = bmp;
            var n = _order.AddFirst(key);
            _idx[key] = n;
        }
    }
}
