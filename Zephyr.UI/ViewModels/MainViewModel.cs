using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Interop;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Zephyr.Core.Archives;
using Zephyr.UI;
using Zephyr.Core.FileSystem;
using Zephyr.Core.History;
using Zephyr.Core.Models;
using Zephyr.Core.Settings;
using Zephyr.UI.Dialogs;
using Zephyr.UI.Services;
using Zephyr.UI.Windows;

namespace Zephyr.UI.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly FileSystemService     _fs;
    private readonly FileOperationsService _fileOps = new();

    public PaneViewModel     LeftPane  { get; }
    public PaneViewModel     RightPane { get; }
    public NavigationHistory History   { get; } = new();

    /// <summary>Shared queue powering the live transfer panel (copy/move progress).</summary>
    public TransferManager   Transfers => TransferManager.Instance;

    [ObservableProperty] private PaneViewModel _activePane;
    [ObservableProperty] private bool          _isSplitView    = false;
    [ObservableProperty] private bool          _isSidebarVisible = true;

    /// <summary>Central command registry (toolbar + hotkeys). Built once in the constructor.</summary>
    public List<AppCommand> AppCommands { get; } = [];

    /// <summary>The commands currently shown on the customizable toolbar, in order.</summary>
    public ObservableCollection<AppCommand> ToolbarItems { get; } = [];

    [RelayCommand]
    private void ToggleSplitView() => IsSplitView = !IsSplitView;

    [RelayCommand]
    private void NewTab() => ActivePane.AddTab();

    [RelayCommand]
    private void ToggleSidebar() => IsSidebarVisible = !IsSidebarVisible;

    // ── Dual-pane compare / mirror ──────────────────────────────────────────────

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MirrorLeftToRightCommand))]
    [NotifyCanExecuteChangedFor(nameof(MirrorRightToLeftCommand))]
    private bool _isCompareMode;

    // The tabs whose Items collections we're currently observing for live re-comparison.
    private TabViewModel? _cmpLeftTab;
    private TabViewModel? _cmpRightTab;

    [RelayCommand]
    private void ToggleCompare() => IsCompareMode = !IsCompareMode;

    partial void OnIsSplitViewChanged(bool value)
    {
        if (!value && IsCompareMode) IsCompareMode = false;
    }

    partial void OnIsCompareModeChanged(bool value)
    {
        if (value && !IsSplitView) IsSplitView = true;

        // Always detach first, then (re)attach if turning on.
        LeftPane.PropertyChanged  -= ComparePanePropertyChanged;
        RightPane.PropertyChanged -= ComparePanePropertyChanged;
        HookCompareTab(ref _cmpLeftTab,  null);
        HookCompareTab(ref _cmpRightTab, null);

        if (value)
        {
            LeftPane.PropertyChanged  += ComparePanePropertyChanged;
            RightPane.PropertyChanged += ComparePanePropertyChanged;
            HookCompareTab(ref _cmpLeftTab,  LeftPane.ActiveTab);
            HookCompareTab(ref _cmpRightTab, RightPane.ActiveTab);
            RecomputeCompare();
        }
    }

    private void ComparePanePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PaneViewModel.ActiveTab)) return;
        HookCompareTab(ref _cmpLeftTab,  LeftPane.ActiveTab);
        HookCompareTab(ref _cmpRightTab, RightPane.ActiveTab);
        RecomputeCompare();
    }

    private void HookCompareTab(ref TabViewModel? slot, TabViewModel? tab)
    {
        if (ReferenceEquals(slot, tab)) return;
        if (slot != null)
        {
            slot.Items.CollectionChanged -= CompareItemsChanged;
            PaneComparer.Clear(slot.Items);  // drop stale tints from the tab we're leaving
        }
        slot = tab;
        if (slot != null) slot.Items.CollectionChanged += CompareItemsChanged;
    }

    private void CompareItemsChanged(object? sender, NotifyCollectionChangedEventArgs e) => RecomputeCompare();

    private void RecomputeCompare()
    {
        if (!IsCompareMode) return;
        if (LeftPane.ActiveTab is not { } left || RightPane.ActiveTab is not { } right) return;
        PaneComparer.Compare(left.Items, right.Items);
    }

    private bool CanMirror() =>
        IsCompareMode && IsSplitView &&
        LeftPane.ActiveTab is { CurrentPath: var lp } && Directory.Exists(lp) &&
        RightPane.ActiveTab is { CurrentPath: var rp } && Directory.Exists(rp);

    [RelayCommand(CanExecute = nameof(CanMirror))]
    private Task MirrorLeftToRight() => MirrorAsync(LeftPane.ActiveTab!, RightPane.ActiveTab!);

    [RelayCommand(CanExecute = nameof(CanMirror))]
    private Task MirrorRightToLeft() => MirrorAsync(RightPane.ActiveTab!, LeftPane.ActiveTab!);

    // Copies everything missing or changed on the destination side, overwriting differing files.
    private async Task MirrorAsync(TabViewModel source, TabViewModel dest)
    {
        var toCopy = source.Items
            .Where(i => i.CompareStatus is CompareStatus.Unique or CompareStatus.Newer or CompareStatus.Different)
            .Select(i => i.FullPath)
            .ToList();

        if (toCopy.Count == 0)
        {
            ZephyrMessageBox.Show("Nothing to mirror — the destination already matches this folder.", "Mirror");
            return;
        }

        if (!ZephyrMessageBox.Confirm(
                $"Copy {toCopy.Count} new or changed item{(toCopy.Count == 1 ? "" : "s")} to:\n{dest.CurrentPath}\n\n" +
                "Existing files with the same name will be overwritten.",
                "Mirror", "Copy"))
            return;

        try
        {
            await Transfers.EnqueueAsync(TransferOperation.Copy, toCopy,
                dest.CurrentPath, FileOperationsService.ConflictResolution.Replace);
            dest.Reload();
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    public ObservableCollection<DriveItem>      Drives      { get; } = [];
    public ObservableCollection<DriveItem>      Devices     { get; } = [];
    public ObservableCollection<BookmarkItem>   Bookmarks   { get; } = [];
    public ObservableCollection<RecentFileItem> RecentFiles { get; } = [];
    public ObservableCollection<NetworkLocation> NetworkLocations { get; } = [];

    public bool HasDevices => Devices.Count > 0;

    public bool HasRecentFiles => RecentFiles.Count > 0;

    public bool HasNetworkLocations => NetworkLocations.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NetworkChevronIcon))]
    private bool _networkCollapsed;

    public string NetworkChevronIcon => NetworkCollapsed ? "" : "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BookmarksChevronIcon))]
    private bool _bookmarksCollapsed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DrivesChevronIcon))]
    private bool _drivesCollapsed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DevicesChevronIcon))]
    private bool _devicesCollapsed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RecentFilesChevronIcon))]
    private bool _recentFilesCollapsed;

    public string BookmarksChevronIcon   => BookmarksCollapsed   ? "" : "";
    public string DrivesChevronIcon      => DrivesCollapsed      ? "" : "";
    public string DevicesChevronIcon     => DevicesCollapsed     ? "" : "";
    public string RecentFilesChevronIcon => RecentFilesCollapsed ? "" : "";

    public string StatusText => $"{ActivePane.ItemCountText}{ActivePane.SelectionText}";

    private TabViewModel? ActiveTab => ActivePane.ActiveTab;

    // ── Undo stack ────────────────────────────────────────────────────────────

    private readonly Stack<Func<Task>> _undoStack = new();

    private void PushUndo(Func<Task> action)
    {
        _undoStack.Push(action);
        UndoCommand.NotifyCanExecuteChanged();
    }

    private bool CanUndo() => _undoStack.Count > 0;

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private async Task UndoAsync()
    {
        if (_undoStack.Count == 0) return;
        var action = _undoStack.Pop();
        UndoCommand.NotifyCanExecuteChanged();
        await action();
    }

    // ── Constructor ───────────────────────────────────────────────────────────

    public MainViewModel(FileSystemService fs, string? startPath = null)
    {
        _fs = fs;

        var home = !string.IsNullOrEmpty(startPath) ? startPath
                 : !string.IsNullOrEmpty(SettingsService.Current.StartupPath) ? SettingsService.Current.StartupPath
                 : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        LeftPane  = new PaneViewModel(fs, History, _fileOps, home);
        RightPane = new PaneViewModel(fs, History, _fileOps, home);
        _activePane = LeftPane;

        LeftPane.PropertyChanged  += (_, _) => { if (ActivePane == LeftPane)  OnPropertyChanged(nameof(StatusText)); };
        RightPane.PropertyChanged += (_, _) => { if (ActivePane == RightPane) OnPropertyChanged(nameof(StatusText)); };

        // Restore previous session when not launched with an explicit path
        if (startPath == null)
        {
            var s = SettingsService.Current;
            if (s.LeftPaneSession is { } ls && ls.TabPaths.Count > 0)
                LeftPane.RestoreTabs(ls.TabPaths, ls.ActiveIndex);
            if (s.RightPaneSession is { } rs && rs.TabPaths.Count > 0)
                RightPane.RestoreTabs(rs.TabPaths, rs.ActiveIndex);
            if (s.LastSplitView)
                IsSplitView = true;
        }

        BookmarksCollapsed   = SettingsService.Current.BookmarksCollapsed;
        DrivesCollapsed      = SettingsService.Current.DrivesCollapsed;
        DevicesCollapsed     = SettingsService.Current.DevicesCollapsed;
        RecentFilesCollapsed = SettingsService.Current.RecentFilesCollapsed;
        NetworkCollapsed     = SettingsService.Current.NetworkCollapsed;

        RecentFiles.CollectionChanged      += (_, _) => OnPropertyChanged(nameof(HasRecentFiles));
        Devices.CollectionChanged          += (_, _) => OnPropertyChanged(nameof(HasDevices));
        NetworkLocations.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNetworkLocations));

        LoadDrives();
        LoadBookmarks();
        LoadNetworkLocations();
        _ = LoadRecentFilesAsync();

        BuildAppCommands();
        RebuildToolbar();
    }

    // ── Command registry / customizable toolbar ─────────────────────────────────

    private void BuildAppCommands()
    {
        void Reg(string id, string name, string glyph, IRelayCommand cmd,
                 string gesture = "", bool toolbar = false, bool defaultOnToolbar = false) =>
            AppCommands.Add(new AppCommand
            {
                Id = id, Name = name, Glyph = glyph, Command = cmd,
                DefaultGesture = gesture, ToolbarEligible = toolbar, DefaultOnToolbar = defaultOnToolbar,
            });

        // View toggles — hotkeyable but not toolbar-customizable (they keep their fixed
        // active-state buttons on the left of the toolbar).
        Reg("toggle-sidebar", "Toggle Sidebar",    "", ToggleSidebarCommand,      "Ctrl+B");
        Reg("toggle-split",   "Toggle Split View", "", ToggleSplitViewCommand);
        Reg("toggle-compare", "Compare Panes",     "", ToggleCompareCommand);

        // Toolbar-eligible actions.
        Reg("new-folder",   "New Folder",      "", NewFolderCommand,   "Ctrl+Shift+N", toolbar: true, defaultOnToolbar: true);
        Reg("new-tab",      "New Tab",         "", NewTabCommand,      "Ctrl+T",       toolbar: true);
        Reg("copy",         "Copy",            "", CopyCommand,        "Ctrl+C",       toolbar: true, defaultOnToolbar: true);
        Reg("cut",          "Cut",             "", CutCommand,         "Ctrl+X",       toolbar: true, defaultOnToolbar: true);
        Reg("paste",        "Paste",           "", PasteCommand,       "Ctrl+V",       toolbar: true, defaultOnToolbar: true);
        Reg("rename",       "Rename",          "", RenameCommand,      "F2",           toolbar: true, defaultOnToolbar: true);
        Reg("delete",       "Delete",          "", DeleteCommand,      "Delete",       toolbar: true, defaultOnToolbar: true);
        Reg("terminal",     "Open Terminal",   "", OpenTerminalCommand,"Ctrl+Oemtilde",toolbar: true, defaultOnToolbar: true);
        Reg("compress",     "Compress…",       "", CreateZipCommand,   "",             toolbar: true, defaultOnToolbar: true);
        Reg("extract",      "Extract Archive…","", ExtractZipCommand,  "",             toolbar: true, defaultOnToolbar: true);
        Reg("batch-rename", "Batch Rename",    "", BatchRenameCommand, "",             toolbar: true, defaultOnToolbar: true);
        Reg("undo",         "Undo",            "", UndoCommand,        "Ctrl+Z",       toolbar: true);
        Reg("settings",     "Settings",        "", OpenSettingsCommand,"Ctrl+OemComma",toolbar: true, defaultOnToolbar: true);

        // Hotkey-only commands.
        Reg("delete-permanent", "Delete Permanently", "", PermanentDeleteCommand,  "Shift+Delete");
        Reg("command-palette",  "Command Palette",    "", OpenCommandPaletteCommand, "Ctrl+P");
    }

    public void RebuildToolbar()
    {
        ToolbarItems.Clear();
        var ids = SettingsService.Current.Toolbar;
        IEnumerable<AppCommand> chosen = ids is { Count: > 0 }
            ? ids.Select(id => AppCommands.FirstOrDefault(c => c.Id == id))
                 .Where(c => c is { ToolbarEligible: true })!
            : AppCommands.Where(c => c is { ToolbarEligible: true, DefaultOnToolbar: true });
        foreach (var c in chosen) ToolbarItems.Add(c!);
    }

    public void SaveSession()
    {
        var s = SettingsService.Current;
        s.LeftPaneSession  = CapturePane(LeftPane);
        s.RightPaneSession = CapturePane(RightPane);
        s.LastSplitView    = IsSplitView;
        SettingsService.Save(s);
    }

    private static PaneSession CapturePane(PaneViewModel pane)
    {
        var paths = pane.Tabs
            .Select(t => t.CurrentPath)
            .Where(p => !string.IsNullOrEmpty(p))
            .ToList();
        var idx = pane.ActiveTab is { } at ? pane.Tabs.IndexOf(at) : 0;
        return new PaneSession { TabPaths = paths, ActiveIndex = Math.Max(0, idx) };
    }

    public void Cleanup() { }

    public async void RefreshDrives()
    {
        var (drives, devices) = await Task.Run(() =>
        {
            var ds = _fs.GetDrives().ToList();
            return (ds, PortableDeviceService.GetPortableDevices(ds).ToList());
        });
        Drives.Clear();
        foreach (var d in drives)  Drives.Add(d);
        Devices.Clear();
        foreach (var d in devices) Devices.Add(d);
    }

    public async Task LoadRecentFilesAsync()
    {
        var files = await Task.Run(() => RecentFilesService.GetRecentFiles());
        RecentFiles.Clear();
        foreach (var f in files) RecentFiles.Add(f);
    }

    public void SetActivePane(PaneViewModel pane)
    {
        ActivePane = pane;
        OnPropertyChanged(nameof(StatusText));
    }

    // ── Sidebar collapse ──────────────────────────────────────────────────────

    [RelayCommand]
    private void ToggleBookmarksCollapsed()
    {
        BookmarksCollapsed = !BookmarksCollapsed;
        PersistSidebarState();
    }

    [RelayCommand]
    private void ToggleDrivesCollapsed()
    {
        DrivesCollapsed = !DrivesCollapsed;
        PersistSidebarState();
    }

    [RelayCommand]
    private void ToggleDevicesCollapsed()
    {
        DevicesCollapsed = !DevicesCollapsed;
        PersistSidebarState();
    }

    [RelayCommand]
    private void ToggleRecentFilesCollapsed()
    {
        RecentFilesCollapsed = !RecentFilesCollapsed;
        PersistSidebarState();
    }

    [RelayCommand]
    private void ToggleNetworkCollapsed()
    {
        NetworkCollapsed = !NetworkCollapsed;
        PersistSidebarState();
    }

    private void PersistSidebarState()
    {
        var s = SettingsService.Current;
        s.BookmarksCollapsed   = BookmarksCollapsed;
        s.DrivesCollapsed      = DrivesCollapsed;
        s.DevicesCollapsed     = DevicesCollapsed;
        s.RecentFilesCollapsed = RecentFilesCollapsed;
        s.NetworkCollapsed     = NetworkCollapsed;
        SettingsService.Save(s);
    }

    // ── Network & cloud locations ───────────────────────────────────────────────

    private const string GlyphCloud   = "";  // Cloud
    private const string GlyphNetwork = "";  // MapDrive / network share

    public void LoadNetworkLocations()
    {
        NetworkLocations.Clear();

        // Detected cloud-sync folders (OneDrive / Dropbox / …) — auto, not removable.
        foreach (var root in CloudSyncService.SyncRoots)
        {
            if (!Directory.Exists(root)) continue;
            NetworkLocations.Add(new NetworkLocation
            {
                Name = Path.GetFileName(root.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : root,
                Path = root, Detail = root, Glyph = GlyphCloud, IsRemovable = false,
            });
        }

        // Mapped network drives — auto, not removable (they come and go with the connection).
        foreach (var d in _fs.GetDrives().Where(d => d.DriveType == DriveType.Network))
            NetworkLocations.Add(new NetworkLocation
            {
                Name = d.DisplayName, Path = d.Name, Detail = d.Name,
                Glyph = GlyphNetwork, IsRemovable = false,
            });

        // User-pinned UNC paths / folders.
        foreach (var pin in SettingsService.Current.NetworkPins)
            NetworkLocations.Add(new NetworkLocation
            {
                Name = string.IsNullOrWhiteSpace(pin.Name) ? pin.Path : pin.Name,
                Path = pin.Path, Detail = pin.Path, Glyph = GlyphNetwork, IsRemovable = true,
            });
    }

    [RelayCommand]
    private void AddNetworkLocation()
    {
        var dlg = new AddNetworkLocationDialog { Owner = Application.Current.MainWindow };
        if (dlg.ShowDialog() != true) return;

        var path = dlg.LocationPath;
        if (string.IsNullOrWhiteSpace(path)) return;
        if (SettingsService.Current.NetworkPins.Any(p => p.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
            return;

        SettingsService.Current.NetworkPins.Add(new BookmarkItem { Name = dlg.LocationName, Path = path });
        SettingsService.Save(SettingsService.Current);
        LoadNetworkLocations();
        ActivePane.ActiveTab?.Navigate(path);
    }

    public void RemoveNetworkLocation(NetworkLocation location)
    {
        if (!location.IsRemovable) return;
        SettingsService.Current.NetworkPins.RemoveAll(p =>
            p.Path.Equals(location.Path, StringComparison.OrdinalIgnoreCase));
        SettingsService.Save(SettingsService.Current);
        LoadNetworkLocations();
    }

    // ── Bookmarks ─────────────────────────────────────────────────────────────

    public void AddBookmark(string name, string path)
    {
        if (Bookmarks.Any(b => b.Path.Equals(path, StringComparison.OrdinalIgnoreCase))) return;
        Bookmarks.Add(new BookmarkItem { Name = name, Path = path });
        PersistBookmarks();
    }

    public void RemoveBookmark(BookmarkItem bookmark)
    {
        Bookmarks.Remove(bookmark);
        PersistBookmarks();
    }

    public void MoveBookmark(int fromIndex, int toIndex)
    {
        if (fromIndex < 0 || toIndex < 0 || fromIndex == toIndex) return;
        if (fromIndex >= Bookmarks.Count || toIndex >= Bookmarks.Count) return;
        Bookmarks.Move(fromIndex, toIndex);
        PersistBookmarks();
    }

    public void RenameBookmark(BookmarkItem bookmark, string newName)
    {
        var idx = Bookmarks.IndexOf(bookmark);
        if (idx < 0) return;
        bookmark.Name = newName;
        Bookmarks.RemoveAt(idx);
        Bookmarks.Insert(idx, bookmark);
        PersistBookmarks();
    }

    private void PersistBookmarks()
    {
        var s = SettingsService.Current;
        s.Bookmarks = [.. Bookmarks];
        SettingsService.Save(s);
    }

    private void LoadBookmarks()
    {
        var saved = SettingsService.Current.Bookmarks;
        if (saved.Count == 0)
        {
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var defaults = new BookmarkItem[]
            {
                new() { Name = "Desktop",   Path = Environment.GetFolderPath(Environment.SpecialFolder.Desktop) },
                new() { Name = "Documents", Path = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) },
                new() { Name = "Downloads", Path = Path.Combine(profile, "Downloads") },
                new() { Name = "Pictures",  Path = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures) },
                new() { Name = "Music",     Path = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic) },
                new() { Name = "Videos",    Path = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos) },
            };
            foreach (var b in defaults.Where(b => Directory.Exists(b.Path)))
                Bookmarks.Add(b);
        }
        else
        {
            foreach (var b in saved.Where(b => Directory.Exists(b.Path)))
                Bookmarks.Add(b);
        }
    }

    // ── File operations ───────────────────────────────────────────────────────

    [RelayCommand]
    private void NewFolder()
    {
        if (ActiveTab is not { } tab) return;
        var dlg = new InputDialog("New Folder", "Folder name:", "New Folder")
            { Owner = Application.Current.MainWindow };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var path = _fileOps.CreateFolder(tab.CurrentPath, dlg.Result);
            RecentInteractionService.Record(path);
            tab.Reload();
            PushUndo(async () =>
            {
                try
                {
                    await Task.Run(() =>
                    {
                        if (Directory.Exists(path))
                            Directory.Delete(path, recursive: false);
                    });
                    ActiveTab?.Reload();
                }
                catch (Exception ex) { ShowError($"Undo failed: {ex.Message}"); }
            });
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    [RelayCommand]
    private void Copy()
    {
        var items = ActiveTab?.SelectedItems;
        if (items is null || items.Count == 0) return;
        var paths = items.Select(i => i.FullPath).ToList();
        ClipboardService.SetFiles(paths, ClipboardEffect.Copy);
        ClipboardHighlightService.Set(paths, ClipboardEffect.Copy);
        RefreshClipboardHighlights();
    }

    [RelayCommand]
    private void Cut()
    {
        var items = ActiveTab?.SelectedItems;
        if (items is null || items.Count == 0) return;
        var paths = items.Select(i => i.FullPath).ToList();
        ClipboardService.SetFiles(paths, ClipboardEffect.Cut);
        ClipboardHighlightService.Set(paths, ClipboardEffect.Cut);
        RefreshClipboardHighlights();
    }

    [RelayCommand]
    private void ClearClipboard()
    {
        ClipboardService.Clear();
        ClipboardHighlightService.Clear();
        RefreshClipboardHighlights();
    }

    private void RefreshClipboardHighlights()
    {
        foreach (var pane in new[] { LeftPane, RightPane })
        foreach (var tab in pane.Tabs)
            tab.ApplyClipboardHighlights();
    }

    [RelayCommand]
    private async Task PasteAsync()
    {
        if (ActiveTab is not { } tab) return;
        if (!ClipboardService.HasFiles()) return;
        var (paths, effect) = ClipboardService.GetFiles();
        if (paths.Count == 0) return;
        try
        {
            if (effect == ClipboardEffect.Cut)
            {
                // Skip files that are already in the destination — moving them would
                // delete the original and produce a "(2)" copy for no reason.
                var filtered = paths
                    .Where(p => !string.Equals(
                        Path.GetDirectoryName(p), tab.CurrentPath,
                        StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (filtered.Count == 0) { ClipboardService.Clear(); ClipboardHighlightService.Clear(); RefreshClipboardHighlights(); return; }
                var outcome = await Transfers.EnqueueAsync(TransferOperation.Move, filtered,
                    tab.CurrentPath, FileOperationsService.ConflictResolution.KeepBoth);
                tab.Reload();
                ClipboardService.Clear(); // mirrors Explorer: cut clipboard is consumed after paste
                ClipboardHighlightService.Clear();
                RefreshClipboardHighlights();
                if (outcome is { RootPairs.Count: > 0 })
                {
                    var captured = outcome.RootPairs; // capture for lambda
                    PushUndo(async () =>
                    {
                        try
                        {
                            await Task.Run(() =>
                            {
                                foreach (var (src, dest) in captured)
                                {
                                    if (!File.Exists(dest) && !Directory.Exists(dest)) continue;
                                    var srcDir = Path.GetDirectoryName(src)!;
                                    Directory.CreateDirectory(srcDir);
                                    if (Directory.Exists(dest)) Directory.Move(dest, src);
                                    else                         File.Move(dest, src, overwrite: false);
                                }
                            });
                            ActiveTab?.Reload();
                        }
                        catch (Exception ex) { ShowError($"Undo failed: {ex.Message}"); }
                    });
                }
            }
            else
            {
                var outcome = await Transfers.EnqueueAsync(TransferOperation.Copy, paths,
                    tab.CurrentPath, FileOperationsService.ConflictResolution.KeepBoth);
                tab.Reload();
                ClipboardHighlightService.Clear();
                RefreshClipboardHighlights();
                if (outcome is { CreatedRoots.Count: > 0 })
                {
                    var captured = outcome.CreatedRoots;
                    PushUndo(async () =>
                    {
                        try
                        {
                            await Task.Run(() =>
                            {
                                foreach (var dest in captured)
                                {
                                    if      (File.Exists(dest))      File.Delete(dest);
                                    else if (Directory.Exists(dest)) Directory.Delete(dest, recursive: true);
                                }
                            });
                            ActiveTab?.Reload();
                        }
                        catch (Exception ex) { ShowError($"Undo failed: {ex.Message}"); }
                    });
                }
            }
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    [RelayCommand]
    private void Rename()
    {
        if (ActiveTab?.SelectedItem is not { } item) return;
        var dlg = new InputDialog("Rename", "New name:", item.Name)
            { Owner = Application.Current.MainWindow };
        if (dlg.ShowDialog() != true) return;
        var result = dlg.Result;
        if (!item.IsDirectory)
        {
            var origExt = Path.GetExtension(item.Name);
            if (!string.IsNullOrEmpty(origExt) && string.IsNullOrEmpty(Path.GetExtension(result)))
                result += origExt;
        }
        var newPath = Path.Combine(Path.GetDirectoryName(item.FullPath)!, result);
        if (!string.Equals(item.FullPath, newPath, StringComparison.OrdinalIgnoreCase) &&
            (File.Exists(newPath) || Directory.Exists(newPath)))
        {
            ZephyrMessageBox.Show($"A file named \"{result}\" already exists in this folder.", "Rename");
            return;
        }
        var oldName = item.Name;
        try
        {
            _fileOps.Rename(item.FullPath, result);
            RecentInteractionService.Record(newPath);
            ActiveTab.Reload();
            PushUndo(async () =>
            {
                try
                {
                    await Task.Run(() => _fileOps.Rename(newPath, oldName));
                    ActiveTab?.Reload();
                }
                catch (Exception ex) { ShowError($"Undo failed: {ex.Message}"); }
            });
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    [RelayCommand]
    private void Delete()
    {
        var items = ActiveTab?.SelectedItems;
        if (items is null || items.Count == 0) return;
        var label = items.Count == 1 ? $"'{items[0].Name}'" : $"{items.Count} items";
        if (!ZephyrMessageBox.Confirm($"Send {label} to the Recycle Bin?", "Delete", "Delete")) return;
        try
        {
            var hwnd = new WindowInteropHelper(Application.Current.MainWindow).Handle;
            _fileOps.Delete(items.Select(i => i.FullPath), hwnd: hwnd);
            ActiveTab!.Reload();
            // Record undo using the Windows Shell's own undo for recycle bin operations.
            // SHFileOperation with FOF_ALLOWUNDO records in the global shell undo stack.
            PushUndo(async () =>
            {
                try
                {
                    await Task.Run(() =>
                    {
                        var shellType = Type.GetTypeFromProgID("Shell.Application");
                        if (shellType == null) return;
                        dynamic shell = Activator.CreateInstance(shellType)!;
                        shell.UndoFileOperation();
                    });
                    await Task.Delay(400); // allow Shell to restore before reloading
                    ActiveTab?.Reload();
                }
                catch (Exception ex) { ShowError($"Undo failed: {ex.Message}"); }
            });
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    [RelayCommand]
    private void PermanentDelete()
    {
        var items = ActiveTab?.SelectedItems;
        if (items is null || items.Count == 0) return;
        var label = items.Count == 1 ? $"'{items[0].Name}'" : $"{items.Count} items";
        if (!ZephyrMessageBox.Confirm($"Permanently delete {label}? This cannot be undone.", "Delete Forever", "Delete")) return;
        try
        {
            _fileOps.Delete(items.Select(i => i.FullPath), permanent: true);
            ActiveTab!.Reload();
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    [RelayCommand]
    private void OpenTerminal()
    {
        if (ActiveTab?.CurrentPath is { } path)
            TerminalService.OpenAt(path);
    }

    [RelayCommand]
    private void ExtractZip()
    {
        var tab = ActiveTab;
        if (tab is null) return;

        var archives = tab.SelectedItems
            .Where(i => !i.IsDirectory && ZephyrArchiveService.CanExtract(i.FullPath))
            .ToList();
        if (archives.Count == 0) return;

        // Single archive defaults to its own subfolder; a batch defaults to the current folder.
        var defaultDest = archives.Count == 1
            ? Path.Combine(tab.CurrentPath, StripArchiveExtension(archives[0].Name))
            : tab.CurrentPath;

        var dlg = new ExtractDialog(archives.Select(a => a.Name).ToList(), defaultDest)
            { Owner = Application.Current.MainWindow };
        if (dlg.ShowDialog() != true) return;

        var opts  = new ZephyrArchiveService.ExtractOptions(Password: dlg.Password);
        var title = archives.Count == 1 ? $"Extracting {archives[0].Name}…" : $"Extracting {archives.Count} archives…";

        RunWithProgress(title, async (prog, ct) =>
        {
            for (int i = 0; i < archives.Count; i++)
            {
                var archive = archives[i];
                var dest = archives.Count == 1 ? dlg.Destination
                         : dlg.EachToOwnSubfolder ? Path.Combine(dlg.Destination, StripArchiveExtension(archive.Name))
                         : dlg.Destination;

                // For a batch, prefix each report with "(i/n) name" so the user sees which archive.
                int idx = i + 1;
                IProgress<ZephyrArchiveService.ArchiveProgress> sub = archives.Count == 1
                    ? prog
                    : new Progress<ZephyrArchiveService.ArchiveProgress>(p =>
                        prog.Report(p with { CurrentEntry = $"({idx}/{archives.Count}) {archive.Name} — {p.CurrentEntry}" }));

                await ZephyrArchiveService.ExtractAsync(archive.FullPath, dest, opts, sub, ct);
            }
        });
        tab.Reload();
    }

    [RelayCommand]
    private void CreateZip()
    {
        var tab   = ActiveTab;
        var items = tab?.SelectedItems;
        if (tab is null || items is null || items.Count == 0) return;

        var defaultName = items.Count == 1 ? Path.GetFileNameWithoutExtension(items[0].Name) : "Archive";
        var dlg = new CompressDialog(defaultName, tab.CurrentPath, items.Count)
            { Owner = Application.Current.MainWindow };
        if (dlg.ShowDialog() != true) return;

        var sources = items.Select(i => i.FullPath).ToList();
        var name    = Path.GetFileName(dlg.ResultPath);
        if (dlg.AddToExisting)
            RunWithProgress($"Adding to {name}…",
                (prog, ct) => ZephyrArchiveService.AppendToZipAsync(dlg.ResultPath, sources, dlg.Options.Level, prog, ct));
        else
            RunWithProgress($"Compressing {name}…",
                (prog, ct) => ZephyrArchiveService.CreateAsync(dlg.ResultPath, sources, dlg.Options, prog, ct));
        tab.Reload();
    }

    // Shows the modal progress dialog for an archive operation and surfaces any error.
    private void RunWithProgress(string title,
        Func<IProgress<ZephyrArchiveService.ArchiveProgress>, CancellationToken, Task> work)
    {
        var dlg = new ArchiveProgressDialog(title, work) { Owner = Application.Current.MainWindow };
        dlg.ShowDialog();
        if (dlg.Error is { } ex) ShowError(ex.Message);
    }

    // Strips a compound (.tar.gz/.tar.bz2/.tar.xz) or single archive extension for naming subfolders.
    private static string StripArchiveExtension(string name)
    {
        foreach (var ext in new[] { ".tar.gz", ".tar.bz2", ".tar.xz" })
            if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                return name[..^ext.Length];
        return Path.GetFileNameWithoutExtension(name);
    }

    [RelayCommand]
    private void BatchRename()
    {
        var items = ActiveTab?.SelectedItems;
        if (items is null || items.Count < 2) return;
        var dlg = new BatchRenameDialog(items.Select(i => i.FullPath))
            { Owner = Application.Current.MainWindow };
        if (dlg.ShowDialog() != true) return;
        try
        {
            foreach (var (oldPath, newName) in dlg.Results)
                _fileOps.Rename(oldPath, newName);
            ActiveTab!.Reload();
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    // ── Settings ──────────────────────────────────────────────────────────────

    [RelayCommand]
    private void OpenSettings()
    {
        var dlg = new SettingsWindow(this) { Owner = Application.Current.MainWindow };
        var ok = dlg.ShowDialog() == true;

        // Shortcut/toolbar edits are applied live by the dialog, so refresh regardless of OK.
        RebuildToolbar();
        if (Application.Current.MainWindow is MainWindow mw) mw.ApplyHotkeys();

        if (!ok) return;
        new ThemeService().Apply(Application.Current, SettingsService.Current.ThemeMode);
        ReloadAllPanes();
        if (Application.Current.MainWindow is MainWindow mw2)
        {
            mw2.ApplyDarkTitleBar();
            if (SettingsService.Current.LaunchMaximized)
                mw2.WindowState = WindowState.Maximized;
        }
    }

    public void ReloadAllPanes()
    {
        FileItem.ShowExtensions = SettingsService.Current.ShowFileExtensions;
        foreach (var tab in LeftPane.Tabs)  tab.Reload();
        foreach (var tab in RightPane.Tabs) tab.Reload();
    }

    // ── Command Palette ─────────────────────────────────────────────────────

    [RelayCommand]
    private void OpenCommandPalette()
    {
        var palette = new CommandPaletteWindow(BuildPaletteItems()) { Owner = Application.Current.MainWindow };
        if (Application.Current.MainWindow is { } owner) palette.PositionOver(owner);
        palette.Show();
    }

    private List<PaletteItem> BuildPaletteItems()
    {
        PaletteItem Cmd(string title, string glyph, IRelayCommand cmd, string gesture = "") => new()
        {
            Title = title, Glyph = glyph, Category = "Command", Gesture = gesture,
            Enabled = cmd.CanExecute(null),
            Action  = () => { if (cmd.CanExecute(null)) cmd.Execute(null); },
        };

        PaletteItem Go(string title, string path) => new()
        {
            Title = title, Subtitle = path, Glyph = "", Category = "Go to",
            Action = () => ActivePane.ActiveTab?.Navigate(path),
        };

        var items = new List<PaletteItem>
        {
            Cmd("New Folder",         "", NewFolderCommand,         "Ctrl+Shift+N"),
            Cmd("New Tab",            "", ActivePane.NewTabCommand, "Ctrl+T"),
            Cmd("Copy",               "", CopyCommand,              "Ctrl+C"),
            Cmd("Cut",                "", CutCommand,               "Ctrl+X"),
            Cmd("Paste",              "", PasteCommand,             "Ctrl+V"),
            Cmd("Rename",             "", RenameCommand,            "F2"),
            Cmd("Delete",             "", DeleteCommand,            "Del"),
            Cmd("Delete Permanently", "", PermanentDeleteCommand,   "Shift+Del"),
            Cmd("Undo",               "", UndoCommand,              "Ctrl+Z"),
            Cmd("Open Terminal",      "", OpenTerminalCommand,      "Ctrl+`"),
            Cmd("Compress…",          "", CreateZipCommand),
            Cmd("Extract Archive…",   "", ExtractZipCommand),
            Cmd("Batch Rename",       "", BatchRenameCommand),
            Cmd("Toggle Split View",  "", ToggleSplitViewCommand),
            Cmd("Toggle Sidebar",     "", ToggleSidebarCommand,     "Ctrl+B"),
            Cmd("Settings",           "", OpenSettingsCommand,      "Ctrl+,"),
        };

        // Quick "Go to" common locations.
        void AddSpecial(string title, string path)
        {
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path)) items.Add(Go(title, path));
        }
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        AddSpecial("Home",      profile);
        AddSpecial("Desktop",   Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
        AddSpecial("Documents", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        AddSpecial("Downloads", Path.Combine(profile, "Downloads"));

        foreach (var b in Bookmarks) items.Add(Go(b.Name, b.Path));
        foreach (var d in Drives)
            items.Add(new PaletteItem
            {
                Title = d.DisplayName, Subtitle = d.Name, Glyph = "", Category = "Drive",
                Action = () => ActivePane.ActiveTab?.Navigate(d.Name),
            });
        foreach (var p in History.RecentPaths)
            items.Add(new PaletteItem
            {
                Title = FolderTitle(p), Subtitle = p, Glyph = "", Category = "Recent",
                Action = () => ActivePane.ActiveTab?.Navigate(p),
            });

        return items;
    }

    private static string FolderTitle(string path)
    {
        var name = Path.GetFileName(path.TrimEnd('\\', '/'));
        return string.IsNullOrEmpty(name) ? path : name;
    }

    // ── Sidebar data ──────────────────────────────────────────────────────────

    private void LoadDrives()
    {
        var drives = _fs.GetDrives();
        foreach (var drive in drives)
            Drives.Add(drive);
        foreach (var device in PortableDeviceService.GetPortableDevices(drives))
            Devices.Add(device);
    }

    private static void ShowError(string msg) =>
        ZephyrMessageBox.Show(msg, "Error");
}
