using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using Zephyr.Core.Models;
using Zephyr.Core.Security;
using Zephyr.Core.Settings;
using Zephyr.UI.Services;

namespace Zephyr.UI;

public partial class App : Application
{
    [DllImport("uxtheme.dll", EntryPoint = "#135", CharSet = CharSet.Unicode)]
    private static extern int SetPreferredAppMode(int mode);
    [DllImport("uxtheme.dll", EntryPoint = "#136")]
    private static extern void FlushMenuThemes();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmd);

    private static Mutex? _instanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        // IFEO invocation: args arrive as ["C:\Windows\explorer.exe", optional path/flags]
        // Strip the explorer.exe prefix so we get the real target path (if any)
        var args = e.Args.ToList();
        if (args.Count > 0 && args[0].EndsWith("explorer.exe", StringComparison.OrdinalIgnoreCase))
            args.RemoveAt(0);

        // --new-window is passed by drag-out tab; skip single-instance check so the
        // second window actually opens instead of being redirected to the first.
        bool forceNewWindow = args.Remove("--new-window");

        if (!forceNewWindow)
        {
            // Single-instance guard: if Zephyr is already running, focus it and exit
            _instanceMutex = new Mutex(true, "ZephyrFileManagerSingleInstance", out bool isFirst);
            if (!isFirst)
            {
                var me = Process.GetCurrentProcess();
                var other = Process.GetProcessesByName(me.ProcessName)
                    .FirstOrDefault(p => p.Id != me.Id);
                if (other != null)
                {
                    ShowWindow(other.MainWindowHandle, 9); // SW_RESTORE
                    SetForegroundWindow(other.MainWindowHandle);
                }
                Environment.Exit(0);
                return;
            }
        }

        string? startPath = args.FirstOrDefault(a => Directory.Exists(a));

        SettingsService.Load();
        FileItem.ShowExtensions = SettingsService.Current.ShowFileExtensions;
        FolderLockService.Load(SettingsService.Current.LockedFolders);
        RecentInteractionService.Load();
        FileLabelService.Load();
        base.OnStartup(e);
        new ThemeService().Apply(this, SettingsService.Current.ThemeMode);

        int appMode = SettingsService.Current.ThemeMode switch
        {
            "Dark"  => 2,
            "Light" => 3,
            _       => 1,
        };
        SetPreferredAppMode(appMode);
        FlushMenuThemes();

        var mainWindow = new MainWindow(startPath);
        if (SettingsService.Current.LaunchMaximized)
            mainWindow.WindowState = WindowState.Maximized;

        mainWindow.Show();
    }
}
