using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Zephyr.Core.Archives;
using Zephyr.Core.Collections;
using Zephyr.Core.FileSystem;
using Zephyr.Core.History;
using Zephyr.Core.Models;
using Zephyr.Core.Search;
using Zephyr.Core.Settings;
using Zephyr.UI.Dialogs;
using Zephyr.UI.FileSystem;
using Zephyr.UI.Services;

namespace Zephyr.UI.ViewModels;

// A single browsing tab: current location, item list, selection, navigation history,
// and the view state (sort/filter/thumbnail size). The larger concerns live in sibling
// partial files — Loading (directory providers), Filters, Search, Preview, Thumbnails,
// Watcher — while this file holds state, computed display strings, navigation, and the
// file-open / drag-drop entry points.
public partial class TabViewModel : ObservableObject
{
    private readonly FileSystemService     _fs;
    private readonly NavigationHistory     _history;
    private readonly FileOperationsService _fileOps;

    // Directory loaders, tried in order; the local-folder provider is the fallback.
    private readonly ArchiveDirectoryProvider _archiveProvider = new();
    private readonly IDirectoryProvider[]     _providers;

    private readonly Stack<string> _backStack    = new();
    private readonly Stack<string> _forwardStack = new();
    private List<FileItem>         _allItems     = [];
    private CancellationTokenSource _loadCts      = new();
    private bool                    _suppressFilters;

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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MatchContentTooltip))]
    private bool _matchContent;

    public string MatchContentTooltip => MatchContent
        ? "Searching file contents — click to search names"
        : "Searching file names — click to search inside files";

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
        IsArchiveView ? _archiveProvider.GetCachedPassword(ArchivePath.Parse(CurrentPath).Archive) : null;

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
                return drive.IsReady ? $"Free: {ByteSize.Format(drive.AvailableFreeSpace)}" : string.Empty;
            }
            catch { return string.Empty; }
        }
    }

    // ── Construction / teardown ───────────────────────────────────────────────
    public TabViewModel(FileSystemService fs, NavigationHistory history, FileOperationsService fileOps, string startPath)
    {
        _fs = fs; _history = history; _fileOps = fileOps;
        _providers =
        [
            new ThisPcDirectoryProvider(fs),
            _archiveProvider,
            new WpdDirectoryProvider(),
            new LocalFolderProvider(fs),
        ];
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

    private async Task OpenArchiveEntryAsync(string virtualPath)
    {
        var (archiveFile, inner) = ArchivePath.Parse(virtualPath);
        var pw = _archiveProvider.GetCachedPassword(archiveFile);
        string temp;
        try
        {
            temp = await Task.Run(() => ZephyrArchiveService.ExtractEntryToTemp(archiveFile, inner, pw));
        }
        catch (Exception ex)
        {
            System.Windows.Application.Current?.Dispatcher.Invoke(() => ZephyrMessageBox.Show(ex.Message, "Open"));
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

    private void RefreshNavState()
    {
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
        OnPropertyChanged(nameof(CanGoUp));
        GoBackCommand.NotifyCanExecuteChanged();
        GoForwardCommand.NotifyCanExecuteChanged();
        GoUpCommand.NotifyCanExecuteChanged();
    }
}
