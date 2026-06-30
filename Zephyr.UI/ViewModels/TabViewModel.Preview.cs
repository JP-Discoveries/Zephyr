using System.IO;
using System.Text;
using Zephyr.Core.Archives;
using Zephyr.Core.Models;
using Zephyr.UI.Services;

namespace Zephyr.UI.ViewModels;

// Preview-pane content: decides the preview type for the selected item and loads its
// text/image, including entries inside archives (extracted to a temp copy) and WPD files.
public partial class TabViewModel
{
    // Entries above this size aren't previewed inside archives (would need full extraction).
    private const long ArchivePreviewSizeCap = 50L * 1024 * 1024;

    partial void OnShowPreviewPaneChanged(bool value)
    {
        if (value && SelectedItem != null) TriggerPreview(SelectedItem);
        else { PreviewType = PreviewType.None; PreviewText = string.Empty; }
    }

    partial void OnSelectedItemChanged(FileItem? value)
    {
        if (!ShowPreviewPane || value == null)
        {
            PreviewType = PreviewType.None;
            return;
        }
        TriggerPreview(value);
    }

    private void TriggerPreview(FileItem item)
    {
        if (item.IsDirectory) { PreviewType = PreviewType.Info; return; }
        PreviewType = PreviewService.GetType(item.Extension);

        if (IsArchive(item.FullPath))
        {
            // Files inside an archive are previewed by extracting a temp copy first.
            if (PreviewType is PreviewType.Text or PreviewType.Document or PreviewType.Image)
            {
                if (item.Size > ArchivePreviewSizeCap)
                {
                    PreviewType = PreviewType.Info;
                    PreviewText = string.Empty;
                }
                else _ = LoadArchivePreviewAsync(item);
            }
            else PreviewText = string.Empty;
            return;
        }

        if (PreviewType is PreviewType.Text or PreviewType.Document)
        {
            if (IsWpd(item.FullPath)) { PreviewType = PreviewType.None; PreviewText = string.Empty; }
            else _ = LoadPreviewTextAsync(item.FullPath);
        }
        else if (PreviewType == PreviewType.Image)
        {
            PreviewText = string.Empty;
            if (IsWpd(item.FullPath)) _ = LoadWpdImagePreviewAsync(item);
            else PreviewImagePath = item.FullPath;
        }
        else PreviewText = string.Empty;
    }

    private async Task LoadArchivePreviewAsync(FileItem item)
    {
        PreviewText      = string.Empty;
        PreviewImagePath = string.Empty;
        IsLoadingPreview = true;
        var wantImage    = PreviewType == PreviewType.Image;
        var wantDocument = PreviewType == PreviewType.Document;
        try
        {
            var (archiveFile, inner) = ArchivePath.Parse(item.FullPath);
            var pw   = _archiveProvider.GetCachedPassword(archiveFile);
            var temp = await Task.Run(() => ZephyrArchiveService.ExtractEntryToTemp(archiveFile, inner, pw));

            if (SelectedItem != item) return; // selection moved on while extracting

            if (wantImage)
            {
                PreviewImagePath = temp;
            }
            else
            {
                PreviewText = await Task.Run(() =>
                {
                    if (wantDocument) return DocumentTextExtractor.Extract(temp);
                    var sb = new StringBuilder();
                    using var reader = new StreamReader(temp, detectEncodingFromByteOrderMarks: true);
                    for (int i = 0; i < 500 && !reader.EndOfStream; i++)
                        sb.AppendLine(reader.ReadLine());
                    return sb.ToString();
                });
            }
        }
        catch { PreviewText = "[Cannot preview this entry]"; }
        finally { IsLoadingPreview = false; }
    }

    private async Task LoadWpdImagePreviewAsync(FileItem item)
    {
        PreviewImagePath = string.Empty;
        IsLoadingPreview = true;
        try
        {
            var (deviceId, objectId) = WpdProvider.ParsePath(item.FullPath);
            var temp = await Task.Run(() => WpdProvider.CopyToTempFile(deviceId, objectId, item.Name));
            // Ignore if the selection changed while we were copying
            if (SelectedItem == item && !string.IsNullOrEmpty(temp))
                PreviewImagePath = temp!;
        }
        catch { }
        finally { IsLoadingPreview = false; }
    }

    private async Task LoadPreviewTextAsync(string path)
    {
        IsLoadingPreview = true;
        var isDocument = PreviewType == PreviewType.Document;
        try
        {
            PreviewText = await Task.Run(() =>
            {
                if (isDocument)
                    return DocumentTextExtractor.Extract(path);
                var sb = new StringBuilder();
                using var reader = new StreamReader(path, detectEncodingFromByteOrderMarks: true);
                for (int i = 0; i < 500 && !reader.EndOfStream; i++)
                    sb.AppendLine(reader.ReadLine());
                return sb.ToString();
            });
        }
        catch { PreviewText = "[Cannot read file content]"; }
        finally { IsLoadingPreview = false; }
    }
}
