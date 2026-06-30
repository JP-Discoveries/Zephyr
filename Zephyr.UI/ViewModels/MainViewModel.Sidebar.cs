using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Zephyr.Core.Models;
using Zephyr.Core.Settings;
using Zephyr.UI.Dialogs;
using Zephyr.UI.Services;

namespace Zephyr.UI.ViewModels;

// Sidebar data and behaviour: drives, portable devices, bookmarks, recent files, and
// network/cloud locations, plus the per-section collapse state.
public partial class MainViewModel
{
    public ObservableCollection<DriveItem>       Drives           { get; } = [];
    public ObservableCollection<DriveItem>       Devices          { get; } = [];
    public ObservableCollection<BookmarkItem>    Bookmarks        { get; } = [];
    public ObservableCollection<RecentFileItem>  RecentFiles      { get; } = [];
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

    /// <summary>Populates the sidebar's Drives, Devices, and Network sections without
    /// blocking the UI thread at startup. The drive + portable-device enumeration runs
    /// on a background thread; the collections are updated back on the UI thread.</summary>
    private async Task InitializeSidebarAsync()
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

        // Network locations reuse the now-warm drive list; cheap enough to run inline.
        LoadNetworkLocations();
    }

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
}
