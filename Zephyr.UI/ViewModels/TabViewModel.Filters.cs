using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using Zephyr.Core.Models;
using Zephyr.Core.Search;
using Zephyr.Core.Settings;

namespace Zephyr.UI.ViewModels;

// In-folder filtering and sorting: the dynamic per-folder type filter, size/date filters,
// and the always-directories-first sort applied to the displayed item list.
public partial class TabViewModel
{
    // ── Dynamic type filter options (rebuilt per-folder) ──────────────────────
    public sealed class TypeFilterItem(string label, Func<FileItem, bool> matches, string[]? extensions = null)
    {
        public string Label { get; } = label;
        public Func<FileItem, bool> Matches { get; } = matches;
        public string[]? Extensions { get; } = extensions;
        public override string ToString() => Label;
    }

    public static readonly IReadOnlyList<KeyValuePair<string, SizeFilter>> SizeFilterOptions =
    [
        new("Any size",   SizeFilter.All),
        new("< 100 KB",  SizeFilter.Tiny),
        new("< 1 MB",    SizeFilter.Small),
        new("< 100 MB",  SizeFilter.Medium),
        new("< 1 GB",    SizeFilter.Large),
        new("> 1 GB",    SizeFilter.Huge),
    ];

    public static readonly IReadOnlyList<KeyValuePair<string, DateFilter>> DateFilterOptions =
    [
        new("Any date",    DateFilter.All),
        new("Today",       DateFilter.Today),
        new("Yesterday",   DateFilter.Yesterday),
        new("This week",   DateFilter.ThisWeek),
        new("This month",  DateFilter.ThisMonth),
        new("This year",   DateFilter.ThisYear),
    ];

    [RelayCommand]
    private void ClearFilters()
    {
        _suppressFilters = true;
        SelectedTypeFilter = DynamicTypeFilterOptions.FirstOrDefault();
        ActiveSizeFilter = SizeFilter.All;
        ActiveDateFilter = DateFilter.All;
        _suppressFilters = false;
        ApplyFilters();
    }

    // ── Filter reactions ──────────────────────────────────────────────────────
    partial void OnSelectedTypeFilterChanged(TypeFilterItem? value) { if (!_suppressFilters) ApplyFilters(); }
    partial void OnActiveSizeFilterChanged(SizeFilter      value) => ApplyFilters();
    partial void OnActiveDateFilterChanged(DateFilter      value) => ApplyFilters();
    partial void OnActiveSortColumnChanged(SortColumn      value) => ApplyFilters();
    partial void OnSortAscendingChanged(bool               value) => ApplyFilters();

    private void RebuildTypeFilterOptions()
    {
        var allItem = new TypeFilterItem("All types", _ => true);
        var options = new List<TypeFilterItem> { allItem };

        if (_allItems.Any(i => i.IsDirectory))
            options.Add(new TypeFilterItem("Folders", i => i.IsDirectory));

        // Group by canonical label so .jpg and .jpeg share one "JPEG" entry
        var labelToExtensions = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var item in _allItems.Where(i => !i.IsDirectory && !string.IsNullOrEmpty(i.Extension)))
        {
            var ext   = item.Extension.ToLowerInvariant();
            var label = FileTypeLabels.Map.TryGetValue(ext, out var l) ? l : ext.TrimStart('.').ToUpperInvariant();
            if (!labelToExtensions.TryGetValue(label, out var list))
                labelToExtensions[label] = list = [];
            if (!list.Contains(ext))
                list.Add(ext);
        }

        foreach (var (label, exts) in labelToExtensions.OrderBy(kv => kv.Key))
        {
            var capturedExts = exts.ToArray();
            options.Add(new TypeFilterItem(label, i => !i.IsDirectory && capturedExts.Contains(i.Extension), capturedExts));
        }

        _suppressFilters = true;
        DynamicTypeFilterOptions = new ObservableCollection<TypeFilterItem>(options);
        SelectedTypeFilter = allItem;
        _suppressFilters = false;
    }

    private void ApplyFilters()
    {
        if (IsSearchMode) return;

        IEnumerable<FileItem> filtered = _allItems;

        if (!string.IsNullOrEmpty(SearchQuery))
            filtered = filtered.Where(i => i.Name.Contains(SearchQuery, StringComparison.OrdinalIgnoreCase));

        if (SelectedTypeFilter != null && SelectedTypeFilter.Label != "All types")
            filtered = filtered.Where(SelectedTypeFilter.Matches);

        if (ActiveSizeFilter != SizeFilter.All)
            filtered = filtered.Where(i => !i.IsDirectory && MatchesSizeFilter(i.Size, ActiveSizeFilter));

        if (ActiveDateFilter != DateFilter.All)
            filtered = filtered.Where(i => MatchesDateFilter(i.LastModified, ActiveDateFilter));

        // Sort (always keep directories first)
        filtered = (ActiveSortColumn, SortAscending) switch
        {
            (SortColumn.Size,         true)  => filtered.OrderBy(i => i.IsDirectory ? 0 : 1).ThenBy(i => i.Size),
            (SortColumn.Size,         false) => filtered.OrderBy(i => i.IsDirectory ? 0 : 1).ThenByDescending(i => i.Size),
            (SortColumn.DateModified, true)  => filtered.OrderBy(i => i.IsDirectory ? 0 : 1).ThenBy(i => i.LastModified),
            (SortColumn.DateModified, false) => filtered.OrderBy(i => i.IsDirectory ? 0 : 1).ThenByDescending(i => i.LastModified),
            (SortColumn.Type,         true)  => filtered.OrderBy(i => i.IsDirectory ? 0 : 1).ThenBy(i => i.TypeDisplay, StringComparer.OrdinalIgnoreCase),
            (SortColumn.Type,         false) => filtered.OrderBy(i => i.IsDirectory ? 0 : 1).ThenByDescending(i => i.TypeDisplay, StringComparer.OrdinalIgnoreCase),
            (SortColumn.DateCreated,  true)  => filtered.OrderBy(i => i.IsDirectory ? 0 : 1).ThenBy(i => i.Created),
            (SortColumn.DateCreated,  false) => filtered.OrderBy(i => i.IsDirectory ? 0 : 1).ThenByDescending(i => i.Created),
            (_,                       true)  => filtered.OrderBy(i => i.IsDirectory ? 0 : 1).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase),
            (_,                       false) => filtered.OrderBy(i => i.IsDirectory ? 0 : 1).ThenByDescending(i => i.Name, StringComparer.OrdinalIgnoreCase),
        };

        // Recently-interacted items bubble to the top when that sort is enabled
        if (SettingsService.Current.SortByRecentlyInteracted)
        {
            var list    = filtered.ToList();
            var recents = list.Where(i => i.IsRecentlyInteracted)
                              .OrderByDescending(i => i.RecentInteractionTime ?? DateTime.MinValue);
            var others  = list.Where(i => !i.IsRecentlyInteracted);
            filtered    = recents.Concat(others);
        }

        Items.Reset(filtered);

        OnPropertyChanged(nameof(ItemCountText));
        OnPropertyChanged(nameof(ItemSummaryText));
        OnPropertyChanged(nameof(SelectionText));
    }

    private static bool MatchesSizeFilter(long size, SizeFilter f) => f switch
    {
        SizeFilter.Tiny   => size < 100 * 1024,
        SizeFilter.Small  => size < 1024 * 1024,
        SizeFilter.Medium => size < 100L * 1024 * 1024,
        SizeFilter.Large  => size < 1024L * 1024 * 1024,
        SizeFilter.Huge   => size >= 1024L * 1024 * 1024,
        _                 => true
    };

    private static bool MatchesDateFilter(DateTime dt, DateFilter f)
    {
        var now = DateTime.Now;
        return f switch
        {
            DateFilter.Today     => dt.Date == now.Date,
            DateFilter.Yesterday => dt.Date == now.Date.AddDays(-1),
            DateFilter.ThisWeek  => dt >= now.AddDays(-7),
            DateFilter.ThisMonth => dt.Month == now.Month && dt.Year == now.Year,
            DateFilter.ThisYear  => dt.Year == now.Year,
            _                   => true
        };
    }

    private static string FormatContentSummary(int folders, int files)
    {
        if (folders < 0) return "";
        var parts = new List<string>(2);
        if (folders > 0) parts.Add($"{folders} {(folders == 1 ? "folder" : "folders")}");
        if (files   > 0) parts.Add($"{files} {(files == 1 ? "file" : "files")}");
        return parts.Count > 0 ? string.Join(", ", parts) : "empty";
    }
}
