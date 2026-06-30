using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Zephyr.Core.Models;

namespace Zephyr.UI.Controls;

// Details-view column management: sort-on-click, proportional auto-sizing, user-resize
// rebalancing (so the Name column always absorbs slack), and show/hide via the header
// right-click menu.
public partial class FilePane
{
    private GridViewColumnHeader? _lastSortHeader;

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
}
