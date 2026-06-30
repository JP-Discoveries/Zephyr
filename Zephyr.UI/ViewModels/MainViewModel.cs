using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Zephyr.Core.FileSystem;
using Zephyr.Core.History;
using Zephyr.Core.Models;
using Zephyr.Core.Settings;
using Zephyr.UI.Dialogs;

namespace Zephyr.UI.ViewModels;

// Window-level view model: owns the two panes, split/compare state, the command registry,
// the sidebar data, and the file-operation commands. The larger concerns live in sibling
// partial files — FileOps, Archive, Sidebar, Compare, Commands — leaving this file with
// construction, pane/layout state, and session save/restore.
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

    [RelayCommand]
    private void ToggleSplitView() => IsSplitView = !IsSplitView;

    [RelayCommand]
    private void NewTab() => ActivePane.AddTab();

    [RelayCommand]
    private void ToggleSidebar() => IsSidebarVisible = !IsSidebarVisible;

    partial void OnIsSplitViewChanged(bool value)
    {
        if (!value && IsCompareMode) IsCompareMode = false;
    }

    public string StatusText => $"{ActivePane.ItemCountText}{ActivePane.SelectionText}";

    private TabViewModel? ActiveTab => ActivePane.ActiveTab;

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

        LoadBookmarks();
        _ = LoadRecentFilesAsync();

        // Drives, portable devices, and network locations do slow blocking I/O and COM
        // (WPD/MTP enumeration in particular). Load them off the UI thread so the window
        // paints immediately and the sidebar fills in a moment later.
        _ = InitializeSidebarAsync();

        BuildAppCommands();
        RebuildToolbar();
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

    public void SetActivePane(PaneViewModel pane)
    {
        ActivePane = pane;
        OnPropertyChanged(nameof(StatusText));
    }

    public void ReloadAllPanes()
    {
        FileItem.ShowExtensions = SettingsService.Current.ShowFileExtensions;
        foreach (var tab in LeftPane.Tabs)  tab.Reload();
        foreach (var tab in RightPane.Tabs) tab.Reload();
    }

    private static void ShowError(string msg) =>
        ZephyrMessageBox.Show(msg, "Error");
}
