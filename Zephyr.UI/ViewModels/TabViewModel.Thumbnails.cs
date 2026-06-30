using Zephyr.Core.Models;
using Zephyr.UI.Services;

namespace Zephyr.UI.ViewModels;

// Image-thumbnail prefetch for the icon view: loads the visible viewport first, then the
// rest of the folder, and re-prefetches when the pane width (and thus the visible count)
// changes.
public partial class TabViewModel
{
    private CancellationTokenSource? _thumbCts;
    private int    _lastThumbnailSize = 50;
    private double _paneWidth         = 800;

    public void SetPaneWidth(double width)
    {
        if (Math.Abs(_paneWidth - width) < 1) return;
        _paneWidth = width;
        if (ThumbnailSize > 0 && _allItems.Count > 0 && !IsSearchMode)
            _ = BeginThumbnailLoadAsync();
    }

    private int PrefetchCount =>
        ThumbnailSize == 0 ? 0 : Math.Max(20, (int)(_paneWidth / ThumbnailContainerSize) * 3);

    private async Task BeginThumbnailLoadAsync(List<FileItem>? items = null)
    {
        _thumbCts?.Cancel();
        _thumbCts = new CancellationTokenSource();
        var ct = _thumbCts.Token;

        var all = (items ?? _allItems)
            .Where(i => ThumbnailService.IsImage(i.Extension) && i.ThumbnailImage == null)
            .ToList();

        if (all.Count == 0) return;

        try
        {
            // Phase 1: visible viewport + rows ahead — appears immediately
            await ThumbnailService.LoadBatchAsync(all.Take(PrefetchCount), ct);
            // Phase 2: remainder of the folder in the same background batch
            await ThumbnailService.LoadBatchAsync(all.Skip(PrefetchCount), ct);
        }
        catch (OperationCanceledException) { }
    }

    partial void OnThumbnailSizeChanged(int value)
    {
        if (value > 0) _lastThumbnailSize = value;
        if (value > 0 && _allItems.Count > 0 && !IsSearchMode)
            _ = BeginThumbnailLoadAsync();
    }
}
