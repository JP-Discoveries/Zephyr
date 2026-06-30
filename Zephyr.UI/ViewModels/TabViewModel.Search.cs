using System.Windows;
using CommunityToolkit.Mvvm.Input;
using Zephyr.Core.Models;
using Zephyr.Core.Search;
using Zephyr.Core.Settings;
using Zephyr.UI.Services;

namespace Zephyr.UI.ViewModels;

// Deep (recursive) search: debounced query handling, streaming results into the list,
// and the scope/content toggles.
public partial class TabViewModel
{
    private readonly SearchEngine _searchEngine = new();
    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _debounceCts;

    [RelayCommand]
    public async Task StartDeepSearchAsync()
    {
        if (string.IsNullOrEmpty(SearchQuery)) return;
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var ct = _searchCts.Token;

        FlatView     = false;
        IsSearchMode = true;
        IsSearching  = true;
        Items.Clear();

        var typeFilter = FileTypeFilter.All;
        string[]? customExtensions = null;
        if (SelectedTypeFilter?.Label == "Folders")
            typeFilter = FileTypeFilter.Folders;
        else if (SelectedTypeFilter?.Extensions != null)
            customExtensions = SelectedTypeFilter.Extensions;

        var options = new SearchOptions
        {
            Query            = SearchQuery,
            SearchRoot       = CurrentPath,
            Scope            = SearchRecursive ? SearchScope.Recursive : SearchScope.CurrentDirectory,
            TypeFilter       = typeFilter,
            CustomExtensions = customExtensions,
            SizeFilter       = ActiveSizeFilter,
            DateFilter       = ActiveDateFilter,
            MatchContent     = MatchContent,
            IncludeHidden    = SettingsService.Current.ShowHiddenFiles,
            IncludeSystem    = SettingsService.Current.ShowSystemFiles,
        };

        try
        {
            await Task.Run(async () =>
            {
                var batch = new List<FileItem>(50);
                await foreach (var item in _searchEngine.SearchAsync(options, ct))
                {
                    batch.Add(item);
                    if (batch.Count < 50) continue;
                    var flush = batch.ToList();
                    batch.Clear();
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        foreach (var it in flush) it.LabelColor = FileLabelService.GetHex(it.FullPath);
                        Items.Append(flush);
                        OnPropertyChanged(nameof(ItemCountText));
                    });
                }
                if (batch.Count > 0)
                {
                    var flush = batch;
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        foreach (var it in flush) it.LabelColor = FileLabelService.GetHex(it.FullPath);
                        Items.Append(flush);
                        OnPropertyChanged(nameof(ItemCountText));
                    });
                }
            }, ct);
        }
        catch (OperationCanceledException) { }
        finally
        {
            IsSearching = false;
            OnPropertyChanged(nameof(ItemCountText));
            OnPropertyChanged(nameof(SelectionText));
        }
    }

    [RelayCommand]
    private void ToggleSearchScope() => SearchRecursive = !SearchRecursive;

    [RelayCommand]
    private void ToggleContentSearch() => MatchContent = !MatchContent;

    [RelayCommand]
    public void ClearSearch()
    {
        _debounceCts?.Cancel();
        _searchCts?.Cancel();
        IsSearchMode     = false;
        IsSearching      = false;
        _suppressFilters = true;
        SearchQuery      = string.Empty;
        _suppressFilters = false;
        _ = LoadDirectoryAsync(CurrentPath);
    }

    // ── Search reactions ──────────────────────────────────────────────────────
    partial void OnSearchQueryChanged(string value)
    {
        if (_suppressFilters) return;

        // Cancel both the pending debounce AND any in-flight search immediately, so a
        // stale (now broader) query stops consuming CPU/IO the moment the text changes —
        // otherwise it keeps scanning for the whole debounce window while backspacing.
        _debounceCts?.Cancel();
        _searchCts?.Cancel();
        _debounceCts = new CancellationTokenSource();
        var cts = _debounceCts;

        if (string.IsNullOrEmpty(value))
        {
            if (IsSearchMode) ClearSearch();
            return;
        }

        // Content search reads every file, so debounce it longer to absorb fast edits.
        int delay = MatchContent ? 500 : 300;
        _ = Task.Delay(delay, cts.Token).ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully)
                Application.Current.Dispatcher.Invoke(() => _ = StartDeepSearchAsync());
        });
    }

    partial void OnMatchContentChanged(bool value)
    {
        if (IsSearchMode && !string.IsNullOrEmpty(SearchQuery)) _ = StartDeepSearchAsync();
    }
}
