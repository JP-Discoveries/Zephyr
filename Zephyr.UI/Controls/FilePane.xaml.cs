using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Zephyr.Core.Models;
using Zephyr.UI.Services;
using Zephyr.UI.ViewModels;

namespace Zephyr.UI.Controls;

// File pane code-behind core: lifecycle wiring, tab/pane observation, navigation,
// breadcrumb/address-bar editing, search box, selection, and drag-drop. The larger,
// self-contained concerns live in sibling files:
//   FilePane.Columns.cs        — details-view column sort/size/show-hide
//   FilePane.TabDrag.cs        — tab reorder / split / new-window drag (Win32 hook)
//   FilePane.QuickPreview.cs   — space-bar Quick Look overlay + jump-to-letter
//   FileContextMenuBuilder.cs  — the right-click command menu
public partial class FilePane : UserControl
{
    private Point   _dragStart;
    private bool    _suppressSelectionReset;
    private ListBox? _suppressedList;

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
    // Shared by the details, search-results, and icon views (all wired in XAML).
    private void OpenItem_DoubleClick(object sender, MouseButtonEventArgs e)
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

        new FileContextMenuBuilder(this).Show(item, tab, (UIElement)el);
        e.Handled = true;
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
