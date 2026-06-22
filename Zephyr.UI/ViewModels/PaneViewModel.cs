using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Zephyr.Core.FileSystem;
using Zephyr.Core.History;

namespace Zephyr.UI.ViewModels;

public partial class PaneViewModel : ObservableObject
{
    private readonly FileSystemService     _fs;
    private readonly NavigationHistory     _history;
    private readonly FileOperationsService _fileOps;

    public ObservableCollection<TabViewModel> Tabs { get; } = [];
    public NavigationHistory History => _history;

    [ObservableProperty] private TabViewModel? _activeTab;

    // Forwarded from ActiveTab so MainViewModel can observe a single source
    public string ItemCountText  => ActiveTab?.ItemCountText  ?? string.Empty;
    public string SelectionText  => ActiveTab?.SelectionText  ?? string.Empty;
    public string FreeSpaceText  => ActiveTab?.FreeSpaceText  ?? string.Empty;

    public PaneViewModel(FileSystemService fs, NavigationHistory history, FileOperationsService fileOps, string? startPath = null)
    {
        _fs = fs;
        _history = history;
        _fileOps = fileOps;
        AddTab(startPath);
    }

    public TabViewModel AddTab(string? path = null)
    {
        var startPath = path
            ?? ActiveTab?.CurrentPath
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var tab = new TabViewModel(_fs, _history, _fileOps, startPath);
        Tabs.Add(tab);
        ActiveTab = tab;
        return tab;
    }

    public void RestoreTabs(IReadOnlyList<string> paths, int activeIndex)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var t in Tabs.ToList()) t.Cleanup();
        Tabs.Clear();
        foreach (var p in paths)
        {
            var resolved = Directory.Exists(p) ? p : home;
            Tabs.Add(new TabViewModel(_fs, _history, _fileOps, resolved));
        }
        if (Tabs.Count == 0) AddTab();
        else ActiveTab = Tabs[Math.Clamp(activeIndex, 0, Tabs.Count - 1)];
    }

    [RelayCommand]
    private void NewTab() => AddTab();

    [RelayCommand]
    private void SelectTab(TabViewModel tab) => ActiveTab = tab;

    [RelayCommand]
    private void CloseTab(TabViewModel tab)
    {
        if (Tabs.Count <= 1) return;
        tab.Cleanup();
        var idx = Tabs.IndexOf(tab);
        Tabs.Remove(tab);
        if (ActiveTab == tab)
            ActiveTab = Tabs[Math.Max(0, Math.Min(idx, Tabs.Count - 1))];
    }

    partial void OnActiveTabChanged(TabViewModel? oldValue, TabViewModel? newValue)
    {
        if (oldValue != null)
        {
            oldValue.IsActive = false;
            oldValue.PropertyChanged -= ForwardTabPropertyChanged;
        }
        if (newValue != null)
        {
            newValue.IsActive = true;
            newValue.PropertyChanged += ForwardTabPropertyChanged;
        }
        RefreshForwardedProperties();
    }

    private void ForwardTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TabViewModel.ItemCountText)
                           or nameof(TabViewModel.SelectionText)
                           or nameof(TabViewModel.FreeSpaceText))
            RefreshForwardedProperties();
    }

    private void RefreshForwardedProperties()
    {
        OnPropertyChanged(nameof(ItemCountText));
        OnPropertyChanged(nameof(SelectionText));
        OnPropertyChanged(nameof(FreeSpaceText));
    }
}
