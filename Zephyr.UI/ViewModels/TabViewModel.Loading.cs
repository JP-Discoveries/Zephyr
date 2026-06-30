using System.IO;
using System.Windows;
using Zephyr.Core.Models;
using Zephyr.Core.Security;
using Zephyr.Core.Settings;
using Zephyr.UI.Dialogs;
using Zephyr.UI.FileSystem;
using Zephyr.UI.Services;

namespace Zephyr.UI.ViewModels;

// Directory loading orchestration: pick the matching provider, then apply the shared
// post-load work (filters, watcher, thumbnails) plus the local-folder-only enrichment
// (labels, lock badges, content counts, folder sizes, cloud badges, recent interactions).
public partial class TabViewModel
{
    private async Task LoadDirectoryAsync(string path)
    {
        _loadCts.Cancel();
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;
        try
        {
            CurrentPath = path;

            var provider = _providers.First(p => p.CanHandle(path));
            var ctx      = new DirectoryLoadContext(SettingsService.Current, FlatView);
            var listing  = await provider.LoadAsync(path, ctx, ct);
            if (ct.IsCancellationRequested) return;

            // A null item list means the load was aborted (e.g. a password prompt was
            // cancelled); follow the provider's redirect, or stay put.
            if (listing.Items is null)
            {
                if (listing.RedirectPath is { } redirect) Navigate(redirect);
                return;
            }

            var items = listing.Items;

            // Local folders get the full enrichment treatment; virtual locations
            // (This PC, archives, devices) skip it.
            if (listing.IsLocalFolder)
            {
                foreach (var it in items)
                {
                    it.LabelColor = FileLabelService.GetHex(it.FullPath);
                    if (it.IsDirectory)
                    {
                        it.IsLocked   = FolderLockService.IsLockRoot(it.FullPath);
                        it.IsUnlocked = it.IsLocked && FolderLockService.IsUnlocked(it.FullPath);
                    }
                }
            }

            _allItems = items;
            RebuildTypeFilterOptions();
            ApplyFilters();
            if (listing.IsLocalFolder) ClipboardHighlightService.Apply(items);
            OnPropertyChanged(nameof(FreeSpaceText));
            SetupWatcher(listing.WatchPath);

            if (listing.IsLocalFolder)
            {
                var s = SettingsService.Current;
                if (!FlatView) _ = PopulateContentCountsAsync(items, ct);
                if (s.ShowFolderSizes) _ = PopulateFolderSizesAsync(items, ct);
                if (s.ShowCloudBadges) _ = PopulateCloudBadgesAsync(items, ct);
                if (s.ShowRecentlyInteracted || s.SortByRecentlyInteracted)
                    _ = PopulateRecentInteractionsAsync(items, ct);
            }

            if (listing.LoadsThumbnails && ThumbnailSize > 0) _ = BeginThumbnailLoadAsync(items);
        }
        catch (OperationCanceledException) { }
        catch when (!ct.IsCancellationRequested)
        {
            Application.Current?.Dispatcher.BeginInvoke(HandlePathLost);
        }
    }

    private void HandlePathLost()
    {
        _watcher?.Dispose();
        _watcher = null;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        ZephyrMessageBox.Show($"\"{CurrentPath}\" is no longer accessible.", "Folder Unavailable");
        Navigate(home);
    }

    private async Task PopulateRecentInteractionsAsync(List<FileItem> items, CancellationToken ct)
    {
        await Task.Run(() =>
        {
            foreach (var item in items)
            {
                if (ct.IsCancellationRequested) return;
                var time = RecentInteractionService.GetTime(item.FullPath);
                if (time.HasValue)
                {
                    item.IsRecentlyInteracted  = true;
                    item.RecentInteractionTime = time;
                }
            }
        }, ct).ConfigureAwait(false);
    }

    private async Task PopulateContentCountsAsync(List<FileItem> items, CancellationToken ct)
    {
        await Parallel.ForEachAsync(
            items.Where(i => i.IsDirectory),
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
            (dir, _) =>
            {
                try
                {
                    int folders = 0, files = 0;
                    foreach (var e in new DirectoryInfo(dir.FullPath).EnumerateFileSystemInfos())
                    {
                        if (e is DirectoryInfo) folders++; else files++;
                    }
                    dir.ContentSummary = FormatContentSummary(folders, files);
                }
                catch { }
                return ValueTask.CompletedTask;
            });
    }

    private async Task PopulateFolderSizesAsync(List<FileItem> items, CancellationToken ct)
    {
        await Parallel.ForEachAsync(
            items.Where(i => i.IsDirectory),
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
            (dir, _) =>
            {
                try
                {
                    long total = 0;
                    foreach (var f in new DirectoryInfo(dir.FullPath)
                                 .EnumerateFiles("*", SearchOption.AllDirectories))
                        total += f.Length;
                    dir.FolderSize = total;
                }
                catch { }
                return ValueTask.CompletedTask;
            });
    }

    private async Task PopulateCloudBadgesAsync(List<FileItem> items, CancellationToken ct)
    {
        var roots = CloudSyncService.SyncRoots;
        if (roots.Count == 0) return;
        await Task.Run(() =>
        {
            foreach (var item in items)
            {
                if (ct.IsCancellationRequested) return;
                var badge = CloudSyncService.GetBadge(item.FullPath, roots);
                if (!string.IsNullOrEmpty(badge)) item.CloudBadge = badge;
            }
        }, ct).ConfigureAwait(false);
    }
}
