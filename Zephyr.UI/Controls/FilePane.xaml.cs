using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Zephyr.Core.Archives;
using Zephyr.Core.Models;
using Zephyr.Core.Security;
using Zephyr.UI.Dialogs;
using Zephyr.UI.Services;
using Zephyr.UI.ViewModels;

namespace Zephyr.UI.Controls;

public partial class FilePane : UserControl
{
    private GridViewColumnHeader? _lastSortHeader;
    private Point                 _dragStart;
    private bool                  _suppressSelectionReset;
    private ListBox?              _suppressedList;

    private static readonly HashSet<string> _elevatableExts = new(StringComparer.OrdinalIgnoreCase)
        { ".exe", ".msi", ".bat", ".cmd", ".com", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".ps1" };

    // ── Column management ─────────────────────────────────────────────────────
    private readonly List<string>    _columnOrder          = ["Name", "Size", "Date Modified", "Date Created", "Type", "Extension", "Attributes", "Contents"];
    private readonly HashSet<string> _hiddenColumns        = [];
    private readonly HashSet<string> _defaultHiddenColumns = ["Date Created", "Extension", "Attributes", "Contents"];

    // Columns removed by user (kept so they can be re-inserted at the right position)
    private readonly Dictionary<string, GridViewColumn> _removedColumns = [];

    private const double MaxSizeWidth   = 120;
    private const double MaxDateWidth   = 160;
    private const double MaxTypeWidth   = 140;
    private const double MinColumnWidth = 40;   // non-Name columns never shrink below this


    // ── Tab drag ──────────────────────────────────────────────────────────────
    private Point            _tabDragStart;
    private TabViewModel?    _tabDragItem;
    private bool             _tabIsDragging;
    private DispatcherTimer? _tabDragTimer;

    [DllImport("user32.dll")] private static extern bool   GetCursorPos(out Win32Point pt);
    [DllImport("user32.dll")] private static extern short  GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")] private static extern IntPtr SetWindowsHookEx(int id, LowLevelMouseProc fn, IntPtr hMod, uint threadId);
    [DllImport("user32.dll")] private static extern bool   UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll", CharSet = CharSet.Auto)] private static extern IntPtr GetModuleHandle(string? name);

    [StructLayout(LayoutKind.Sequential)] private struct Win32Point { public int X, Y; }
    private const int VK_LBUTTON  = 0x01;
    private const int WH_MOUSE_LL = 14;
    private const int WM_LBUTTONUP_MSG = 0x0202;

    private delegate IntPtr LowLevelMouseProc(int code, IntPtr w, IntPtr l);
    private LowLevelMouseProc? _mouseHookProc;   // must hold reference to prevent GC
    private IntPtr              _mouseHook;

    // ── Tab/pane observation (drives AutoSizeColumns) ─────────────────────────
    private TabViewModel? _observedTab;

    public FilePane()
    {
        InitializeComponent();
        FileList.AddHandler(GridViewColumnHeader.ClickEvent,
            new RoutedEventHandler(ColumnHeader_Click));
        Loaded   += OnLoaded;
        Unloaded += OnUnloaded;
        // Only re-size when the pane width changes (not when a scrollbar appears/disappears)
        FileList.SizeChanged += (_, e) =>
        {
            if (e.WidthChanged)
                Dispatcher.InvokeAsync(AutoSizeColumns, DispatcherPriority.Background);
        };
        SizeChanged += (_, e) =>
        {
            if (e.WidthChanged && ActualWidth > 10)
                Tab?.SetPaneWidth(ActualWidth);
        };
        // Column width monitoring is set up in OnLoaded after initial AutoSizeColumns.
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (Pane is { } pane)
        {
            pane.PropertyChanged += OnPanePropertyChanged;
            SubscribeToTab(pane.ActiveTab);
        }
        if (FileList.View is GridView gv)
        {
            foreach (var name in _defaultHiddenColumns)
                HideColumnSilent(name, gv);
        }
        AutoSizeColumns();
        SubscribeColumnWidths();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ExitAddressBar();
        if (Pane is { } pane) pane.PropertyChanged -= OnPanePropertyChanged;
        if (_observedTab is { } tab) tab.PropertyChanged -= OnTabPropertyChanged;
        if (_tabIsDragging) AbortTabDrag(); else StopDragTimer();
        Mouse.Capture(null);
        UnsubscribeColumnWidths();
    }

    private void OnPanePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PaneViewModel.ActiveTab) && Pane is { } pane)
            SubscribeToTab(pane.ActiveTab);
    }

    private void SubscribeToTab(TabViewModel? tab)
    {
        if (_observedTab != null)
        {
            _observedTab.PropertyChanged -= OnTabPropertyChanged;
            _observedTab.Items.CollectionChanged -= OnItemsChanged;
        }
        _observedTab = tab;
        if (tab != null)
        {
            tab.PropertyChanged += OnTabPropertyChanged;
            tab.Items.CollectionChanged += OnItemsChanged;
            if (IsLoaded && ActualWidth > 10)
                tab.SetPaneWidth(ActualWidth);
        }
        Dispatcher.InvokeAsync(AutoSizeColumns, DispatcherPriority.Background);
    }

    private void OnItemsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        => Dispatcher.InvokeAsync(AutoSizeColumns, DispatcherPriority.Background);

    private void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TabViewModel.ShowDetailsView))
            Dispatcher.InvokeAsync(AutoSizeColumns, DispatcherPriority.Background);
    }

    private PaneViewModel? Pane => DataContext as PaneViewModel;
    private TabViewModel?  Tab  => Pane?.ActiveTab;

    // ── Navigation ─────────────────────────────────────────────────────────
    private void FileItem_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Tab is not { } tab || tab.SelectedItem is not { } item) return;
        RecentInteractionService.Record(item.FullPath);
        if (item.IsDirectory) tab.Navigate(item.FullPath);
        else tab.OpenFile(item.FullPath);
    }

    private void SearchResult_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Tab is not { } tab || tab.SelectedItem is not { } item) return;
        RecentInteractionService.Record(item.FullPath);
        if (item.IsDirectory) tab.Navigate(item.FullPath);
        else tab.OpenFile(item.FullPath);
    }

    private void IconItem_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Tab is not { } tab || tab.SelectedItem is not { } item) return;
        RecentInteractionService.Record(item.FullPath);
        if (item.IsDirectory) tab.Navigate(item.FullPath);
        else tab.OpenFile(item.FullPath);
    }

    // ── Breadcrumb / Address ───────────────────────────────────────────────
    private void BreadcrumbArea_Click(object sender, MouseButtonEventArgs e)
    {
        if (Tab is not { } tab) return;
        if (tab.IsEditingPath) return; // already open — TextBox handles clicks now

        tab.IsEditingPath = true;

        // Show history popup if there are recent paths
        var history = Pane?.History?.RecentPaths;
        if (history is { Count: > 0 })
        {
            HistoryListBox.ItemsSource          = history;
            AddressHistoryPopup.PlacementTarget = BreadcrumbBorder;
            AddressHistoryPopup.Width           = BreadcrumbBorder.ActualWidth;
            AddressHistoryPopup.IsOpen          = true;
        }

        // Always watch for outside clicks so we can exit edit mode.
        // Popup clicks live in a separate HwndSource and never reach this handler.
        var win = Window.GetWindow(this);
        if (win != null)
        {
            win.PreviewMouseDown += AddressBarOutsideClick;
            // Close the popup when the user switches to another app — otherwise the
            // topmost history popup floats over the other application.
            win.Deactivated += AddressBarWindowDeactivated;
        }

        Dispatcher.InvokeAsync(() =>
        {
            AddressBox.Text = tab.CurrentPath;
            AddressBox.Focus();
            AddressBox.SelectAll();
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void AddressBarOutsideClick(object sender, MouseButtonEventArgs e)
    {
        // Ignore clicks on the breadcrumb border itself (re-clicking the address bar)
        if (IsDescendantOf(e.OriginalSource as DependencyObject, BreadcrumbBorder)) return;
        ExitAddressBar();
    }

    private void ExitAddressBar()
    {
        if (AddressHistoryPopup.IsOpen)
        {
            AddressHistoryPopup.IsOpen  = false;
            HistoryListBox.SelectedItem = null;
        }
        if (Tab is { } tab) tab.IsEditingPath = false;
        var win = Window.GetWindow(this);
        if (win != null)
        {
            win.PreviewMouseDown -= AddressBarOutsideClick;
            win.Deactivated      -= AddressBarWindowDeactivated;
        }
    }

    private void AddressBarWindowDeactivated(object? sender, EventArgs e) => ExitAddressBar();

    private static bool IsDescendantOf(DependencyObject? child, DependencyObject ancestor)
    {
        var cur = child;
        while (cur != null)
        {
            if (cur == ancestor) return true;
            cur = VisualTreeHelper.GetParent(cur);
        }
        return false;
    }

    private void AddressBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (Tab is not { } tab) return;
        if (e.Key == Key.Enter)
        {
            var path = ((TextBox)sender).Text;
            ExitAddressBar();
            tab.Navigate(path);
            FileList.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            ExitAddressBar();
            FileList.Focus();
            e.Handled = true;
        }
    }

    private void AddressBox_GotFocus(object sender, RoutedEventArgs e)
        => ((TextBox)sender).SelectAll();

    private void AddressBox_LostFocus(object sender, RoutedEventArgs e)
    {
        // Safety net for Alt+Tab / app losing focus — don't exit if popup is
        // handling the interaction (user clicked a history item).
        if (!AddressHistoryPopup.IsOpen)
            ExitAddressBar();
    }

    private void AddressHistoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (HistoryListBox.SelectedItem is not string path) return;
        ExitAddressBar();
        Tab?.Navigate(path);
        FileList.Focus();
    }

    // ── Search ─────────────────────────────────────────────────────────────
    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (Tab is not { } tab) return;
        if (e.Key == Key.Enter)      { _ = tab.StartDeepSearchAsync(); e.Handled = true; }
        else if (e.Key == Key.Escape){ tab.ClearSearch();              e.Handled = true; }
    }

    // ── Column sort ────────────────────────────────────────────────────────
    private void ColumnHeader_Click(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader { Role: GridViewColumnHeaderRole.Normal } header) return;
        if (Tab is not { } tab) return;

        var baseText = (header.Content?.ToString() ?? "").TrimEnd(' ', '▲', '▼');
        var col = baseText switch
        {
            "Name"          => (SortColumn?)SortColumn.Name,
            "Size"          => SortColumn.Size,
            "Date Modified" => SortColumn.DateModified,
            "Date Created"  => SortColumn.DateCreated,
            "Type"          => SortColumn.Type,
            _               => null
        };
        if (col is null) return;

        if (tab.ActiveSortColumn == col) tab.SortAscending = !tab.SortAscending;
        else { tab.ActiveSortColumn = col.Value; tab.SortAscending = true; }

        if (_lastSortHeader != null && _lastSortHeader != header)
            _lastSortHeader.Content = (_lastSortHeader.Content?.ToString() ?? "").TrimEnd(' ', '▲', '▼');

        header.Content = $"{baseText} {(tab.SortAscending ? "▲" : "▼")}";
        _lastSortHeader = header;
    }

    // ── Selection ──────────────────────────────────────────────────────────
    private void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Tab is { } tab)
            tab.UpdateSelection(FileList.SelectedItems.Cast<FileItem>().ToList());
    }

    private void SearchResultList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Tab is { } tab)
            tab.UpdateSelection(SearchResultList.SelectedItems.Cast<FileItem>().ToList());
    }

    private void IconView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Tab is { } tab)
            tab.UpdateSelection(IconView.SelectedItems.Cast<FileItem>().ToList());
    }

    // ── View mode icon clicks ──────────────────────────────────────────────
    private void DetailsIcon_Click(object sender, MouseButtonEventArgs e)
        => Tab?.SetDetailsViewCommand.Execute(null);

    private void ThumbnailsIcon_Click(object sender, MouseButtonEventArgs e)
        => Tab?.SetThumbnailsViewCommand.Execute(null);

    // ── Right-click context menu ───────────────────────────────────────────
    private void List_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Check for column header first (tunneling phase gives us reliable interception)
        if (FileList.View is GridView gv)
        {
            var hdrEl = e.OriginalSource as DependencyObject;
            while (hdrEl != null)
            {
                if (hdrEl is GridViewColumnHeader { Role: GridViewColumnHeaderRole.Normal } h)
                {
                    ShowColumnContextMenu(h, gv);
                    e.Handled = true;
                    return;
                }
                hdrEl = VisualTreeHelper.GetParent(hdrEl);
            }
        }

        var el = e.OriginalSource as DependencyObject;
        while (el != null && el is not ListViewItem && el is not ListBoxItem)
            el = VisualTreeHelper.GetParent(el);

        var tab = Tab;
        if ((el as FrameworkElement)?.DataContext is not FileItem item || tab == null) return;

        if (!tab.SelectedItems.Contains(item))
        {
            if (sender is ListBox  lb) lb.SelectedItem = item;
            if (sender is ListView lv) lv.SelectedItem = item;
        }

        ShowItemContextMenu(item, tab, (UIElement)el);
        e.Handled = true;
    }

    private void ShowItemContextMenu(FileItem item, TabViewModel tab, UIElement anchor)
    {
        var vm       = Window.GetWindow(this)?.DataContext as MainViewModel;
        var sepStyle = (Style)FindResource("MenuSep");
        var menu     = new ContextMenu { HorizontalContentAlignment = HorizontalAlignment.Stretch };

        var filterItems = new List<(MenuItem mi, string label)>();
        var sepList     = new List<Separator>();
        MenuItem? labelRow = null;

        // ── Search bar (icon | input | clear ×) ──────────────────────────────
        var searchBar = new Border
        {
            Margin              = new Thickness(0),
            Padding             = new Thickness(0),
            BorderThickness     = new Thickness(0, 0, 0, 1),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        searchBar.SetResourceReference(Border.BackgroundProperty,  "ZephyrSurface");
        searchBar.SetResourceReference(Border.BorderBrushProperty, "ZephyrBorder");

        var searchGrid = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch };
        searchGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        searchGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        searchGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Magnifier icon
        var magIcon = new TextBlock
        {
            FontFamily        = new FontFamily("Segoe Fluent Icons"),
            Text              = "",
            FontSize          = 13,
            Margin            = new Thickness(12, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible  = false,
        };
        magIcon.SetResourceReference(TextBlock.ForegroundProperty, "ZephyrTextSecondary");
        Grid.SetColumn(magIcon, 0);
        searchGrid.Children.Add(magIcon);

        // Placeholder (overlaid on input column, behind TextBox)
        var placeholder = new TextBlock
        {
            Text              = "Search actions…",
            FontSize          = 13,
            Margin            = new Thickness(3, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible  = false,
        };
        placeholder.SetResourceReference(TextBlock.ForegroundProperty, "ZephyrTextSecondary");
        Grid.SetColumn(placeholder, 1);
        searchGrid.Children.Add(placeholder);

        // Actual TextBox — transparent, borderless, sits on top of placeholder
        var searchBox = new TextBox
        {
            FontSize          = 13,
            Padding           = new Thickness(2, 8, 2, 8),
            BorderThickness   = new Thickness(0),
            Background        = Brushes.Transparent,
            VerticalAlignment = VerticalAlignment.Center,
        };
        searchBox.SetResourceReference(TextBox.ForegroundProperty, "ZephyrTextPrimary");
        Grid.SetColumn(searchBox, 1);
        searchGrid.Children.Add(searchBox);

        // Clear × button (hidden until text is entered)
        var clearIcon = new TextBlock
        {
            FontFamily        = new FontFamily("Segoe Fluent Icons"),
            Text              = "",
            FontSize          = 10,
            Margin            = new Thickness(4, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor            = Cursors.Arrow,
            Visibility        = Visibility.Collapsed,
        };
        clearIcon.SetResourceReference(TextBlock.ForegroundProperty, "ZephyrTextSecondary");
        clearIcon.MouseLeftButtonUp += (_, _) =>
        {
            searchBox.Clear();
            searchBox.Focus();
            Keyboard.Focus(searchBox);
        };
        Grid.SetColumn(clearIcon, 2);
        searchGrid.Children.Add(clearIcon);

        searchBar.Child = searchGrid;

        // Wrap in a MenuItem with a bare template so WPF doesn't apply hover
        // highlight or extra padding. The ContentPresenter is stretched to fill
        // the full menu width so the search bar goes edge-to-edge.
        var cpFactory = new FrameworkElementFactory(typeof(ContentPresenter));
        cpFactory.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
        cpFactory.SetBinding(ContentPresenter.ContentProperty, new System.Windows.Data.Binding
        {
            RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent),
            Path           = new PropertyPath(MenuItem.HeaderProperty),
        });
        var bareTemplate = new ControlTemplate(typeof(MenuItem)) { VisualTree = cpFactory };

        menu.Items.Add(new MenuItem
        {
            Header                     = searchBar,
            Template                   = bareTemplate,
            Padding                    = new Thickness(0),
            Margin                     = new Thickness(0),
            Focusable                  = false,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        });

        Separator AddSep()
        {
            var s = new Separator { Style = sepStyle };
            sepList.Add(s);
            menu.Items.Add(s);
            return s;
        }

        void Add(MenuItem mi, string label)
        {
            filterItems.Add((mi, label.ToLowerInvariant()));
            menu.Items.Add(mi);
        }

        // ── Primary actions ───────────────────────────────────────────────────
        bool inArchive = tab.IsArchiveView; // browsing inside an archive → read-only menu

        Add(MakeMenuItem("Open", () =>
        {
            if (item.IsDirectory) tab.Navigate(item.FullPath);
            else                  tab.OpenFile(item.FullPath);
        }), "open");

        if (!inArchive)
        {
        if (item.IsDirectory)
            Add(MakeMenuItem("Add to Bookmarks", () =>
            {
                if (Window.GetWindow(this)?.DataContext is MainViewModel mvm)
                    mvm.AddBookmark(item.Name, item.FullPath);
            }), "add bookmarks favorite");

        if (!item.IsDirectory)
        {
            var openWithMi = new MenuItem { Header = "Open With…" };
            openWithMi.Click += (_, _) =>
            {
                menu.IsOpen = false;
                var path = item.FullPath;
                var t = new System.Threading.Thread(() => ShellIntegrationService.ShowOpenWith(path));
                t.SetApartmentState(System.Threading.ApartmentState.STA);
                t.IsBackground = true;
                t.Start();
            };
            Add(openWithMi, "open with");
            if (_elevatableExts.Contains(item.Extension))
            {
                var runAsAdminMi = new MenuItem { Header = "Run as Administrator" };
                runAsAdminMi.Click += (_, _) =>
                {
                    menu.IsOpen = false;
                    var path = item.FullPath;
                    var t = new System.Threading.Thread(() =>
                    {
                        try { ShellIntegrationService.RunAsAdmin(path); }
                        catch (Exception ex) { Dispatcher.Invoke(() => ShowError(ex.Message)); }
                    });
                    t.SetApartmentState(System.Threading.ApartmentState.STA);
                    t.IsBackground = true;
                    t.Start();
                };
                Add(runAsAdminMi, "run administrator admin elevated");
            }
        }

        Add(MakeMenuItem("Open in Terminal", () =>
        {
            var dir = item.IsDirectory ? item.FullPath : Path.GetDirectoryName(item.FullPath)!;
            TerminalService.OpenAt(dir);
        }), "open terminal console cmd powershell");
        }

        var owner   = Window.GetWindow(this);
        var itemDir = Path.GetDirectoryName(item.FullPath)!;

        AddSep();

        // ── Archive actions ───────────────────────────────────────────────────
        // Flat top-level items: the themed MenuItem template has no submenu popup.
        if (inArchive)
        {
            // Inside an archive (read-only): extract the selected entries to disk.
            var (archiveFile, baseInner) = ArchivePath.Parse(tab.CurrentPath);
            var selItems = tab.SelectedItems.Count > 0 ? tab.SelectedItems.ToList() : [item];
            var selLabel = selItems.Count == 1 ? "Extract selected…" : $"Extract {selItems.Count} items…";
            Add(MakeMenuItem(selLabel, () =>
            {
                var defaultDest = Path.GetDirectoryName(archiveFile) ?? tab.CurrentPath;
                var dlg = new ExtractDialog([Path.GetFileName(archiveFile)], defaultDest) { Owner = owner };
                if (dlg.ShowDialog() != true) return;
                var opts   = new ZephyrArchiveService.ExtractOptions(Password: dlg.Password ?? tab.CurrentArchivePassword);
                var inners = selItems.Select(i => ArchivePath.Parse(i.FullPath).Inner).ToList();
                RunArchiveWithProgress($"Extracting from {Path.GetFileName(archiveFile)}…",
                    (prog, ct) => ZephyrArchiveService.ExtractEntriesAsync(archiveFile, inners, baseInner, dlg.Destination, opts, prog, ct));
            }), "extract selected archive entry");
        }
        else
        {
        // Compress: all selected items, falling back to the right-clicked item.
        var compressSources = tab.SelectedItems.Count > 0 ? tab.SelectedItems.ToList() : [item];
        var compressName    = compressSources.Count == 1
                                ? Path.GetFileNameWithoutExtension(compressSources[0].Name)
                                : "Archive";

        Add(MakeMenuItem("Compress…", () =>
        {
            var dlg = new CompressDialog(compressName, tab.CurrentPath, compressSources.Count) { Owner = owner };
            if (dlg.ShowDialog() != true) return;
            var sources = compressSources.Select(i => i.FullPath).ToList();
            var name    = Path.GetFileName(dlg.ResultPath);
            if (dlg.AddToExisting)
                RunArchiveWithProgress($"Adding to {name}…",
                    (prog, ct) => ZephyrArchiveService.AppendToZipAsync(dlg.ResultPath, sources, dlg.Options.Level, prog, ct));
            else
                RunArchiveWithProgress($"Compressing {name}…",
                    (prog, ct) => ZephyrArchiveService.CreateAsync(dlg.ResultPath, sources, dlg.Options, prog, ct));
            tab.Reload();
        }), "compress archive zip tar gz create");

        // Extract: selected archives, falling back to the right-clicked item if it's one.
        var extractSources = (tab.SelectedItems.Count > 0 ? tab.SelectedItems.ToList() : [item])
            .Where(i => !i.IsDirectory && ZephyrArchiveService.CanExtract(i.FullPath))
            .ToList();

        if (extractSources.Count > 0)
        {
            var extractLabel = extractSources.Count == 1 ? "Extract…" : $"Extract {extractSources.Count} archives…";
            Add(MakeMenuItem(extractLabel, () =>
            {
                var defaultDest = extractSources.Count == 1
                    ? Path.Combine(tab.CurrentPath, StripArchiveExt(extractSources[0].Name))
                    : tab.CurrentPath;
                var dlg = new ExtractDialog(extractSources.Select(a => a.Name).ToList(), defaultDest) { Owner = owner };
                if (dlg.ShowDialog() != true) return;
                var opts  = new ZephyrArchiveService.ExtractOptions(Password: dlg.Password);
                var title = extractSources.Count == 1 ? $"Extracting {extractSources[0].Name}…" : $"Extracting {extractSources.Count} archives…";
                RunArchiveWithProgress(title, async (prog, ct) =>
                {
                    for (int i = 0; i < extractSources.Count; i++)
                    {
                        var a = extractSources[i];
                        var dest = extractSources.Count == 1 ? dlg.Destination
                                 : dlg.EachToOwnSubfolder ? Path.Combine(dlg.Destination, StripArchiveExt(a.Name))
                                 : dlg.Destination;
                        int idx = i + 1;
                        IProgress<ZephyrArchiveService.ArchiveProgress> sub = extractSources.Count == 1
                            ? prog
                            : new Progress<ZephyrArchiveService.ArchiveProgress>(p =>
                                prog.Report(p with { CurrentEntry = $"({idx}/{extractSources.Count}) {a.Name} — {p.CurrentEntry}" }));
                        await ZephyrArchiveService.ExtractAsync(a.FullPath, dest, opts, sub, ct);
                    }
                });
                tab.Reload();
            }), "extract archive unzip zip 7z rar tar gz bz2 xz");

            Add(MakeMenuItem("Test Archive", () =>
            {
                _ = RunArchiveAsync(async () =>
                {
                    var sb     = new StringBuilder();
                    bool allOk = true;
                    foreach (var a in extractSources)
                    {
                        var r = await ZephyrArchiveService.TestAsync(a.FullPath);
                        if (r.AllOk)
                            sb.AppendLine($"✔ {a.Name} — all {r.Total} entries OK");
                        else
                        {
                            allOk = false;
                            sb.AppendLine($"✘ {a.Name} — {r.Failed} of {r.Total} failed:");
                            foreach (var f in r.FailedEntries.Take(10)) sb.AppendLine($"      • {f}");
                            if (r.Failed > 10) sb.AppendLine($"      …and {r.Failed - 10} more");
                        }
                    }
                    ZephyrMessageBox.Show(sb.ToString().TrimEnd(), allOk ? "Test Archive — OK" : "Test Archive — Problems Found");
                });
            }), "test archive verify integrity");
        }
        }

        if (!inArchive)
        {
        AddSep();

        // ── Clipboard operations ──────────────────────────────────────────────
        if (vm != null)
        {
            Add(MakeMenuItem("Cut",    () => vm.CutCommand.Execute(null),   "Ctrl+X"), "cut move");
            Add(MakeMenuItem("Copy",   () => vm.CopyCommand.Execute(null),  "Ctrl+C"), "copy");
            Add(MakeMenuItem("Paste",  () => vm.PasteCommand.Execute(null), "Ctrl+V",
                enabled: ClipboardService.HasFiles()), "paste");
            AddSep();
            Add(MakeMenuItem("Rename", () => vm.RenameCommand.Execute(null), "F2"),    "rename");
            Add(MakeMenuItem("Delete", () => vm.DeleteCommand.Execute(null), "Del"),   "delete remove trash");

            var duplicateSources = tab.SelectedItems.Count > 0
                ? tab.SelectedItems.Select(i => i.FullPath)
                : [item.FullPath];
            Add(MakeMenuItem("Create Copy", () => _ = tab.DuplicateAsync(duplicateSources)), "create copy duplicate");
        }

        AddSep();

        // ── Shell utilities ───────────────────────────────────────────────────
        Add(MakeMenuItem("Copy Path", () => Clipboard.SetText(item.FullPath)), "copy path location");

        Add(MakeMenuItem("Create Shortcut", () =>
        {
            try   { ShellIntegrationService.CreateShortcut(item.FullPath, itemDir); tab.Reload(); }
            catch (Exception ex) { ShowError(ex.Message); }
        }), "create shortcut link lnk");

        Add(MakeMenuItem("Create Link…", () =>
        {
            var dlg = new Zephyr.UI.Dialogs.CreateLinkDialog(item.FullPath)
                { Owner = Window.GetWindow(this) };
            if (dlg.ShowDialog() != true) return;
            try
            {
                Zephyr.Core.FileSystem.LinkService.Create(dlg.SelectedKind, dlg.LinkPath, dlg.TargetPath);
                tab.Reload();
            }
            catch (Exception ex)
            {
                ShowError(dlg.SelectedKind == Zephyr.Core.FileSystem.LinkKind.Symbolic
                    ? $"{ex.Message}\n\nSymbolic links need administrator rights or Windows Developer Mode. " +
                      "For folders try a Junction; for files try a Hard link instead."
                    : ex.Message);
            }
        }), "create link symbolic junction hardlink hard symlink");

        if (!item.IsDirectory)
            Add(MakeMenuItem("Pin to Start",
                () => ShellIntegrationService.PinToStart(item.FullPath)), "pin start menu");

        if (!item.IsDirectory)
            Add(MakeMenuItem("Checksum…", () =>
            {
                var win = new Zephyr.UI.Dialogs.ChecksumWindow(item.FullPath)
                    { Owner = Window.GetWindow(this) };
                win.ShowDialog();
            }), "checksum hash md5 sha verify compare integrity");

        if (item.IsDirectory)
            Add(MakeMenuItem("Disk usage…", () =>
            {
                var win = new Zephyr.UI.Dialogs.DiskUsageWindow(item.FullPath)
                    { Owner = Window.GetWindow(this) };
                win.Show();
            }), "disk usage size treemap heatmap space analyze");

        // ── Colour label ──────────────────────────────────────────────────────
        AddSep();
        var labelTargets = tab.SelectedItems.Count > 0 ? tab.SelectedItems.ToList() : [item];
        labelRow = BuildLabelRow(labelTargets, menu);
        menu.Items.Add(labelRow);

        // ── Hide / Unhide ─────────────────────────────────────────────────────
        var hideTargets = tab.SelectedItems.Count > 0 ? tab.SelectedItems.ToList() : [item];
        bool unhide = item.IsHidden;
        Add(MakeMenuItem(unhide ? "Unhide" : "Hide", () =>
        {
            try
            {
                foreach (var t in hideTargets)
                {
                    var attr = File.GetAttributes(t.FullPath);
                    attr = unhide ? attr & ~FileAttributes.Hidden : attr | FileAttributes.Hidden;
                    File.SetAttributes(t.FullPath, attr);
                }
                tab.Reload();
            }
            catch (Exception ex) { ShowError(ex.Message); }
        }), unhide ? "unhide show reveal folder" : "hide conceal folder");

        // ── Lock / Unlock folder ──────────────────────────────────────────────
        if (item.IsDirectory)
        {
            AddSep();
            var lockRoot = FolderLockService.FindLockRoot(item.FullPath);
            if (!FolderLockService.IsLockRoot(item.FullPath))
            {
                Add(MakeMenuItem("Lock Folder…", () =>
                {
                    var dlg = new SetPasswordDialog(item.Name) { Owner = Window.GetWindow(this) };
                    if (dlg.ShowDialog() != true) return;
                    FolderLockService.Lock(item.FullPath, dlg.Password);
                    tab.Reload();
                }), "lock folder password protect privacy");
            }
            else
            {
                if (FolderLockService.IsUnlocked(item.FullPath))
                    Add(MakeMenuItem("Lock Now", () =>
                    {
                        FolderLockService.Relock(item.FullPath);
                        tab.Reload();
                    }), "lock now relock secure");
                else
                    Add(MakeMenuItem("Unlock…", () =>
                    {
                        if (lockRoot is null) return;
                        var pw = PromptFolderPassword(lockRoot, "Locked Folder",
                            $"\"{item.Name}\" is locked. Enter its password to open it.");
                        if (pw is null) return;
                        FolderLockService.Unlock(lockRoot, pw);
                        tab.Reload();
                    }), "unlock open password");

                Add(MakeMenuItem("Remove Lock…", () =>
                {
                    if (lockRoot is null) return;
                    var pw = PromptFolderPassword(lockRoot, "Remove Lock",
                        $"Enter the password for \"{item.Name}\" to remove its lock.");
                    if (pw is null) return;
                    FolderLockService.RemoveLock(item.FullPath, pw);
                    tab.Reload();
                }), "remove lock unprotect delete password");
            }
        }

        AddSep();
        Add(MakeMenuItem("Attributes & Timestamps…", () =>
        {
            var targets = (tab.SelectedItems.Count > 0 ? tab.SelectedItems : [item])
                .Select(i => i.FullPath).ToList();
            var win = new Zephyr.UI.Dialogs.BatchAttributesWindow(targets)
                { Owner = Window.GetWindow(this) };
            if (win.ShowDialog() == true) tab.Reload();
        }), "attributes timestamps read-only hidden system archive date modified created");

        Add(MakeMenuItem("Properties", () =>
        {
            var win = new Zephyr.UI.Windows.FilePropertiesWindow(item.FullPath)
                { Owner = Window.GetWindow(this) };
            win.ShowDialog();
        }), "properties info details");
        }

        // ── Search filtering ──────────────────────────────────────────────────
        searchBox.TextChanged += (_, _) =>
        {
            var hasText = !string.IsNullOrEmpty(searchBox.Text);
            placeholder.Visibility = hasText ? Visibility.Collapsed : Visibility.Visible;
            clearIcon.Visibility   = hasText ? Visibility.Visible   : Visibility.Collapsed;

            var q = searchBox.Text.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(q))
            {
                foreach (var (mi, _) in filterItems) mi.Visibility = Visibility.Visible;
                foreach (var s in sepList)           s.Visibility  = Visibility.Visible;
                if (labelRow != null) labelRow.Visibility = Visibility.Visible;
                return;
            }
            // Hide separators (and the colour-label row) while searching so results render flat
            foreach (var s in sepList) s.Visibility = Visibility.Collapsed;
            if (labelRow != null) labelRow.Visibility = Visibility.Collapsed;
            foreach (var (mi, label) in filterItems)
                mi.Visibility = label.Contains(q) ? Visibility.Visible : Visibility.Collapsed;
        };

        // Move keyboard focus to the search box once the menu is rendered
        menu.Opened += (_, _) =>
        {
            searchBox.Focus();
            Keyboard.Focus(searchBox);
        };

        menu.PlacementTarget = anchor;
        menu.Placement       = PlacementMode.MousePoint;
        menu.IsOpen          = true;
    }

    // Builds the colour-label swatch row for the context menu: a circle per palette colour
    // (ringed if currently applied) plus a clear (×) chip. Clicking applies/clears the label
    // on every target (the full selection, or just the right-clicked item) and closes the menu.
    private MenuItem BuildLabelRow(IReadOnlyList<FileItem> targets, ContextMenu menu)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 5, 12, 5) };

        // Show the current selection's ring only when a single item is selected.
        string? currentKey = targets.Count == 1 ? FileLabelService.GetKey(targets[0].FullPath) : null;

        foreach (var lbl in FileLabels.All)
        {
            var fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(lbl.Hex));
            fill.Freeze();
            var swatch = new Border
            {
                Width           = 18,
                Height          = 18,
                CornerRadius    = new CornerRadius(9),
                Margin          = new Thickness(0, 0, 7, 0),
                Background      = fill,
                Cursor          = Cursors.Hand,
                ToolTip         = lbl.Name,
                BorderThickness = new Thickness(currentKey == lbl.Key ? 2 : 0),
            };
            swatch.SetResourceReference(Border.BorderBrushProperty, "ZephyrTextPrimary");
            var key = lbl.Key;
            swatch.MouseLeftButtonUp += (_, _) =>
            {
                foreach (var t in targets)
                {
                    FileLabelService.Set(t.FullPath, key);
                    t.LabelColor = FileLabelService.GetHex(t.FullPath);
                }
                menu.IsOpen = false;
            };
            row.Children.Add(swatch);
        }

        // Clear (×) chip — removes any label from the targets.
        var clear = new Border
        {
            Width           = 18,
            Height          = 18,
            CornerRadius    = new CornerRadius(9),
            Margin          = new Thickness(3, 0, 0, 0),
            Cursor          = Cursors.Hand,
            ToolTip         = "No label",
            BorderThickness = new Thickness(1),
        };
        clear.SetResourceReference(Border.BorderBrushProperty, "ZephyrBorder");
        clear.SetResourceReference(Border.BackgroundProperty, "ZephyrElevated");
        var x = new TextBlock
        {
            FontFamily          = new FontFamily("Segoe Fluent Icons"),
            Text                = "",
            FontSize            = 9,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Center,
        };
        x.SetResourceReference(TextBlock.ForegroundProperty, "ZephyrTextSecondary");
        clear.Child = x;
        clear.MouseLeftButtonUp += (_, _) =>
        {
            foreach (var t in targets)
            {
                FileLabelService.Set(t.FullPath, null);
                t.LabelColor = string.Empty;
            }
            menu.IsOpen = false;
        };
        row.Children.Add(clear);

        // Bare template so the row gets no MenuItem hover highlight or padding.
        var cpFactory = new FrameworkElementFactory(typeof(ContentPresenter));
        cpFactory.SetBinding(ContentPresenter.ContentProperty, new System.Windows.Data.Binding
        {
            RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent),
            Path           = new PropertyPath(MenuItem.HeaderProperty),
        });
        var bare = new ControlTemplate(typeof(MenuItem)) { VisualTree = cpFactory };

        return new MenuItem
        {
            Header     = row,
            Template   = bare,
            Padding    = new Thickness(0),
            Margin     = new Thickness(0),
            Focusable  = false,
        };
    }

    // Runs an archive operation off the menu, surfacing any failure to the user.
    private static async Task RunArchiveAsync(Func<Task> op)
    {
        try   { await op(); }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    // Runs a compress/extract operation behind the modal progress dialog (bar + ETA + cancel).
    private void RunArchiveWithProgress(string title,
        Func<IProgress<ZephyrArchiveService.ArchiveProgress>, CancellationToken, Task> work)
    {
        var dlg = new ArchiveProgressDialog(title, work) { Owner = Window.GetWindow(this) };
        dlg.ShowDialog();
        if (dlg.Error is { } ex) ShowError(ex.Message);
    }

    // Strips a compound (.tar.gz/.tar.bz2/.tar.xz) or single archive extension.
    private static string StripArchiveExt(string name)
    {
        foreach (var ext in new[] { ".tar.gz", ".tar.bz2", ".tar.xz" })
            if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                return name[..^ext.Length];
        return Path.GetFileNameWithoutExtension(name);
    }

    private static void ShowError(string msg) =>
        ZephyrMessageBox.Show(msg, "Error");

    // ── Column auto-size ─────────────────────────────────────────────────────

    private void AutoSizeColumns()
    {
        if (!IsLoaded) return;
        if (FileList.ActualWidth < 10) return;

        var tab = Tab;
        if (tab == null || !tab.ShowDetailsView) return;
        if (FileList.View is not GridView gv) return;

        var items = tab.Items.Take(200).ToList();

        double sizeW = items.Any(i => !i.IsDirectory)
            ? items.Where(i => !i.IsDirectory).Max(i => MeasureText(i.SizeDisplay, 13)) + 16
            : 60;
        double typeW = items.Count > 0
            ? items.Max(i => MeasureText(i.TypeDisplay, 13)) + 16
            : 80;
        double dateW = MeasureText("2025-01-31  23:59", 13) + 16;

        sizeW = Math.Clamp(sizeW, 50, MaxSizeWidth);
        typeW = Math.Clamp(typeW, 60, MaxTypeWidth);
        dateW = Math.Clamp(dateW, 90, MaxDateWidth);

        double reserved = 0;
        GridViewColumn? nameCol = null, sizeCol = null, dateCol = null, typeCol = null;
        GridViewColumn? dateCreatedCol = null, extCol = null, attrCol = null, contentsCol = null;
        foreach (var col in gv.Columns)
        {
            switch (col.Header as string)
            {
                case "Name":          nameCol        = col;                    break;
                case "Size":          sizeCol        = col; reserved += sizeW;  break;
                case "Date Modified": dateCol        = col; reserved += dateW;  break;
                case "Type":          typeCol        = col; reserved += typeW;  break;
                case "Date Created":  dateCreatedCol = col; reserved += dateW;  break;
                case "Extension":     extCol         = col; reserved += 80;     break;
                case "Attributes":    attrCol        = col; reserved += 70;     break;
                case "Contents":      contentsCol    = col; reserved += 160;    break;
            }
        }

        double avail = Math.Max(0, FileList.ActualWidth - 22);
        double nameW = Math.Max(120, avail - reserved);

        _isRebalancing = true;
        try
        {
            if (nameCol        != null) nameCol.Width        = nameW;
            if (sizeCol        != null) sizeCol.Width        = sizeW;
            if (dateCol        != null) dateCol.Width        = dateW;
            if (typeCol        != null) typeCol.Width        = typeW;
            if (dateCreatedCol != null) dateCreatedCol.Width = dateW;
            if (extCol         != null) extCol.Width         = 80;
            if (attrCol        != null) attrCol.Width        = 70;
            if (contentsCol    != null) contentsCol.Width    = 160;
        }
        finally
        {
            _isRebalancing = false;
        }
    }

    // ── Column resize rebalancing (DependencyPropertyDescriptor approach) ────────
    // DPD.AddValueChanged fires synchronously AFTER a column.Width is committed —
    // no DragDelta timing race, no before/after ambiguity.

    private bool _isRebalancing;
    private readonly List<GridViewColumn> _watchedCols = [];
    private static readonly DependencyPropertyDescriptor _widthDpd =
        DependencyPropertyDescriptor.FromProperty(GridViewColumn.WidthProperty, typeof(GridViewColumn));

    private void SubscribeColumnWidths()
    {
        if (FileList.View is not GridView gv) return;
        UnsubscribeColumnWidths();
        foreach (var col in gv.Columns)
        {
            _widthDpd.AddValueChanged(col, OnColumnWidthChanged);
            _watchedCols.Add(col);
        }
        gv.Columns.CollectionChanged += OnColumnsCollectionChanged;
    }

    private void UnsubscribeColumnWidths()
    {
        foreach (var col in _watchedCols)
            _widthDpd.RemoveValueChanged(col, OnColumnWidthChanged);
        _watchedCols.Clear();
        if (FileList.View is GridView gv)
            gv.Columns.CollectionChanged -= OnColumnsCollectionChanged;
    }

    private void OnColumnsCollectionChanged(object? sender,
        System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null)
            foreach (GridViewColumn col in e.OldItems)
            { _widthDpd.RemoveValueChanged(col, OnColumnWidthChanged); _watchedCols.Remove(col); }
        if (e.NewItems != null)
            foreach (GridViewColumn col in e.NewItems)
            { _widthDpd.AddValueChanged(col, OnColumnWidthChanged); _watchedCols.Add(col); }
    }

    private void OnColumnWidthChanged(object? sender, EventArgs e)
    {
        if (_isRebalancing || sender is not GridViewColumn changedCol) return;
        if (FileList.View is not GridView gv) return;

        var nameCol = gv.Columns.FirstOrDefault(c => c.Header as string == "Name");
        if (nameCol == null) return;

        double avail = Math.Max(0, FileList.ActualWidth - 22);

        _isRebalancing = true;
        try
        {
            if (changedCol == nameCol)
            {
                // Name was resized by the user: absorb the change in the column to its right.
                var nextCol = ColumnAfter(gv, nameCol);
                if (nextCol == null) return;
                double others = gv.Columns.Where(c => c != nameCol && c != nextCol).Sum(c => c.Width);
                double targetNext = avail - nameCol.Width - others;
                if (targetNext < MinColumnWidth)
                {
                    nextCol.Width = MinColumnWidth;
                    nameCol.Width = Math.Max(120, avail - MinColumnWidth - others);
                }
                else
                {
                    nextCol.Width = targetNext;
                }
            }
            else
            {
                // A non-Name column was resized: clamp it to the minimum, then fill Name.
                if (changedCol.Width < MinColumnWidth) changedCol.Width = MinColumnWidth;
                double used = gv.Columns.Where(c => c != nameCol).Sum(c => c.Width);
                nameCol.Width = Math.Max(120, avail - used);
            }
        }
        finally
        {
            _isRebalancing = false;
        }
    }

    private static GridViewColumn? ColumnAfter(GridView gv, GridViewColumn col)
    {
        int idx = gv.Columns.IndexOf(col);
        return idx >= 0 && idx + 1 < gv.Columns.Count ? gv.Columns[idx + 1] : null;
    }

    private double MeasureText(string text, double fontSize)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), fontSize, Brushes.Black,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        return ft.Width;
    }

    // ── Column header right-click (show / hide columns) ───────────────────────

    private void ShowColumnContextMenu(GridViewColumnHeader header, GridView gv)
    {
        var menu = new ContextMenu();
        foreach (var name in _columnOrder)
        {
            var mi = new MenuItem
            {
                Header      = name,
                IsCheckable = true,
                IsChecked   = !_hiddenColumns.Contains(name),
                IsEnabled   = name != "Name",
            };
            var capture = name;
            mi.Click += (_, _) =>
            {
                if (_hiddenColumns.Contains(capture)) ShowColumn(capture, gv);
                else                                  HideColumn(capture, gv);
            };
            menu.Items.Add(mi);
        }
        menu.PlacementTarget = header;
        menu.Placement       = PlacementMode.Bottom;
        menu.IsOpen          = true;
    }

    private void ShowColumn(string name, GridView gv)
    {
        if (!_hiddenColumns.Remove(name)) return;
        if (!_removedColumns.TryGetValue(name, out var col)) return;
        _removedColumns.Remove(name);
        int targetOrder = _columnOrder.IndexOf(name);
        int insertAt    = gv.Columns.Count;
        for (int i = 0; i < gv.Columns.Count; i++)
        {
            if (gv.Columns[i].Header is string h && _columnOrder.IndexOf(h) > targetOrder)
            { insertAt = i; break; }
        }
        gv.Columns.Insert(insertAt, col);
        Dispatcher.InvokeAsync(AutoSizeColumns, DispatcherPriority.Background);
    }

    private void HideColumn(string name, GridView gv)
    {
        var col = gv.Columns.FirstOrDefault(c => c.Header is string h && h == name);
        if (col == null) return;
        gv.Columns.Remove(col);
        _hiddenColumns.Add(name);
        _removedColumns[name] = col;
        Dispatcher.InvokeAsync(AutoSizeColumns, DispatcherPriority.Background);
    }

    private void HideColumnSilent(string name, GridView gv)
    {
        var col = gv.Columns.FirstOrDefault(c => c.Header is string h && h == name);
        if (col == null) return;
        gv.Columns.Remove(col);
        _hiddenColumns.Add(name);
        _removedColumns[name] = col;
    }

    // ── Tab drag (reorder + new-window + split-view + merge-back) ───────────

    private void TabBar_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_tabIsDragging) AbortTabDrag();
        _tabDragStart  = e.GetPosition(null);
        _tabDragItem   = HitTestTab(e.OriginalSource as DependencyObject);
        _tabIsDragging = false;
    }

    private void TabBar_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _tabDragItem == null) return;

        var diff = _tabDragStart - e.GetPosition(null);
        if (!_tabIsDragging &&
            Math.Abs(diff.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(diff.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        if (!_tabIsDragging)
        {
            _tabIsDragging = true;
            Mouse.Capture((IInputElement)sender);
            if (Window.GetWindow(this) is { } w)
                w.PreviewMouseLeftButtonUp += TabDrag_GlobalMouseUp;
            _tabDragTimer = new DispatcherTimer(DispatcherPriority.Input)
                { Interval = TimeSpan.FromMilliseconds(32) };
            _tabDragTimer.Tick += TabDragTimer_Tick;
            _tabDragTimer.Start();
            // Low-level hook receives WM_LBUTTONUP system-wide regardless of which window
            // has focus or capture — the only reliable way to catch outside-window releases.
            _mouseHookProc = MouseHookProc;
            _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseHookProc, GetModuleHandle(null), 0);
        }

        // Live reorder within the tab strip
        var posInStrip = e.GetPosition(TabStrip);
        if (Pane != null && _tabDragItem != null &&
            posInStrip.X >= 0 && posInStrip.X <= TabStrip.ActualWidth &&
            posInStrip.Y >= 0 && posInStrip.Y <= TabStrip.ActualHeight)
        {
            var targetIdx  = GetTabIndexAtPoint(posInStrip);
            var currentIdx = Pane.Tabs.IndexOf(_tabDragItem);
            if (targetIdx >= 0 && currentIdx >= 0 && targetIdx != currentIdx)
                Pane.Tabs.Move(currentIdx, targetIdx);
        }

        UpdateDragCursor();
    }

    // Re-acquire capture if WPF drops it while the button is still held.
    // Use GetAsyncKeyState (hardware state) not Mouse.LeftButton (stale WPF cache).
    private void TabBar_LostCapture(object sender, MouseEventArgs e)
    {
        if (_tabIsDragging && (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0)
            Mouse.Capture((IInputElement)sender);
    }

    private void TabDragTimer_Tick(object? sender, EventArgs e)
    {
        if (!_tabIsDragging) { StopDragTimer(); return; }
        // GetAsyncKeyState reflects actual hardware state — Mouse.LeftButton only updates
        // when WM_LBUTTONUP reaches our window, which never happens for outside-window releases.
        bool lbDown = (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;
        if (!lbDown)
            CompleteDrag();
        else
            UpdateDragCursor();
    }

    private IntPtr MouseHookProc(int code, IntPtr w, IntPtr l)
    {
        if (code >= 0 && w == (IntPtr)WM_LBUTTONUP_MSG && _tabIsDragging)
            Dispatcher.BeginInvoke(CompleteDrag, DispatcherPriority.Input);
        return CallNextHookEx(_mouseHook, code, w, l);
    }

    private void RemoveMouseHook()
    {
        if (_mouseHook != IntPtr.Zero) { UnhookWindowsHookEx(_mouseHook); _mouseHook = IntPtr.Zero; }
        _mouseHookProc = null;
    }

    // WPF event path: fires when button released inside or (sometimes) outside window
    private void TabDrag_GlobalMouseUp(object sender, MouseButtonEventArgs e)
        => CompleteDrag();

    private void CompleteDrag()
    {
        if (!_tabIsDragging) return;    // guard against double-fire (hook + timer + event)
        _tabIsDragging = false;

        var tab = _tabDragItem;
        _tabDragItem = null;

        RemoveMouseHook();
        StopDragTimer();
        if (Window.GetWindow(this) is { } w) w.PreviewMouseLeftButtonUp -= TabDrag_GlobalMouseUp;
        Mouse.Capture(null);
        Mouse.OverrideCursor = null;

        if (tab == null) return;

        var win = Window.GetWindow(this);
        if (win == null || win.DataContext is not MainViewModel vm) return;

        // Use Win32 GetCursorPos + PointToScreen for reliable DPI-correct
        // outside-window detection regardless of WPF coordinate mapping.
        GetCursorPos(out var sc);
        var tl = win.PointToScreen(new Point(0, 0));
        var br = win.PointToScreen(new Point(win.ActualWidth, win.ActualHeight));

        bool outside   = sc.X < (int)tl.X || sc.X > (int)br.X
                      || sc.Y < (int)tl.Y || sc.Y > (int)br.Y;
        bool rightHalf = !outside && (sc.X - tl.X) > (br.X - tl.X) * 0.55;
        bool leftHalf  = !outside && !rightHalf;
        bool isRight   = Pane == vm.RightPane;

        if (outside)
        {
            var exe = Environment.ProcessPath
                   ?? Path.Combine(AppContext.BaseDirectory,
                      System.Diagnostics.Process.GetCurrentProcess().ProcessName + ".exe");
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = false };
                psi.ArgumentList.Add("--new-window");
                psi.ArgumentList.Add(tab.CurrentPath ?? "");
                System.Diagnostics.Process.Start(psi);
                if (Pane?.Tabs.Count > 1) Pane.CloseTabCommand.Execute(tab);
            }
            catch (Exception ex) { ZephyrMessageBox.Show($"Could not open new window:\n{exe}\n{ex.Message}", "Error"); }
        }
        else if (rightHalf && !isRight)
        {
            if (!vm.IsSplitView) vm.ToggleSplitViewCommand.Execute(null);
            vm.RightPane.AddTab(tab.CurrentPath);
            if (Pane?.Tabs.Count > 1) Pane.CloseTabCommand.Execute(tab);
            vm.SetActivePane(vm.RightPane);
        }
        else if (leftHalf && isRight && vm.IsSplitView)
        {
            vm.LeftPane.AddTab(tab.CurrentPath);
            if (Pane?.Tabs.Count > 1) Pane.CloseTabCommand.Execute(tab);
            vm.IsSplitView = false;
            vm.SetActivePane(vm.LeftPane);
        }
        // else: tab reorder was applied live in PreviewMouseMove
    }

    private void UpdateDragCursor()
    {
        var win = Window.GetWindow(this);
        if (win == null) return;

        GetCursorPos(out var sc);
        var tl = win.PointToScreen(new Point(0, 0));
        var br = win.PointToScreen(new Point(win.ActualWidth, win.ActualHeight));

        bool outside   = sc.X < (int)tl.X || sc.X > (int)br.X
                      || sc.Y < (int)tl.Y || sc.Y > (int)br.Y;
        bool isRight   = win.DataContext is MainViewModel vm0 && Pane == vm0.RightPane && vm0.IsSplitView;
        bool rightHalf = !outside && (sc.X - tl.X) > (br.X - tl.X) * 0.55;
        bool leftHalf  = !outside && !rightHalf;

        Mouse.OverrideCursor = outside                      ? Cursors.No
                             : (rightHalf && !isRight)      ? Cursors.SizeAll
                             : (leftHalf  &&  isRight)      ? Cursors.SizeAll
                             : null;
    }

    private void AbortTabDrag()
    {
        RemoveMouseHook();
        StopDragTimer();
        if (Window.GetWindow(this) is { } w) w.PreviewMouseLeftButtonUp -= TabDrag_GlobalMouseUp;
        Mouse.Capture(null);
        Mouse.OverrideCursor = null;
        _tabDragItem   = null;
        _tabIsDragging = false;
    }

    private void StopDragTimer()
    {
        _tabDragTimer?.Stop();
        if (_tabDragTimer != null) { _tabDragTimer.Tick -= TabDragTimer_Tick; _tabDragTimer = null; }
    }

    private int GetTabIndexAtPoint(Point pointInTabStrip)
    {
        if (Pane == null) return -1;

        var hit = VisualTreeHelper.HitTest(TabStrip, pointInTabStrip);
        for (var el = (DependencyObject?)hit?.VisualHit; el != null; el = VisualTreeHelper.GetParent(el))
        {
            if (el is FrameworkElement { DataContext: TabViewModel tab })
            {
                var idx = Pane.Tabs.IndexOf(tab);
                if (idx >= 0) return idx;
            }
        }

        for (int i = 0; i < Pane.Tabs.Count; i++)
        {
            if (TabStrip.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement c) continue;
            var right = c.TransformToAncestor(TabStrip).Transform(new Point(c.ActualWidth, 0)).X;
            if (pointInTabStrip.X <= right) return i;
        }
        return Pane.Tabs.Count > 0 ? Pane.Tabs.Count - 1 : -1;
    }

    private static TabViewModel? HitTestTab(DependencyObject? source)
    {
        for (var el = source; el != null; el = VisualTreeHelper.GetParent(el))
            if (el is FrameworkElement { DataContext: TabViewModel tab }) return tab;
        return null;
    }

    // Prompts (with retry) for a locked folder's password until it verifies or the
    // user cancels. Returns the verified password, or null on cancel.
    private string? PromptFolderPassword(LockedFolder root, string title, string prompt)
    {
        bool retry = false;
        while (true)
        {
            var dlg = new PasswordDialog(title, prompt, retry) { Owner = Window.GetWindow(this) };
            if (dlg.ShowDialog() != true) return null;
            if (FolderLockService.Verify(root, dlg.Password)) return dlg.Password;
            retry = true;
        }
    }

    private static MenuItem MakeMenuItem(string header, Action onClick,
        string? gestureText = null, bool enabled = true)
    {
        var mi = new MenuItem
        {
            Header           = header,
            IsEnabled        = enabled,
            InputGestureText = gestureText ?? string.Empty
        };
        mi.Click += (_, _) => onClick();
        return mi;
    }

    // ── Quick Preview (Space bar) ─────────────────────────────────────────

    private bool _quickPreviewVisible;
    private CancellationTokenSource? _quickPreviewCts;

    private void List_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && e.KeyboardDevice.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            if (_quickPreviewVisible) CloseQuickPreview();
            else if (Tab?.SelectedItem is { } item) _ = ShowQuickPreviewAsync(item);
            return;
        }

        if (e.Key == Key.Escape && _quickPreviewVisible)
        {
            e.Handled = true;
            CloseQuickPreview();
            return;
        }

        // While the preview is open, arrow keys move the selection and the preview
        // follows it live (macOS Quick Look style).
        if (_quickPreviewVisible &&
            e.Key is Key.Up or Key.Down or Key.Left or Key.Right &&
            e.KeyboardDevice.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            int delta = e.Key is Key.Up or Key.Left ? -1 : +1;
            if (MovePreviewSelection(sender as ListBox, delta) is { } next)
                _ = ShowQuickPreviewAsync(next);
            return;
        }

if (e.KeyboardDevice.Modifiers == ModifierKeys.None)
        {
            var c = KeyToChar(e.Key);
            if (c.HasValue)
            {
                e.Handled = true;
                JumpToLetter(c.Value, sender as ItemsControl);
            }
        }
    }

    // Moves the active list's selection by delta (clamped) and returns the newly
    // selected item, so the open preview can refresh to match.
    private FileItem? MovePreviewSelection(ListBox? list, int delta)
    {
        var items = Tab?.Items;
        if (list == null || items == null || items.Count == 0) return null;

        int index = list.SelectedIndex < 0 ? 0 : list.SelectedIndex;
        index = Math.Clamp(index + delta, 0, items.Count - 1);

        var next = items[index];
        list.SelectedItem = next;
        list.ScrollIntoView(next);
        return next;
    }

    private async Task ShowQuickPreviewAsync(FileItem item)
    {
        _quickPreviewCts?.Cancel();
        _quickPreviewCts = new CancellationTokenSource();
        var ct = _quickPreviewCts.Token;

        QuickPreviewTitle.Text             = item.Name;
        QuickPreviewImageScroll.Visibility = Visibility.Collapsed;
        QuickPreviewTextScroll.Visibility  = Visibility.Collapsed;
        QuickPreviewInfo.Visibility        = Visibility.Collapsed;
        QuickPreviewImage.Source           = null;
        QuickPreviewPdfPages.ItemsSource   = null;
        QuickPreviewOverlay.Visibility     = Visibility.Visible;
        _quickPreviewVisible               = true;

        // PDFs render to actual page images rather than scraped text.
        if (!item.IsDirectory && string.Equals(item.Extension, ".pdf", StringComparison.OrdinalIgnoreCase))
        {
            QuickPreviewText.Text             = "Rendering…";
            QuickPreviewTextScroll.Visibility = Visibility.Visible;
            try
            {
                var path  = item.FullPath;
                var pages = await PdfRenderService.RenderPagesAsync(path, ct);
                if (ct.IsCancellationRequested) return;
                if (pages.Count == 0) { ShowQPInfo(item); return; }
                QuickPreviewTextScroll.Visibility  = Visibility.Collapsed;
                QuickPreviewPdfPages.ItemsSource   = pages;
                QuickPreviewImageScroll.Visibility = Visibility.Visible;
                QuickPreviewImageScroll.ScrollToTop();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { QuickPreviewText.Text = $"[Cannot render PDF: {ex.Message}]"; }
            return;
        }

        var previewType = item.IsDirectory ? PreviewType.Info : PreviewService.GetType(item.Extension);

        switch (previewType)
        {
            case PreviewType.Image:
                try
                {
                    var bmp = await Task.Run(() =>
                    {
                        var b = new BitmapImage();
                        b.BeginInit();
                        b.UriSource    = new Uri(item.FullPath);
                        b.CacheOption  = BitmapCacheOption.OnLoad;
                        b.EndInit();
                        b.Freeze();
                        return b;
                    }, ct);
                    if (ct.IsCancellationRequested) return;
                    QuickPreviewImage.Source            = bmp;
                    QuickPreviewImageScroll.Visibility  = Visibility.Visible;
                }
                catch (OperationCanceledException) { return; }
                catch { ShowQPInfo(item); }
                break;

            case PreviewType.Text:
                QuickPreviewText.Text              = "Loading…";
                QuickPreviewTextScroll.Visibility  = Visibility.Visible;
                try
                {
                    var text = await Task.Run(() =>
                    {
                        var sb = new StringBuilder();
                        using var reader = new StreamReader(item.FullPath, detectEncodingFromByteOrderMarks: true);
                        for (int i = 0; i < 200 && !reader.EndOfStream; i++)
                            sb.AppendLine(reader.ReadLine());
                        return sb.ToString();
                    }, ct);
                    if (ct.IsCancellationRequested) return;
                    QuickPreviewText.Text = text;
                }
                catch (OperationCanceledException) { return; }
                catch { QuickPreviewText.Text = "[Cannot read file]"; }
                break;

            case PreviewType.Document:
                QuickPreviewText.Text             = "Loading…";
                QuickPreviewTextScroll.Visibility = Visibility.Visible;
                try
                {
                    var path = item.FullPath;
                    var docText = await Task.Run(() => DocumentTextExtractor.Extract(path), ct);
                    if (ct.IsCancellationRequested) return;
                    QuickPreviewText.Text = docText;
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) { QuickPreviewText.Text = $"[Cannot read document: {ex.Message}]"; }
                break;

            default:
                ShowQPInfo(item);
                break;
        }
    }

    private void ShowQPInfo(FileItem item)
    {
        QuickPreviewInfoIcon.Text           = item.Icon;
        QuickPreviewInfoType.Text           = item.TypeDisplay;
        QuickPreviewInfoSize.Text           = item.IsDirectory ? item.ContentSummary : item.SizeDisplay;
        QuickPreviewInfoDate.Text           = $"Modified  {item.LastModified:yyyy-MM-dd  HH:mm}";
        QuickPreviewInfo.Visibility         = Visibility.Visible;
    }

    private void CloseQuickPreview()
    {
        _quickPreviewCts?.Cancel();
        _quickPreviewVisible               = false;
        QuickPreviewOverlay.Visibility     = Visibility.Collapsed;
        QuickPreviewImage.Source           = null;
        QuickPreviewPdfPages.ItemsSource   = null;
        QuickPreviewText.Text              = string.Empty;
    }

    private void QuickPreviewClose_Click(object sender, RoutedEventArgs e) => CloseQuickPreview();

    private void QuickPreviewOverlay_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource == QuickPreviewBackdrop)
            CloseQuickPreview();
    }

    private void QuickPreviewCard_MouseDown(object sender, MouseButtonEventArgs e)
        => e.Handled = true;

    // ── Jump-to-letter ────────────────────────────────────────────────────

    private string   _jumpBuffer   = string.Empty;
    private DateTime _lastJumpTime = DateTime.MinValue;
    private const int JumpTimeoutMs = 700;

    private void JumpToLetter(char c, ItemsControl? list)
    {
        var items = Tab?.Items;
        if (items == null || list == null) return;

        var now = DateTime.UtcNow;
        if ((now - _lastJumpTime).TotalMilliseconds > JumpTimeoutMs)
            _jumpBuffer = string.Empty;
        _lastJumpTime = now;
        _jumpBuffer  += c.ToString();

        var match = items.FirstOrDefault(i =>
            i.Name.StartsWith(_jumpBuffer, StringComparison.OrdinalIgnoreCase));

        // If no match for accumulated buffer, fall back to just the new char
        if (match == null && _jumpBuffer.Length > 1)
        {
            _jumpBuffer = c.ToString();
            match = items.FirstOrDefault(i =>
                i.Name.StartsWith(_jumpBuffer, StringComparison.OrdinalIgnoreCase));
        }

        if (match == null) return;
        if (list is ListView lv) { lv.SelectedItem = match; lv.ScrollIntoView(match); }
        else if (list is ListBox lb) { lb.SelectedItem = match; lb.ScrollIntoView(match); }
    }

    private static char? KeyToChar(Key key)
    {
        if (key >= Key.A && key <= Key.Z) return (char)('a' + (key - Key.A));
        if (key >= Key.D0 && key <= Key.D9) return (char)('0' + (key - Key.D0));
        if (key >= Key.NumPad0 && key <= Key.NumPad9) return (char)('0' + (key - Key.NumPad0));
        return null;
    }

    // ── Drag-drop ──────────────────────────────────────────────────────────
    private void List_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(null);
        _suppressSelectionReset = false;
        _suppressedList = null;

        // When clicking on an already-selected item with multiple items selected
        // (and no modifier key), suppress the ListView's selection reset so all
        // selected items remain selected for the drag. We undo this on mouse-up
        // if the user didn't actually drag.
        if (sender is not ListBox list || list.SelectedItems.Count <= 1) return;
        if (Keyboard.Modifiers != ModifierKeys.None) return;

        var hit = VisualTreeHelper.HitTest(list, e.GetPosition(list))?.VisualHit;
        for (var el = (DependencyObject?)hit; el != null; el = VisualTreeHelper.GetParent(el))
        {
            if (el is ListViewItem { IsSelected: true } or ListBoxItem { IsSelected: true })
            {
                e.Handled = true;
                _suppressSelectionReset = true;
                _suppressedList = list;
                return;
            }
        }
    }

    private void List_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // If we suppressed a selection reset and no drag happened, perform a
        // normal single-click selection now (select only the clicked item).
        if (!_suppressSelectionReset || _suppressedList is not ListBox list) return;
        _suppressSelectionReset = false;
        _suppressedList = null;

        var hit = VisualTreeHelper.HitTest((Visual)sender, e.GetPosition((IInputElement)sender))?.VisualHit;
        for (var el = (DependencyObject?)hit; el != null; el = VisualTreeHelper.GetParent(el))
        {
            if (el is ListBoxItem container)
            {
                list.UnselectAll();
                container.IsSelected = true;
                return;
            }
        }
    }

    private void List_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        var pos  = e.GetPosition(null);
        var diff = _dragStart - pos;
        if (Math.Abs(diff.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(diff.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        if (Tab is not { } tab || tab.SelectedItems.Count == 0) return;

        _suppressSelectionReset = false;
        _suppressedList = null;

        var files = tab.SelectedItems.Select(i => i.FullPath).ToArray();
        var data  = new DataObject(DataFormats.FileDrop, files);
        DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Copy | DragDropEffects.Move);
    }

    private void List_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void List_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        if (Tab is not { } tab) return;
        _ = tab.DropFilesAsync(files, tab.CurrentPath);
    }
}
