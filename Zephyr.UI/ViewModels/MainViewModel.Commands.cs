using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using Zephyr.Core.Archives;
using Zephyr.Core.Settings;
using Zephyr.UI.Dialogs;
using Zephyr.UI.Services;
using Zephyr.UI.Windows;

namespace Zephyr.UI.ViewModels;

// The central command registry that feeds the customizable toolbar and the hotkey map,
// plus the Settings dialog and the Ctrl+P command palette built from those commands.
public partial class MainViewModel
{
    /// <summary>Central command registry (toolbar + hotkeys). Built once in the constructor.</summary>
    public List<AppCommand> AppCommands { get; } = [];

    /// <summary>The commands currently shown on the customizable toolbar, in order.</summary>
    public ObservableCollection<AppCommand> ToolbarItems { get; } = [];

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
        Reg("properties",   "Properties",    "", ShowPropertiesCommand,  "Alt+Return");
        Reg("refresh",      "Refresh",       "", RefreshViewCommand,     "F5");
        Reg("close-tab",    "Close Tab",     "", CloseActiveTabCommand,  "Ctrl+W");
        Reg("focus-search", "Focus Search",  "", FocusSearchCommand,     "Ctrl+F");
        Reg("nav-back",     "Back",          "", NavigateBackCommand,    "Alt+Left");
        Reg("nav-forward",  "Forward",       "", NavigateForwardCommand, "Alt+Right");
        Reg("nav-up",       "Up One Level",  "", NavigateUpCommand,      "Alt+Up");
        Reg("delete-permanent", "Delete Permanently", "", PermanentDeleteCommand,  "Shift+Delete");
        Reg("command-palette",  "Command Palette",    "", OpenCommandPaletteCommand, "Ctrl+P");
    }

    // ── Shortcut-only actions ─────────────────────────────────────────────────
    // Thin wrappers so tab/pane-scoped operations can be bound at the window level
    // (and rebound from Settings) without the hotkey layer knowing about panes.

    [RelayCommand]
    private void ShowProperties()
    {
        if (ActiveTab is not { } tab) return;
        var path = tab.SelectedItem?.FullPath ?? tab.CurrentPath;
        if (string.IsNullOrEmpty(path)) return;
        // Virtual paths have no file-system identity to describe.
        if (ArchivePath.IsArchivePath(path) || WpdProvider.IsWpdPath(path)) return;

        new FilePropertiesWindow(path) { Owner = Application.Current.MainWindow }.ShowDialog();
    }

    [RelayCommand]
    private void RefreshView() => ActiveTab?.Reload();

    [RelayCommand]
    private void CloseActiveTab()
    {
        if (ActivePane.ActiveTab is { } tab) ActivePane.CloseTabCommand.Execute(tab);
    }

    [RelayCommand]
    private void FocusSearch()
    {
        if (Application.Current.MainWindow is MainWindow mw) mw.FocusActiveSearchBox();
    }

    [RelayCommand] private void NavigateBack()    => Invoke(ActiveTab?.GoBackCommand);
    [RelayCommand] private void NavigateForward() => Invoke(ActiveTab?.GoForwardCommand);
    [RelayCommand] private void NavigateUp()      => Invoke(ActiveTab?.GoUpCommand);

    private static void Invoke(IRelayCommand? cmd)
    {
        if (cmd?.CanExecute(null) == true) cmd.Execute(null);
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
            Cmd("Properties",         "", ShowPropertiesCommand,    "Alt+Enter"),
            Cmd("Refresh",            "", RefreshViewCommand,       "F5"),
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
}
