using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Microsoft.Win32;
using Zephyr.Core.Settings;
using Zephyr.UI.Services;

namespace Zephyr.UI.Windows;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        NavList.SelectedIndex = 0;
        LoadCurrentSettings();
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
        AdvancedPage.Visibility   = idx == 2 ? Visibility.Visible : Visibility.Collapsed;
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
