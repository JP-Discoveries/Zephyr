using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Zephyr.Core.Archives;
using Zephyr.Core.Collections;
using Zephyr.Core.FileSystem;
using Zephyr.Core.History;
using Zephyr.Core.Models;
using Zephyr.Core.Search;
using Zephyr.Core.Security;
using Zephyr.Core.Settings;
using Zephyr.UI.Dialogs;
using Zephyr.UI.Services;

namespace Zephyr.UI.ViewModels;

public partial class TabViewModel : ObservableObject
{
    private readonly FileSystemService     _fs;
    private readonly NavigationHistory     _history;
    private readonly FileOperationsService _fileOps;
    private readonly SearchEngine          _searchEngine = new();

    private readonly Stack<string> _backStack    = new();
    private readonly Stack<string> _forwardStack = new();
    private List<FileItem>         _allItems     = [];
    // Per-archive password cache (key present = auth handled; value null = not encrypted).
    private readonly Dictionary<string, string?> _archiveAuth = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource  _loadCts       = new();
    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _debounceCts;
    private CancellationTokenSource? _watcherDebounce;
    private CancellationTokenSource? _thumbCts;
    private FileSystemWatcher?       _watcher;
    private bool                     _suppressFilters;
    private int                      _lastThumbnailSize = 50;
    private double                   _paneWidth         = 800;

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

    // ── Path ──────────────────────────────────────────────────────────────────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Header))]
    [NotifyPropertyChangedFor(nameof(BreadcrumbSegments))]
    [NotifyPropertyChangedFor(nameof(CanGoUp))]
    [NotifyPropertyChangedFor(nameof(IsArchiveView))]
    private string _currentPath = string.Empty;

    // ── Items & selection ─────────────────────────────────────────────────────
    [ObservableProperty] private BulkObservableCollection<FileItem> _items = new();
    [ObservableProperty] private FileItem? _selectedItem;
    [ObservableProperty] private int _selectedCount;
    public List<FileItem> SelectedItems { get; private set; } = [];

    // ── Tab state ─────────────────────────────────────────────────────────────
    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private bool _isEditingPath;

    // ── Thumbnail size (0 = Details, 1-100 = thumbnails of increasing size) ─────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDetailsView))]
    [NotifyPropertyChangedFor(nameof(ShowIconView))]
    [NotifyPropertyChangedFor(nameof(ThumbnailPixelSize))]
    [NotifyPropertyChangedFor(nameof(ThumbnailNameHeight))]
    [NotifyPropertyChangedFor(nameof(ThumbnailContainerSize))]
    [NotifyPropertyChangedFor(nameof(ThumbnailIconFontSize))]
    private int _thumbnailSize = 0;

    public bool ShowDetailsView => !ShowListView && ThumbnailSize == 0;
    public bool ShowIconView    => !ShowListView && ThumbnailSize > 0;

    // Maps slider 1-100 → pixel sizes 48-512
    public int ThumbnailPixelSize     => ThumbnailSize == 0 ? 64 : Math.Max(48, 48 + (int)(ThumbnailSize * 4.64));
    // Name area scales with image size so text is never squeezed at large thumbnails
    public int ThumbnailNameHeight    => Math.Clamp(ThumbnailPixelSize / 5, 32, 60);
    public int ThumbnailContainerSize => ThumbnailPixelSize + ThumbnailNameHeight;
    public int ThumbnailIconFontSize  => Math.Max(18, ThumbnailPixelSize * 44 / 96);

    // ── Sort ──────────────────────────────────────────────────────────────────
    [ObservableProperty] private SortColumn _activeSortColumn = SortColumn.Name;
    [ObservableProperty] private bool       _sortAscending    = true;

    // ── Flat view (browse all subfolders without searching) ───────────────────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDetailsView))]
    [NotifyPropertyChangedFor(nameof(ShowIconView))]
    [NotifyPropertyChangedFor(nameof(ShowListView))]
    [NotifyPropertyChangedFor(nameof(ItemCountText))]
    [NotifyPropertyChangedFor(nameof(ItemSummaryText))]
    [NotifyPropertyChangedFor(nameof(FlatViewTooltip))]
    private bool _flatView = false;

    public bool ShowListView => IsSearchMode || FlatView;
    public string FlatViewTooltip => FlatView
        ? "Exit flat view"
        : "Flat view — show all files from subfolders";

    // ── Search ────────────────────────────────────────────────────────────────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDetailsView))]
    [NotifyPropertyChangedFor(nameof(ShowIconView))]
    [NotifyPropertyChangedFor(nameof(ShowListView))]
    private bool _isSearchMode;

    [ObservableProperty] private bool   _isSearching;
    [ObservableProperty] private string _searchQuery = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SearchScopeTooltip))]
    private bool _searchRecursive = true;

    public string SearchScopeTooltip => SearchRecursive
        ? "Scope: all subfolders — click for current folder only"
        : "Scope: current folder only — click for all subfolders";

    // ── Filters ───────────────────────────────────────────────────────────────
    [ObservableProperty] private bool           _showFilterBar;
    [ObservableProperty] private ObservableCollection<TypeFilterItem> _dynamicTypeFilterOptions = [];
    [ObservableProperty] private TypeFilterItem? _selectedTypeFilter;
    [ObservableProperty] private SizeFilter     _activeSizeFilter = SizeFilter.All;
    [ObservableProperty] private DateFilter     _activeDateFilter = DateFilter.All;

    // ── Preview pane ──────────────────────────────────────────────────────────
    [ObservableProperty] private bool        _showPreviewPane;
    [ObservableProperty] private PreviewType _previewType = PreviewType.None;
    [ObservableProperty] private string      _previewText  = string.Empty;
    [ObservableProperty] private bool        _isLoadingPreview;
    // Path the image preview binds to — real path for on-disk files, temp copy for WPD items
    [ObservableProperty] private string      _previewImagePath = string.Empty;

    // ── Computed ──────────────────────────────────────────────────────────────
    public const string ThisPcPath = "thispc:";
    private static bool IsThisPc(string path)    => path == ThisPcPath;
    private static bool IsDriveRoot(string path) => path.Length == 3 && path[1] == ':' && path[2] == '\\';
    private static bool IsWpd(string path)       => WpdProvider.IsWpdPath(path);
    private static bool IsArchive(string path)   => ArchivePath.IsArchivePath(path);

    /// <summary>True while the current location is inside an archive (read-only browsing).</summary>
    public bool IsArchiveView => IsArchive(CurrentPath);

    /// <summary>Cached password for the archive currently being browsed, if any.</summary>
    public string? CurrentArchivePassword =>
        IsArchiveView && _archiveAuth.TryGetValue(ArchivePath.Parse(CurrentPath).Archive, out var pw) ? pw : null;

    public bool CanGoBack    => _backStack.Count > 0;
    public bool CanGoForward => _forwardStack.Count > 0;
    public bool CanGoUp      => !string.IsNullOrEmpty(CurrentPath) && !IsThisPc(CurrentPath)
                                && (IsWpd(CurrentPath) || IsArchive(CurrentPath) || _fs.GetParent(CurrentPath) != null || IsDriveRoot(CurrentPath));

    // Back stack as a list (most recent first) for the history dropdown
    public IReadOnlyList<string> BackHistory => [.. _backStack];

    public string Header
    {
        get
        {
            if (IsThisPc(CurrentPath))             return "This PC";
            if (string.IsNullOrEmpty(CurrentPath)) return "New Tab";
            if (IsArchive(CurrentPath))
            {
                var (archiveFile, inner) = ArchivePath.Parse(CurrentPath);
                return inner.Length == 0
                    ? Path.GetFileName(archiveFile)
                    : inner.Split('/').Last();
            }
            if (IsWpd(CurrentPath))
            {
                var (deviceId, objectId) = WpdProvider.ParsePath(CurrentPath);
                if (objectId == WpdProvider.DeviceRootObjectId)
                    return WpdProvider.GetDevices().FirstOrDefault(d => d.DeviceId == deviceId).FriendlyName ?? "Device";
                return WpdProvider.GetCachedName(deviceId, objectId)
                    ?? (Path.GetFileName(objectId.TrimEnd('/')) is { Length: > 0 } n ? n : objectId);
            }
            var name = Path.GetFileName(CurrentPath.TrimEnd('\\', '/'));
            return string.IsNullOrEmpty(name) ? CurrentPath : name;
        }
    }

    public IReadOnlyList<BreadcrumbSegment> BreadcrumbSegments
    {
        get
        {
            if (string.IsNullOrEmpty(CurrentPath)) return [];
            var segments = new List<BreadcrumbSegment>();
            segments.Add(new BreadcrumbSegment("This PC", ThisPcPath));
            if (IsThisPc(CurrentPath)) return segments;

            if (IsArchive(CurrentPath))
            {
                var (archiveFile, inner) = ArchivePath.Parse(CurrentPath);
                var aroot = Path.GetPathRoot(archiveFile) ?? archiveFile;
                segments.Add(new BreadcrumbSegment(aroot.TrimEnd('\\'), aroot));
                var acc = aroot;
                var dir = Path.GetDirectoryName(archiveFile) ?? aroot;
                foreach (var part in dir[aroot.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
                {
                    acc = Path.Combine(acc, part);
                    segments.Add(new BreadcrumbSegment(part, acc));
                }
                // The archive file itself → its virtual root, then each internal folder.
                segments.Add(new BreadcrumbSegment(Path.GetFileName(archiveFile), ArchivePath.Make(archiveFile)));
                var innerAcc = "";
                foreach (var part in inner.Split('/', StringSplitOptions.RemoveEmptyEntries))
                {
                    innerAcc = innerAcc.Length == 0 ? part : innerAcc + "/" + part;
                    segments.Add(new BreadcrumbSegment(part, ArchivePath.Make(archiveFile, innerAcc)));
                }
                return segments;
            }

            if (IsWpd(CurrentPath))
            {
                var (deviceId, objectId) = WpdProvider.ParsePath(CurrentPath);
                var deviceName = WpdProvider.GetDevices()
                    .FirstOrDefault(d => d.DeviceId == deviceId).FriendlyName ?? "Device";
                segments.Add(new BreadcrumbSegment(deviceName, WpdProvider.MakeRootPath(deviceId)));
                return segments;
            }

            var root = Path.GetPathRoot(CurrentPath) ?? CurrentPath;
            segments.Add(new BreadcrumbSegment(root.TrimEnd('\\'), root));
            var accumulated = root;
            foreach (var part in CurrentPath[root.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
            {
                accumulated = Path.Combine(accumulated, part);
                segments.Add(new BreadcrumbSegment(part, accumulated));
            }
            return segments;
        }
    }

    public string ItemCountText => IsSearchMode
        ? $"{Items.Count} results"
        : FlatView
            ? $"{Items.Count} items (all subfolders)"
            : Items.Count == 1 ? "1 item" : $"{Items.Count} items";

    public string ItemSummaryText
    {
        get
        {
            if (IsSearchMode) return $"{Items.Count} result{(Items.Count != 1 ? "s" : "")}";
            var folders = Items.Count(i => i.IsDirectory);
            var files   = Items.Count(i => !i.IsDirectory);
            var parts   = new System.Collections.Generic.List<string>();
            if (folders > 0) parts.Add($"{folders} folder{(folders != 1 ? "s" : "")}");
            if (files   > 0) parts.Add($"{files} file{(files != 1 ? "s" : "")}");
            var summary = parts.Count > 0 ? string.Join(", ", parts) : "Empty folder";
            return FlatView ? $"{summary} (recursive)" : summary;
        }
    }

    public string SelectionText => SelectedCount > 0 ? $"  |  {SelectedCount} selected" : "";

    public string FreeSpaceText
    {
        get
        {
            try
            {
                if (IsThisPc(CurrentPath)) return string.Empty;
                if (IsWpd(CurrentPath))    return string.Empty;
                if (IsArchive(CurrentPath)) return string.Empty;
                var root = Path.GetPathRoot(CurrentPath);
                if (string.IsNullOrEmpty(root)) return string.Empty;
                var drive = new DriveInfo(root);
                return drive.IsReady ? $"Free: {FormatSize(drive.AvailableFreeSpace)}" : string.Empty;
            }
            catch { return string.Empty; }
        }
    }

    // ── Construction / teardown ───────────────────────────────────────────────
    public TabViewModel(FileSystemService fs, NavigationHistory history, FileOperationsService fileOps, string startPath)
    {
        _fs = fs; _history = history; _fileOps = fileOps;
        Navigate(startPath);
    }

    public void Cleanup()
    {
        _watcher?.Dispose();
        _watcher = null;
        _loadCts.Cancel();
        _searchCts?.Cancel();
        _debounceCts?.Cancel();
        _watcherDebounce?.Cancel();
        _thumbCts?.Cancel();
    }

    // ── Thumbnail loading ─────────────────────────────────────────────────
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

    // ── Navigation ────────────────────────────────────────────────────────────
    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void GoBack()
    {
        _forwardStack.Push(CurrentPath);
        _ = LoadDirectoryAsync(_backStack.Pop());
        RefreshNavState();
    }

    [RelayCommand(CanExecute = nameof(CanGoForward))]
    private void GoForward()
    {
        _backStack.Push(CurrentPath);
        _ = LoadDirectoryAsync(_forwardStack.Pop());
        RefreshNavState();
    }

    [RelayCommand(CanExecute = nameof(CanGoUp))]
    private void GoUp()
    {
        if (IsDriveRoot(CurrentPath)) { Navigate(ThisPcPath); return; }
        if (IsArchive(CurrentPath))
        {
            var (archiveFile, inner) = ArchivePath.Parse(CurrentPath);
            if (inner.Length == 0)
            {
                // At the archive root → step out to the real folder that contains the archive.
                var folder = Path.GetDirectoryName(archiveFile);
                if (folder != null) Navigate(folder);
            }
            else
            {
                var up = inner.Contains('/') ? inner[..inner.LastIndexOf('/')] : "";
                Navigate(ArchivePath.Make(archiveFile, up));
            }
            return;
        }
        if (IsWpd(CurrentPath))
        {
            var (deviceId, objectId) = WpdProvider.ParsePath(CurrentPath);
            if (objectId == WpdProvider.DeviceRootObjectId) { Navigate(ThisPcPath); return; }
            _ = GoUpWpdAsync(deviceId, objectId);
            return;
        }
        var parent = _fs.GetParent(CurrentPath);
        if (parent != null) Navigate(parent);
    }

    private async Task GoUpWpdAsync(string deviceId, string objectId)
    {
        var parentId = await Task.Run(() => WpdProvider.GetParentObjectId(deviceId, objectId));
        Navigate(WpdProvider.MakePath(deviceId, parentId));
    }

    [RelayCommand]
    private void Refresh()
    {
        if (IsSearchMode) _ = StartDeepSearchAsync();
        else _ = LoadDirectoryAsync(CurrentPath);
    }

    [RelayCommand]
    private void NavigateTo(string path) => Navigate(path);

    public void Navigate(string path)
    {
        if (!IsThisPc(path) && !IsWpd(path) && !IsArchive(path) && !_fs.DirectoryExists(path)) return;
        if (IsSearchMode)
        {
            _searchCts?.Cancel();
            IsSearchMode     = false;
            IsSearching      = false;
            _suppressFilters = true;
            SearchQuery      = string.Empty;
            _suppressFilters = false;
        }
        if (!string.IsNullOrEmpty(CurrentPath))
        {
            _backStack.Push(CurrentPath);
            _forwardStack.Clear();
        }
        _ = LoadDirectoryAsync(path);
        _history.Record(path);
        RefreshNavState();
    }

    // ── View & layout ─────────────────────────────────────────────────────────
    [RelayCommand]
    private void ToggleViewMode()
        => ThumbnailSize = ThumbnailSize == 0 ? Math.Max(50, _lastThumbnailSize) : 0;

    [RelayCommand]
    private void SetDetailsView() => ThumbnailSize = 0;

    [RelayCommand]
    private void SetThumbnailsView() => ThumbnailSize = ThumbnailSize > 0 ? ThumbnailSize : Math.Max(50, _lastThumbnailSize);

    [RelayCommand]
    private void TogglePreviewPane() => ShowPreviewPane = !ShowPreviewPane;

    [RelayCommand]
    private void ToggleFilterBar() => ShowFilterBar = !ShowFilterBar;

    [RelayCommand]
    private void ToggleFlatView()
    {
        if (!FlatView)
        {
            if (IsSearchMode) ClearSearch();
            FlatView = true;
        }
        else
        {
            FlatView = false;
        }
        _ = LoadDirectoryAsync(CurrentPath);
    }

    // ── Search ────────────────────────────────────────────────────────────────
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
                        Items.AddRange(flush);
                        OnPropertyChanged(nameof(ItemCountText));
                    });
                }
                if (batch.Count > 0)
                {
                    var flush = batch;
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        Items.AddRange(flush);
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

    // ── Preview pane ──────────────────────────────────────────────────────────
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

    // Entries above this size aren't previewed inside archives (would need full extraction).
    private const long ArchivePreviewSizeCap = 50L * 1024 * 1024;

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
            var pw   = _archiveAuth.GetValueOrDefault(archiveFile);
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

    // ── View reactions ────────────────────────────────────────────────────────
    partial void OnThumbnailSizeChanged(int value)
    {
        if (value > 0) _lastThumbnailSize = value;
        if (value > 0 && _allItems.Count > 0 && !IsSearchMode)
            _ = BeginThumbnailLoadAsync();
    }

    // ── Filter reactions ──────────────────────────────────────────────────────
    partial void OnSearchQueryChanged(string value)
    {
        if (_suppressFilters) return;

        _debounceCts?.Cancel();
        _debounceCts = new CancellationTokenSource();
        var cts = _debounceCts;

        if (string.IsNullOrEmpty(value))
        {
            if (IsSearchMode) ClearSearch();
            return;
        }

        // Debounce 300 ms then run the search with the current scope
        _ = Task.Delay(300, cts.Token).ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully)
                Application.Current.Dispatcher.Invoke(() => _ = StartDeepSearchAsync());
        });
    }

    partial void OnSelectedTypeFilterChanged(TypeFilterItem? value) { if (!_suppressFilters) ApplyFilters(); }
    partial void OnActiveSizeFilterChanged(SizeFilter      value) => ApplyFilters();
    partial void OnActiveDateFilterChanged(DateFilter      value) => ApplyFilters();
    partial void OnActiveSortColumnChanged(SortColumn      value) => ApplyFilters();
    partial void OnSortAscendingChanged(bool               value) => ApplyFilters();

    // ── Selection ─────────────────────────────────────────────────────────────
    public void UpdateSelection(IList<FileItem> selected)
    {
        SelectedItems = [.. selected];
        SelectedCount = selected.Count;
        SelectedItem  = selected.LastOrDefault();
        OnPropertyChanged(nameof(SelectionText));
    }

    // ── Reload & drop ─────────────────────────────────────────────────────────
    public void Reload()
    {
        if (IsSearchMode) _ = StartDeepSearchAsync();
        else              _ = LoadDirectoryAsync(CurrentPath);
    }

    public void ApplyClipboardHighlights() => ClipboardHighlightService.Apply(_allItems);

    public async Task DuplicateAsync(IEnumerable<string> sources)
    {
        try
        {
            await _fileOps.DuplicateAsync(sources);
            Reload();
        }
        catch (Exception ex) { System.Windows.MessageBox.Show(ex.Message, "Error"); }
    }

    public async Task DropFilesAsync(string[] files, string destFolder)
    {
        // Skip if every source file is already in the destination folder.
        if (files.All(f => string.Equals(
                System.IO.Path.GetDirectoryName(f), destFolder,
                StringComparison.OrdinalIgnoreCase))) return;

        try
        {
            await TransferManager.Instance.EnqueueAsync(TransferOperation.Copy, files, destFolder,
                FileOperationsService.ConflictResolution.KeepBoth);
            Reload();
        }
        catch { } // errors are swallowed for drag-drop; keyboard paste shows errors via MainViewModel
    }

    public void OpenFile(string path)
    {
        try
        {
            // A file inside an archive → extract to a temp copy and open it.
            if (IsArchive(path))
            {
                _ = OpenArchiveEntryAsync(path);
                return;
            }
            // A real archive on disk → browse into it rather than launching an external app.
            if (File.Exists(path) && ZephyrArchiveService.CanExtract(path))
            {
                Navigate(ArchivePath.Make(path));
                return;
            }
            if (IsWpd(path))
            {
                _ = OpenWpdFileAsync(path);
                return;
            }
            RecentInteractionService.Record(path);
            RecentFilesService.AddToRecentDocs(path);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path, UseShellExecute = true
            });
        }
        catch { }
    }

    // Shows a password prompt (with retry on a wrong password) until the user enters a valid
    // password or cancels. Returns the validated password, or null if cancelled.
    private string? PromptArchivePassword(string archiveFile)
    {
        bool retry = false;
        while (true)
        {
            var dlg = new PasswordDialog(Path.GetFileName(archiveFile), retry) { Owner = Application.Current.MainWindow };
            if (dlg.ShowDialog() != true) return null;
            if (ZephyrArchiveService.ValidatePassword(archiveFile, dlg.Password)) return dlg.Password;
            retry = true;
        }
    }

    // Prompts for a locked folder's password, retrying until correct or cancelled.
    // On success the root is marked unlocked for the rest of the session.
    private bool PromptFolderUnlock(LockedFolder root)
    {
        var name  = Path.GetFileName(root.Path.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : root.Path;
        bool retry = false;
        while (true)
        {
            var dlg = new PasswordDialog("Locked Folder",
                $"\"{name}\" is locked. Enter its password to open it.", retry)
                { Owner = Application.Current.MainWindow };
            if (dlg.ShowDialog() != true) return false;
            if (FolderLockService.Unlock(root, dlg.Password)) return true;
            retry = true;
        }
    }

    private async Task OpenArchiveEntryAsync(string virtualPath)
    {
        var (archiveFile, inner) = ArchivePath.Parse(virtualPath);
        var pw = _archiveAuth.GetValueOrDefault(archiveFile);
        string temp;
        try
        {
            temp = await Task.Run(() => ZephyrArchiveService.ExtractEntryToTemp(archiveFile, inner, pw));
        }
        catch (Exception ex)
        {
            Application.Current?.Dispatcher.Invoke(() => ZephyrMessageBox.Show(ex.Message, "Open"));
            return;
        }
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = temp, UseShellExecute = true
            });
        }
        catch { }
    }

    private async Task OpenWpdFileAsync(string path)
    {
        var (deviceId, objectId) = WpdProvider.ParsePath(path);
        var fileName = _allItems.FirstOrDefault(i => i.FullPath == path)?.Name
                       ?? objectId;
        var temp = await Task.Run(() => WpdProvider.CopyToTempFile(deviceId, objectId, fileName));
        if (string.IsNullOrEmpty(temp)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = temp, UseShellExecute = true
            });
        }
        catch { }
    }

    // ── Internal helpers ──────────────────────────────────────────────────────
    private static readonly Dictionary<string, string> ExtensionLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        { ".jpg",  "JPEG" }, { ".jpeg", "JPEG" }, { ".jpe", "JPEG" },
        { ".tiff", "TIFF" }, { ".tif",  "TIFF" },
        { ".heic", "HEIC" }, { ".heif", "HEIF" },
        { ".mp3",  "MP3"  }, { ".m4a",  "M4A"  }, { ".m4v", "M4V"  },
        { ".mp4",  "MP4"  }, { ".m4p",  "M4P"  },
        { ".3gp",  "3GP"  }, { ".3g2",  "3G2"  },
        { ".aac",  "AAC"  }, { ".ogg",  "OGG"  }, { ".oga", "OGA"  },
        { ".flac", "FLAC" }, { ".opus", "Opus" }, { ".wma", "WMA"  },
        { ".wav",  "WAV"  }, { ".aiff", "AIFF" }, { ".aif", "AIFF" },
        { ".mkv",  "MKV"  }, { ".webm", "WebM" }, { ".avi", "AVI"  },
        { ".mov",  "MOV"  }, { ".wmv",  "WMV"  }, { ".flv", "FLV"  },
        { ".docx", "DOCX" }, { ".doc",  "DOC"  },
        { ".xlsx", "XLSX" }, { ".xls",  "XLS"  },
        { ".pptx", "PPTX" }, { ".ppt",  "PPT"  },
        { ".pdf",  "PDF"  }, { ".epub", "EPUB" },
        { ".txt",  "TXT"  }, { ".md",   "MD"   }, { ".rtf", "RTF"  },
        { ".csv",  "CSV"  }, { ".tsv",  "TSV"  },
        { ".json", "JSON" }, { ".xml",  "XML"  }, { ".yaml","YAML" }, { ".yml", "YAML" },
        { ".zip",  "ZIP"  }, { ".rar",  "RAR"  }, { ".7z",  "7Z"   },
        { ".tar",  "TAR"  }, { ".gz",   "GZ"   }, { ".bz2", "BZ2"  },
        { ".exe",  "EXE"  }, { ".dll",  "DLL"  }, { ".msi", "MSI"  },
        { ".sh",   "SH"   }, { ".bat",  "BAT"  }, { ".cmd", "CMD"  },
        { ".ps1",  "PS1"  }, { ".py",   "PY"   }, { ".js",  "JS"   },
        { ".ts",   "TS"   }, { ".cs",   "CS"   }, { ".go",  "Go"   },
        { ".rs",   "RS"   }, { ".cpp",  "CPP"  }, { ".c",   "C"    },
        { ".h",    "H"    }, { ".java", "Java" }, { ".kt",  "KT"   },
        { ".swift","Swift"}, { ".rb",   "Ruby" }, { ".php", "PHP"  },
        { ".html", "HTML" }, { ".htm",  "HTML" }, { ".css", "CSS"  },
        { ".scss", "SCSS" }, { ".vue",  "Vue"  }, { ".jsx", "JSX"  },
        { ".tsx",  "TSX"  }, { ".sql",  "SQL"  },
    };

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
            var label = ExtensionLabels.TryGetValue(ext, out var l) ? l : ext.TrimStart('.').ToUpperInvariant();
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

    private async Task LoadDirectoryAsync(string path)
    {
        _loadCts.Cancel();
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;
        try
        {
            CurrentPath = path;

            if (IsThisPc(path))
            {
                var drives = await Task.Run(() => _fs.GetDrives()
                    .Select(d => new FileItem
                    {
                        Name         = d.DisplayName,
                        FullPath     = d.Name,
                        IsDirectory  = true,
                        Attributes   = FileAttributes.Directory,
                        LastModified = DateTime.MinValue,
                        Created      = DateTime.MinValue,
                    })
                    .ToList(), ct);

                if (ct.IsCancellationRequested) return;
                _allItems = drives;
                RebuildTypeFilterOptions();
                ApplyFilters();
                OnPropertyChanged(nameof(FreeSpaceText));
                SetupWatcher(null);
                return;
            }

            if (IsArchive(path))
            {
                var (archiveFile, inner) = ArchivePath.Parse(path);

                // Resolve a password once per archive (encrypted only). Cancel → leave the archive.
                if (!_archiveAuth.TryGetValue(archiveFile, out var pw))
                {
                    if (await Task.Run(() => ZephyrArchiveService.IsEncrypted(archiveFile), ct))
                    {
                        pw = PromptArchivePassword(archiveFile);
                        if (pw is null)
                        {
                            var folder = Path.GetDirectoryName(archiveFile);
                            Navigate(folder != null && Directory.Exists(folder) ? folder : ThisPcPath);
                            return;
                        }
                    }
                    _archiveAuth[archiveFile] = pw;
                }

                var children = await Task.Run(
                    () => ZephyrArchiveService.GetChildren(archiveFile, inner, pw), ct);
                if (ct.IsCancellationRequested) return;
                _allItems = children.Select(c => new FileItem
                {
                    Name         = Path.GetFileName(c.Path),
                    FullPath     = ArchivePath.Make(archiveFile, c.Path),
                    IsDirectory  = c.IsDirectory,
                    Size         = c.Size,
                    LastModified = c.Modified,
                    Created      = c.Modified,
                    Extension    = c.IsDirectory ? string.Empty : Path.GetExtension(c.Path).ToLowerInvariant(),
                    Attributes   = c.IsDirectory ? FileAttributes.Directory : FileAttributes.Normal,
                }).ToList();
                RebuildTypeFilterOptions();
                ApplyFilters();
                OnPropertyChanged(nameof(FreeSpaceText));
                SetupWatcher(null); // archives are read-only; nothing to watch
                if (ThumbnailSize > 0) _ = BeginThumbnailLoadAsync();
                return;
            }

            if (IsWpd(path))
            {
                var (deviceId, objectId) = WpdProvider.ParsePath(path);
                var wpdItems = await Task.Run(() => WpdProvider.GetChildren(deviceId, objectId), ct);
                if (ct.IsCancellationRequested) return;
                _allItems = wpdItems.Select(w => new FileItem
                {
                    Name         = w.Name,
                    FullPath     = WpdProvider.MakePath(deviceId, w.ObjectId),
                    IsDirectory  = w.IsFolder,
                    Size         = w.Size,
                    LastModified = w.DateModified,
                    Created      = w.DateModified,
                    Extension    = w.IsFolder ? string.Empty
                                 : System.IO.Path.GetExtension(w.Name).ToLowerInvariant(),
                    Attributes   = w.IsFolder ? FileAttributes.Directory : FileAttributes.Normal,
                }).ToList();
                RebuildTypeFilterOptions();
                ApplyFilters();
                OnPropertyChanged(nameof(FreeSpaceText));
                SetupWatcher(null);
                if (ThumbnailSize > 0) _ = BeginThumbnailLoadAsync();
                return;
            }

            // ── Folder lock gate (mirror the archive auth flow above) ──────────────
            if (FolderLockService.FindLockRoot(path) is { } lockRoot
                && !FolderLockService.IsUnlocked(lockRoot.Path))
            {
                if (!PromptFolderUnlock(lockRoot))
                {
                    // Cancelled — bounce out to the nearest accessible location.
                    var parent = _fs.GetParent(lockRoot.Path);
                    Navigate(parent != null && Directory.Exists(parent) && !FolderLockService.IsGated(parent)
                        ? parent : ThisPcPath);
                    return;
                }
            }

            var s = SettingsService.Current;

            List<FileItem> items;
            if (FlatView)
            {
                items = await Task.Run(() => LoadFlatItems(path, s, ct), ct);
            }
            else
            {
                items = await Task.Run(
                    () => _fs.GetContents(path, s.ShowHiddenFiles, s.ShowSystemFiles).ToList(), ct);
            }

            if (ct.IsCancellationRequested) return;
            foreach (var it in items)
                if (it.IsDirectory)
                {
                    it.IsLocked   = FolderLockService.IsLockRoot(it.FullPath);
                    it.IsUnlocked = it.IsLocked && FolderLockService.IsUnlocked(it.FullPath);
                }
            _allItems = items;
            RebuildTypeFilterOptions();
            ApplyFilters();
            ClipboardHighlightService.Apply(items);
            OnPropertyChanged(nameof(FreeSpaceText));
            SetupWatcher(path);
            if (!FlatView) _ = PopulateContentCountsAsync(items, ct);
            if (s.ShowFolderSizes) _ = PopulateFolderSizesAsync(items, ct);
            if (s.ShowCloudBadges) _ = PopulateCloudBadgesAsync(items, ct);
            if (s.ShowRecentlyInteracted || s.SortByRecentlyInteracted)
                _ = PopulateRecentInteractionsAsync(items, ct);
            if (ThumbnailSize > 0) _ = BeginThumbnailLoadAsync(items);
        }
        catch (OperationCanceledException) { }
        catch when (!ct.IsCancellationRequested)
        {
            Application.Current?.Dispatcher.BeginInvoke(HandlePathLost);
        }
    }

    private static List<FileItem> LoadFlatItems(string path, ZephyrSettings s, CancellationToken ct)
    {
        var results = new List<FileItem>();
        EnumerateFlat(new DirectoryInfo(path), results, s, ct);
        return results;
    }

    private static void EnumerateFlat(DirectoryInfo dir, List<FileItem> results, ZephyrSettings s, CancellationToken ct)
    {
        if (ct.IsCancellationRequested || results.Count > 10_000) return;
        try
        {
            foreach (var entry in dir.EnumerateFileSystemInfos())
            {
                if (ct.IsCancellationRequested || results.Count > 10_000) return;
                if (!s.ShowHiddenFiles && (entry.Attributes & FileAttributes.Hidden) != 0) continue;
                if (!s.ShowSystemFiles && (entry.Attributes & FileAttributes.System) != 0) continue;
                results.Add(new FileItem
                {
                    Name           = entry.Name,
                    FullPath       = entry.FullName,
                    IsDirectory    = entry is DirectoryInfo,
                    Size           = entry is FileInfo fi ? fi.Length : 0,
                    LastModified   = entry.LastWriteTime,
                    Created        = entry.CreationTime,
                    Extension      = entry is FileInfo ? Path.GetExtension(entry.Name).ToLowerInvariant() : string.Empty,
                    Attributes     = entry.Attributes,
                    SearchLocation = dir.FullName
                });
                if (entry is DirectoryInfo sub)
                    EnumerateFlat(sub, results, s, ct);
            }
        }
        catch (UnauthorizedAccessException) { }
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

    private void HandlePathLost()
    {
        _watcher?.Dispose();
        _watcher = null;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        ZephyrMessageBox.Show($"\"{CurrentPath}\" is no longer accessible.", "Folder Unavailable");
        Navigate(home);
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

    private static string FormatContentSummary(int folders, int files)
    {
        if (folders < 0) return "";
        var parts = new List<string>(2);
        if (folders > 0) parts.Add($"{folders} {(folders == 1 ? "folder" : "folders")}");
        if (files   > 0) parts.Add($"{files} {(files == 1 ? "file" : "files")}");
        return parts.Count > 0 ? string.Join(", ", parts) : "empty";
    }

    private void SetupWatcher(string? path)
    {
        _watcher?.Dispose();
        _watcher = null;
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            _watcher = new FileSystemWatcher(path)
            {
                NotifyFilter          = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
                IncludeSubdirectories = false,
                EnableRaisingEvents   = true,
            };
            _watcher.Created += OnWatcherEvent;
            _watcher.Deleted += OnWatcherEvent;
            _watcher.Renamed += OnWatcherEvent;
            _watcher.Changed += OnWatcherEvent;
            _watcher.Error   += OnWatcherError;
        }
        catch { _watcher = null; }
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (!string.IsNullOrEmpty(CurrentPath) && !Directory.Exists(CurrentPath))
                HandlePathLost();
        });
    }

    private void OnWatcherEvent(object sender, FileSystemEventArgs e)
    {
        _watcherDebounce?.Cancel();
        _watcherDebounce = new CancellationTokenSource();
        var cts = _watcherDebounce;
        _ = Task.Delay(600, cts.Token).ContinueWith(t =>
        {
            if (!t.IsCompletedSuccessfully) return;
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                if (!IsSearchMode) _ = LoadDirectoryAsync(CurrentPath);
            });
        }, TaskScheduler.Default);
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

    private void RefreshNavState()
    {
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
        OnPropertyChanged(nameof(CanGoUp));
        GoBackCommand.NotifyCanExecuteChanged();
        GoForwardCommand.NotifyCanExecuteChanged();
        GoUpCommand.NotifyCanExecuteChanged();
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

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):F1} GB"
    };
}
