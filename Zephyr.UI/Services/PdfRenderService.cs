using System.IO;
using System.Windows.Media.Imaging;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Zephyr.UI.Services;

/// <summary>
/// Renders PDF pages to bitmaps using the built-in Windows runtime PDF engine
/// (Windows.Data.Pdf) — no third-party or native dependencies required.
/// </summary>
public static class PdfRenderService
{
    // Cap how many pages we render for a quick preview to keep it snappy.
    private const int    MaxPages       = 20;
    // Width to rasterise each page at; higher = crisper but slower / more memory.
    private const double RenderWidthPx  = 1100;

    /// <summary>
    /// Renders up to <see cref="MaxPages"/> pages of the PDF at <paramref name="path"/>
    /// into frozen <see cref="BitmapSource"/>s safe to hand to the UI thread.
    /// </summary>
    public static async Task<List<BitmapSource>> RenderPagesAsync(string path, CancellationToken ct)
    {
        var pages = new List<BitmapSource>();

        var file = await StorageFile.GetFileFromPathAsync(path);
        var doc  = await PdfDocument.LoadFromFileAsync(file);

        int count = Math.Min((int)doc.PageCount, MaxPages);
        for (int i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();

            using var page   = doc.GetPage((uint)i);
            using var stream = new InMemoryRandomAccessStream();

            var options = new PdfPageRenderOptions { DestinationWidth = (uint)RenderWidthPx };
            await page.RenderToStreamAsync(stream, options);

            pages.Add(ToBitmap(stream));
        }

        return pages;
    }

    private static BitmapSource ToBitmap(InMemoryRandomAccessStream stream)
    {
        // Copy the WinRT stream into a managed buffer via DataReader so we avoid
        // taking a dependency on the WinRT<->Stream interop extension assemblies.
        uint size = (uint)stream.Size;
        var  bytes = new byte[size];
        using (var reader = new DataReader(stream.GetInputStreamAt(0)))
        {
            reader.LoadAsync(size).AsTask().GetAwaiter().GetResult();
            reader.ReadBytes(bytes);
        }

        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption  = BitmapCacheOption.OnLoad;
        bmp.StreamSource = new MemoryStream(bytes);
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }
}
