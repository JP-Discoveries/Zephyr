using Zephyr.Core.Models;
using Zephyr.Core.Security;

namespace Zephyr.Core.Settings;

public class PaneSession
{
    public List<string> TabPaths    { get; set; } = [];
    public int          ActiveIndex { get; set; } = 0;
}

public class ZephyrSettings
{
    public bool   ShowHiddenFiles        { get; set; } = false;
    public bool   ShowSystemFiles        { get; set; } = false;
    public bool   ShowFileExtensions     { get; set; } = true;
    public bool   ShowRecentlyInteracted { get; set; } = false;
    public bool   SortByRecentlyInteracted { get; set; } = false;
    public string ThemeMode              { get; set; } = "Auto";   // Auto | Dark | Light
    public bool   LaunchMaximized        { get; set; } = false;
    public string StartupPath            { get; set; } = string.Empty;
    public bool   CaptureWinE            { get; set; } = false;
    public bool   ShowFolderSizes        { get; set; } = false;
    public bool   ShowCloudBadges        { get; set; } = false;
    public List<BookmarkItem>   Bookmarks      { get; set; } = [];
    public List<LockedFolder>   LockedFolders  { get; set; } = [];
    public List<BookmarkItem>   NetworkPins    { get; set; } = [];
    public bool   BookmarksCollapsed   { get; set; } = false;
    public bool   DrivesCollapsed      { get; set; } = false;
    public bool   DevicesCollapsed     { get; set; } = false;
    public bool   RecentFilesCollapsed { get; set; } = false;
    public bool   NetworkCollapsed     { get; set; } = false;
    public PaneSession? LeftPaneSession  { get; set; }
    public PaneSession? RightPaneSession { get; set; }
    public bool         LastSplitView    { get; set; } = false;

    /// <summary>Command id → canonical key gesture (e.g. "Ctrl+Shift+N"). Overrides defaults only.</summary>
    public Dictionary<string, string> Hotkeys { get; set; } = new();

    /// <summary>Ordered command ids shown on the customizable toolbar. Empty = default set.</summary>
    public List<string> Toolbar { get; set; } = [];
}
