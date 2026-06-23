using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Microsoft.Win32;
using Zephyr.Core.Settings;
using Zephyr.UI.Dialogs;
using Zephyr.UI.Services;
using Zephyr.UI.ViewModels;

namespace Zephyr.UI.Windows;

public partial class SettingsWindow : Window
{
    private readonly MainViewModel? _vm;
    private ObservableCollection<AppCommand>? _toolbarEdit;

    public SettingsWindow(MainViewModel? vm = null)
    {
        InitializeComponent();
        _vm = vm;
        NavList.SelectedIndex = 0;
        LoadCurrentSettings();
        if (_vm is not null) { BuildHotkeyList(); BuildToolbarEditor(); }
        SourceInitialized += (_, _) => ApplyDarkTitleBar();
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private void ApplyDarkTitleBar()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        int val = 1;
        DwmSetWindowAttribute(hwnd, 20, ref val, sizeof(int));
    }

    private void LoadCurrentSettings()
    {
        var s = SettingsService.Current;
        ShowHiddenCheck.IsChecked               = s.ShowHiddenFiles;
        ShowSystemCheck.IsChecked               = s.ShowSystemFiles;
        ShowSystemCheck.IsEnabled               = s.ShowHiddenFiles;
        ShowFileExtensionsCheck.IsChecked       = s.ShowFileExtensions;
        ShowRecentlyInteractedCheck.IsChecked   = s.ShowRecentlyInteracted;
        SortByRecentlyInteractedCheck.IsChecked = s.SortByRecentlyInteracted;
        ShowFolderSizesCheck.IsChecked          = s.ShowFolderSizes;
        ShowCloudBadgesCheck.IsChecked          = s.ShowCloudBadges;
        LaunchMaximizedCheck.IsChecked          = s.LaunchMaximized;
        StartupPathBox.Text                     = s.StartupPath;

        ThemeAuto.IsChecked  = s.ThemeMode is not "Dark" and not "Light";
        ThemeDark.IsChecked  = s.ThemeMode == "Dark";
        ThemeLight.IsChecked = s.ThemeMode == "Light";

        CaptureWinECheck.IsChecked = s.CaptureWinE;
        StartupCheck.IsChecked     = ShellIntegrationService.IsLaunchAtStartup();
        UpdateContextMenuStatus();
        UpdatePortableModeStatus();
        UpdateDefaultFMStatus();
    }

    private void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GeneralPage is null) return;
        var idx = NavList.SelectedIndex;
        GeneralPage.Visibility    = idx == 0 ? Visibility.Visible : Visibility.Collapsed;
        AppearancePage.Visibility = idx == 1 ? Visibility.Visible : Visibility.Collapsed;
        ShortcutsPage.Visibility  = idx == 2 ? Visibility.Visible : Visibility.Collapsed;
        ToolbarPage.Visibility    = idx == 3 ? Visibility.Visible : Visibility.Collapsed;
        AdvancedPage.Visibility   = idx == 4 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── Shortcuts (rebindable hotkeys) ──────────────────────────────────────────

    private void BuildHotkeyList()
    {
        HotkeyList.ItemsSource = new ObservableCollection<HotkeyRow>(
            _vm!.AppCommands.Select(c => new HotkeyRow(c)));
    }

    private void ChangeHotkey_Click(object sender, RoutedEventArgs e)
    {
        if (_vm is null || (sender as Button)?.Tag is not HotkeyRow row) return;

        var dlg = new HotkeyCaptureDialog(row.Command.Name, row.GestureDisplay) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        var gesture = dlg.Gesture; // canonical, "" = no shortcut
        if (gesture.Length > 0)
        {
            var clash = _vm.AppCommands.FirstOrDefault(c =>
                c.Id != row.Command.Id &&
                string.Equals(HotkeyService.EffectiveGesture(c), gesture, StringComparison.OrdinalIgnoreCase));
            if (clash is not null)
            {
                ZephyrMessageBox.Show(
                    $"{HotkeyService.ToDisplay(gesture)} is already used by “{clash.Name}”.",
                    "Shortcut in use", this);
                return;
            }
        }

        // Equal to the default → drop the override; otherwise store it.
        if (string.Equals(gesture, row.Command.DefaultGesture, StringComparison.OrdinalIgnoreCase))
            SettingsService.Current.Hotkeys.Remove(row.Command.Id);
        else
            SettingsService.Current.Hotkeys[row.Command.Id] = gesture;

        PersistAndReapplyHotkeys();
        row.Refresh();
    }

    private void ResetHotkey_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not HotkeyRow row) return;
        SettingsService.Current.Hotkeys.Remove(row.Command.Id);
        PersistAndReapplyHotkeys();
        row.Refresh();
    }

    private void ResetAllHotkeys_Click(object sender, RoutedEventArgs e)
    {
        SettingsService.Current.Hotkeys.Clear();
        PersistAndReapplyHotkeys();
        BuildHotkeyList();
    }

    private void PersistAndReapplyHotkeys()
    {
        SettingsService.Save(SettingsService.Current);
        if (Application.Current.MainWindow is MainWindow mw) mw.ApplyHotkeys();
    }

    // ── Toolbar customization ───────────────────────────────────────────────────

    private void BuildToolbarEditor()
    {
        _toolbarEdit = new ObservableCollection<AppCommand>(_vm!.ToolbarItems);
        ToolbarOrderList.ItemsSource = _toolbarEdit;
        RefreshAddCombo();
    }

    private void RefreshAddCombo()
    {
        if (_vm is null || _toolbarEdit is null) return;
        ToolbarAddCombo.ItemsSource = _vm.AppCommands
            .Where(c => c.ToolbarEligible && !_toolbarEdit.Contains(c))
            .ToList();
    }

    private void ToolbarUp_Click(object sender, RoutedEventArgs e)
    {
        int i = ToolbarOrderList.SelectedIndex;
        if (_toolbarEdit is null || i <= 0) return;
        _toolbarEdit.Move(i, i - 1);
        ToolbarOrderList.SelectedIndex = i - 1;
        PersistToolbar();
    }

    private void ToolbarDown_Click(object sender, RoutedEventArgs e)
    {
        int i = ToolbarOrderList.SelectedIndex;
        if (_toolbarEdit is null || i < 0 || i >= _toolbarEdit.Count - 1) return;
        _toolbarEdit.Move(i, i + 1);
        ToolbarOrderList.SelectedIndex = i + 1;
        PersistToolbar();
    }

    private void ToolbarRemove_Click(object sender, RoutedEventArgs e)
    {
        if (_toolbarEdit is null || ToolbarOrderList.SelectedItem is not AppCommand c) return;
        _toolbarEdit.Remove(c);
        PersistToolbar();
        RefreshAddCombo();
    }

    private void ToolbarAdd_Click(object sender, RoutedEventArgs e)
    {
        if (_toolbarEdit is null || ToolbarAddCombo.SelectedItem is not AppCommand c) return;
        _toolbarEdit.Add(c);
        PersistToolbar();
        RefreshAddCombo();
    }

    private void ToolbarReset_Click(object sender, RoutedEventArgs e)
    {
        SettingsService.Current.Toolbar = [];
        SettingsService.Save(SettingsService.Current);
        _vm!.RebuildToolbar();
        BuildToolbarEditor();
    }

    private void PersistToolbar()
    {
        if (_toolbarEdit is null) return;
        SettingsService.Current.Toolbar = _toolbarEdit.Select(c => c.Id).ToList();
        SettingsService.Save(SettingsService.Current);
        _vm!.RebuildToolbar();
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog
        {
            Title           = "Select default startup folder",
            InitialDirectory = StartupPathBox.Text
        };
        if (dlg.ShowDialog() == true)
            StartupPathBox.Text = dlg.FolderName;
    }

    private void ContextMenu_Click(object sender, RoutedEventArgs e)
    {
        if (ShellIntegrationService.IsRegistered())
            ShellIntegrationService.Unregister();
        else
            ShellIntegrationService.Register();
        UpdateContextMenuStatus();
    }

    private void PortableMode_Click(object sender, RoutedEventArgs e)
    {
        if (SettingsService.IsPortableMode)
            SettingsService.DisablePortableMode();
        else
            SettingsService.EnablePortableMode();
        UpdatePortableModeStatus();
    }

    private void UpdateContextMenuStatus()
    {
        var reg = ShellIntegrationService.IsRegistered();
        ContextMenuBtn.Content  = reg ? "Remove from Context Menu" : "Add to Context Menu";
        ContextMenuStatus.Text  = reg ? "Registered" : "Not registered";
    }

    private void UpdateDefaultFMStatus()
    {
        var set = ShellIntegrationService.IsDefaultFileManager();
        DefaultFMBtn.Content    = set ? "Remove as Default" : "Set as Default File Manager";
        DefaultFMStatus.Text    = set ? "Registered" : "Not registered";
    }

    private void DefaultFM_Click(object sender, RoutedEventArgs e)
    {
        if (ShellIntegrationService.IsDefaultFileManager())
            ShellIntegrationService.RemoveDefaultFileManager();
        else
            ShellIntegrationService.SetAsDefaultFileManager();
        UpdateDefaultFMStatus();
    }

    private void CaptureWinE_Click(object sender, RoutedEventArgs e)
    {
        var capture = CaptureWinECheck.IsChecked == true;
        SettingsService.Current.CaptureWinE = capture;
        SettingsService.Save(SettingsService.Current);
        // In-process hook: works while Zephyr is running
        if (Application.Current.MainWindow is MainWindow mw)
            mw.UpdateWinECapture(capture);
        // Background helper: works even when Zephyr is closed
        if (capture)
            ShellIntegrationService.InstallWinEHelper();
        else
            ShellIntegrationService.UninstallWinEHelper();
    }

    private void Startup_Click(object sender, RoutedEventArgs e)
        => ShellIntegrationService.SetLaunchAtStartup(StartupCheck.IsChecked == true);

    private void UpdatePortableModeStatus()
    {
        var portable = SettingsService.IsPortableMode;
        PortableModeBtn.Content = portable ? "Disable Portable Mode" : "Enable Portable Mode";
        PortablePathText.Text   = portable
            ? Path.Combine(AppContext.BaseDirectory, "settings.json")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Zephyr", "settings.json");
    }

    private void ShowHidden_Changed(object sender, RoutedEventArgs e)
    {
        var show = ShowHiddenCheck.IsChecked == true;
        ShowSystemCheck.IsChecked = show;
        ShowSystemCheck.IsEnabled = show;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var themeMode = ThemeDark.IsChecked  == true ? "Dark"
                      : ThemeLight.IsChecked == true ? "Light" : "Auto";
        var s = SettingsService.Current;
        s.ShowHiddenFiles          = ShowHiddenCheck.IsChecked               == true;
        s.ShowSystemFiles          = ShowSystemCheck.IsChecked               == true;
        s.ShowFileExtensions       = ShowFileExtensionsCheck.IsChecked       == true;
        s.ShowRecentlyInteracted   = ShowRecentlyInteractedCheck.IsChecked   == true;
        s.SortByRecentlyInteracted = SortByRecentlyInteractedCheck.IsChecked == true;
        s.ShowFolderSizes          = ShowFolderSizesCheck.IsChecked          == true;
        s.ShowCloudBadges          = ShowCloudBadgesCheck.IsChecked          == true;
        s.LaunchMaximized          = LaunchMaximizedCheck.IsChecked          == true;
        s.StartupPath              = StartupPathBox.Text.Trim();
        s.ThemeMode                = themeMode;
        s.CaptureWinE              = CaptureWinECheck.IsChecked              == true;
        SettingsService.Save(s);
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}

/// <summary>Row in the Shortcuts list — wraps a command and shows its current gesture.</summary>
public sealed class HotkeyRow : INotifyPropertyChanged
{
    public AppCommand Command { get; }
    public string Name => Command.Name;
    public string GestureDisplay => HotkeyService.ToDisplay(HotkeyService.EffectiveGesture(Command));

    public HotkeyRow(AppCommand command) => Command = command;

    public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(GestureDisplay)));

    public event PropertyChangedEventHandler? PropertyChanged;
}
