using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Zephyr.Core.FileSystem;
using Zephyr.Core.Models;
using Zephyr.Core.Settings;
using Zephyr.UI.Dialogs;
using Zephyr.UI.Services;
using Zephyr.UI.ViewModels;
using static Zephyr.UI.Services.WpdProvider;

namespace Zephyr.UI;

public partial class MainWindow : Window
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private const uint MOD_WIN      = 0x0008;
    private const uint MOD_NOREPEAT = 0x4000;
    private const uint VK_E         = 0x45;
    private const int  HOTKEY_WIN_E = 1;
    private const int  WM_HOTKEY    = 0x0312;

    private const int  WM_DEVICECHANGE          = 0x0219;
    private const int  DBT_DEVNODES_CHANGED     = 0x0007;
    private const int  DBT_DEVICEARRIVAL        = 0x8000;
    private const int  DBT_DEVICEREMOVECOMPLETE = 0x8004;

    private bool _hotkeyRegistered = false;

    public MainWindow(string? startPath = null)
    {
        InitializeComponent();
        DataContext = new MainViewModel(new FileSystemService(), startPath);
        if (DataContext is MainViewModel vm)
            vm.PropertyChanged += OnVmPropertyChanged;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        ApplyHotkeys();
        if (VM is { IsSplitView: true } vm)
            ApplyPaneOpacities(vm, animate: false);
    }

    /// <summary>Rebuilds the window's key bindings from the command registry + user overrides.</summary>
    public void ApplyHotkeys()
    {
        if (VM is not { } vm) return;
        InputBindings.Clear();

        // Fixed extras that aren't user-rebindable.
        InputBindings.Add(new KeyBinding(vm.ClearClipboardCommand, Key.Escape, ModifierKeys.None));
        InputBindings.Add(new KeyBinding(vm.OpenCommandPaletteCommand, Key.P, ModifierKeys.Control | ModifierKeys.Shift));

        foreach (var cmd in vm.AppCommands)
            if (HotkeyService.TryParse(HotkeyService.EffectiveGesture(cmd), out var key, out var mods))
                InputBindings.Add(new KeyBinding(cmd.Command, key, mods));
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not MainViewModel vm) return;

        if (e.PropertyName == nameof(MainViewModel.ActivePane) && vm.IsSplitView)
        {
            ApplyPaneOpacities(vm);
            return;
        }

        if (e.PropertyName == nameof(MainViewModel.IsSidebarVisible))
        {
            bool visible = vm.IsSidebarVisible;
            Dispatcher.BeginInvoke(() =>
            {
                var cols = ContentGrid.ColumnDefinitions;
                if (visible)
                {
                    cols[0].MinWidth = 120;
                    cols[0].Width    = new GridLength(175);
                    cols[1].Width    = new GridLength(4);
                }
                else
                {
                    cols[0].MinWidth = 0;
                    cols[0].Width    = new GridLength(0);
                    cols[1].Width    = new GridLength(0);
                }
            }, System.Windows.Threading.DispatcherPriority.Render);
            return;
        }

        if (e.PropertyName != nameof(MainViewModel.IsSplitView)) return;

        bool split = vm.IsSplitView;
        if (split)
            ApplyPaneOpacities(vm, animate: false);
        else
            ResetPaneOpacities();

        Dispatcher.BeginInvoke(() =>
        {
            var cols = ContentGrid.ColumnDefinitions;
            cols[2].Width = new GridLength(1, GridUnitType.Star);
            cols[4].Width = split ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        }, System.Windows.Threading.DispatcherPriority.Render);
    }

    private void ApplyPaneOpacities(MainViewModel vm, bool animate = true)
    {
        bool leftActive = vm.ActivePane == vm.LeftPane;
        SetPaneOpacity(LeftPaneBorder,  leftActive ? 1.0 : 0.5, animate);
        SetPaneOpacity(RightPaneBorder, leftActive ? 0.5 : 1.0, animate);
    }

    private void ResetPaneOpacities()
    {
        LeftPaneBorder.BeginAnimation(UIElement.OpacityProperty, null);
        RightPaneBorder.BeginAnimation(UIElement.OpacityProperty, null);
        LeftPaneBorder.Opacity  = 1.0;
        RightPaneBorder.Opacity = 1.0;
    }

    private static void SetPaneOpacity(Border pane, double to, bool animate)
    {
        if (!animate)
        {
            pane.BeginAnimation(UIElement.OpacityProperty, null);
            pane.Opacity = to;
            return;
        }
        pane.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation
        {
            To           = to,
            Duration     = new Duration(TimeSpan.FromSeconds(0.2)),
            FillBehavior = FillBehavior.HoldEnd,
        });
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyDarkTitleBar();
        HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WndProc);
        UpdateWinECapture(SettingsService.Current.CaptureWinE);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HOTKEY_WIN_E)
        {
            if (WindowState == WindowState.Minimized)
                WindowState = WindowState.Normal;
            Show();
            Activate();
            handled = true;
        }
        else if (msg == WM_DEVICECHANGE)
        {
            int wp = wParam.ToInt32();
            if (wp == DBT_DEVNODES_CHANGED || wp == DBT_DEVICEARRIVAL || wp == DBT_DEVICEREMOVECOMPLETE)
                (DataContext as MainViewModel)?.RefreshDrives();
        }
        return IntPtr.Zero;
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (DataContext is MainViewModel vm)
        {
            vm.SaveSession();
            vm.Cleanup();
        }
        RecentInteractionService.Save();
        if (_hotkeyRegistered)
            UnregisterHotKey(new WindowInteropHelper(this).Handle, HOTKEY_WIN_E);
    }

    public void UpdateWinECapture(bool capture)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        if (capture && !_hotkeyRegistered)
            _hotkeyRegistered = RegisterHotKey(hwnd, HOTKEY_WIN_E, MOD_WIN | MOD_NOREPEAT, VK_E);
        else if (!capture && _hotkeyRegistered)
        {
            UnregisterHotKey(hwnd, HOTKEY_WIN_E);
            _hotkeyRegistered = false;
        }
    }

    public void ApplyDarkTitleBar()
    {
        var mode = SettingsService.Current.ThemeMode;
        bool dark = mode == "Dark" || (mode != "Light" && new ThemeService().IsDarkMode());
        int val = dark ? 1 : 0;
        var hwnd = new WindowInteropHelper(this).Handle;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref val, Marshal.SizeOf(val));
    }

    private MainViewModel? VM => DataContext as MainViewModel;

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Delete && Keyboard.Modifiers == ModifierKeys.Shift
            && Keyboard.FocusedElement is not System.Windows.Controls.Primitives.TextBoxBase)
        {
            e.Handled = true;
            VM?.PermanentDeleteCommand.Execute(null);
            return;
        }
        base.OnPreviewKeyDown(e);
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (VM?.ActivePane?.ActiveTab is not { } tab) return;
        if (e.ChangedButton == MouseButton.XButton1 && tab.CanGoBack)
        {
            tab.GoBackCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.ChangedButton == MouseButton.XButton2 && tab.CanGoForward)
        {
            tab.GoForwardCommand.Execute(null);
            e.Handled = true;
        }
    }

    // ── Bookmark drag-reorder ─────────────────────────────────────────────────

    private Point         _bookmarkDragStart;
    private BookmarkItem? _bookmarkDragItem;

    private void BookmarkList_DragInit(object sender, MouseButtonEventArgs e)
        => _bookmarkDragStart = e.GetPosition(null);

    private void BookmarkList_DragMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _bookmarkDragItem != null) return;

        var diff = _bookmarkDragStart - e.GetPosition(null);
        if (Math.Abs(diff.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(diff.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        var item = HitTestBookmark(e.OriginalSource as DependencyObject);
        if (item == null) return;

        _bookmarkDragItem = item;
        var data = new DataObject("ZephyrBookmark", item);
        DragDrop.DoDragDrop(BookmarksListView, data, DragDropEffects.Move);
        _bookmarkDragItem = null;
    }

    private void BookmarkList_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent("ZephyrBookmark") ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void BookmarkList_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent("ZephyrBookmark")) return;
        if (e.Data.GetData("ZephyrBookmark") is not BookmarkItem dragged) return;
        if (VM is not { } vm) return;

        var target = HitTestBookmark(e.OriginalSource as DependencyObject);
        if (target == null || target == dragged) return;

        var from = vm.Bookmarks.IndexOf(dragged);
        var to   = vm.Bookmarks.IndexOf(target);
        vm.MoveBookmark(from, to);
        e.Handled = true;
    }

    private static BookmarkItem? HitTestBookmark(DependencyObject? source)
    {
        var el = source;
        while (el != null && el is not ListViewItem)
            el = VisualTreeHelper.GetParent(el);
        return (el as FrameworkElement)?.DataContext as BookmarkItem;
    }

    // ── Sidebar click handlers ────────────────────────────────────────────────

    private void SidebarListView_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        SidebarScrollViewer.ScrollToVerticalOffset(SidebarScrollViewer.VerticalOffset - e.Delta);
    }

    private void Bookmark_Click(object sender, MouseButtonEventArgs e)
    {
        if (VM is { } vm && ((ListViewItem)sender).DataContext is BookmarkItem bookmark)
            vm.ActivePane.ActiveTab?.Navigate(bookmark.Path);
    }

    private void Bookmark_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (VM is not { } vm || ((ListViewItem)sender).DataContext is not BookmarkItem bookmark) return;

        var sep  = new Separator { Style = (Style)FindResource("MenuSep") };
        var menu = new ContextMenu();

        var miRemove = new MenuItem { Header = "Remove Bookmark" };
        miRemove.Click += (_, _) => vm.RemoveBookmark(bookmark);

        var miRename = new MenuItem { Header = "Rename…" };
        miRename.Click += (_, _) =>
        {
            var dlg = new InputDialog("Rename Bookmark", "New name:", bookmark.Name) { Owner = this };
            if (dlg.ShowDialog() == true)
                vm.RenameBookmark(bookmark, dlg.Result);
        };

        menu.Items.Add(miRemove);
        menu.Items.Add(sep);
        menu.Items.Add(miRename);
        menu.PlacementTarget = (ListViewItem)sender;
        menu.Placement       = PlacementMode.MousePoint;
        menu.IsOpen          = true;
        e.Handled            = true;
    }

    private void NetworkLocation_Click(object sender, MouseButtonEventArgs e)
    {
        if (VM is { } vm && ((ListViewItem)sender).DataContext is NetworkLocation loc)
            vm.ActivePane.ActiveTab?.Navigate(loc.Path);
    }

    private void NetworkLocation_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (VM is not { } vm || ((ListViewItem)sender).DataContext is not NetworkLocation loc) return;

        var menu = new ContextMenu();
        var miOpen = new MenuItem { Header = "Open" };
        miOpen.Click += (_, _) => vm.ActivePane.ActiveTab?.Navigate(loc.Path);
        menu.Items.Add(miOpen);

        if (loc.IsRemovable)
        {
            var miCopy = new MenuItem { Header = "Copy Path" };
            miCopy.Click += (_, _) => { try { Clipboard.SetText(loc.Path); } catch { } };
            menu.Items.Add(miCopy);

            menu.Items.Add(new Separator { Style = (Style)FindResource("MenuSep") });
            var miRemove = new MenuItem { Header = "Remove Pin" };
            miRemove.Click += (_, _) => vm.RemoveNetworkLocation(loc);
            menu.Items.Add(miRemove);
        }

        menu.PlacementTarget = (ListViewItem)sender;
        menu.Placement       = PlacementMode.MousePoint;
        menu.IsOpen          = true;
        e.Handled            = true;
    }

    private void DrivesHeader_Click(object sender, MouseButtonEventArgs e)
        => VM?.ActivePane.ActiveTab?.Navigate(TabViewModel.ThisPcPath);

    private void Drive_Click(object sender, MouseButtonEventArgs e)
    {
        if (VM is { } vm && ((ListViewItem)sender).DataContext is DriveItem drive)
            vm.ActivePane.ActiveTab?.Navigate(drive.Name);
    }

    private void Device_Click(object sender, MouseButtonEventArgs e)
    {
        if (VM is not { } vm || ((ListViewItem)sender).DataContext is not DriveItem device) return;

        if (WpdProvider.IsWpdPath(device.Name) || System.IO.Directory.Exists(device.Name))
            vm.ActivePane.ActiveTab?.Navigate(device.Name);
    }

    private void Recent_Click(object sender, MouseButtonEventArgs e)
    {
        if (VM is { } vm && ((ListViewItem)sender).DataContext is string path)
            vm.ActivePane.ActiveTab?.Navigate(path);
    }

    private void RecentFile_Click(object sender, MouseButtonEventArgs e)
    {
        if (VM is { } vm && ((ListViewItem)sender).DataContext is Zephyr.Core.Models.RecentFileItem file)
            vm.ActivePane.ActiveTab?.OpenFile(file.FullPath);
    }

    private void LeftPane_GotFocus(object sender, RoutedEventArgs e)
    {
        if (VM is { } vm) vm.SetActivePane(vm.LeftPane);
    }

    private void RightPane_GotFocus(object sender, RoutedEventArgs e)
    {
        if (VM is { } vm) vm.SetActivePane(vm.RightPane);
    }

    private void LeftPane_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (VM is { } vm && vm.ActivePane != vm.LeftPane) vm.SetActivePane(vm.LeftPane);
    }

    private void RightPane_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (VM is { } vm && vm.ActivePane != vm.RightPane) vm.SetActivePane(vm.RightPane);
    }
}
