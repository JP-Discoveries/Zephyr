using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Zephyr.Core.FileSystem;
using Zephyr.UI.Services;

namespace Zephyr.UI.Dialogs;

public partial class DiskUsageWindow : Window
{
    private readonly string _rootPath;
    private CancellationTokenSource? _cts;
    private bool _scanning;

    private UsageNode? _displayRoot;
    private string _sortKey = "Bytes";
    private ListSortDirection _sortDir = ListSortDirection.Descending;

    public DiskUsageWindow(string folderPath)
    {
        InitializeComponent();
        _rootPath = folderPath;
        PathText.Text = folderPath;

        Treemap.HoverChanged   += OnHoverChanged;
        Treemap.DrillRequested += OnDrillRequested;

        SourceInitialized += (_, _) => ApplyDarkTitleBar();
        Loaded  += async (_, _) => await ScanAsync();
        Closed  += (_, _) => _cts?.Cancel();
    }

    private async Task ScanAsync()
    {
        _cts = new CancellationTokenSource();
        _scanning = true;
        ScanProgress.Visibility = Visibility.Visible;
        CancelButton.Content = "Cancel";

        var progress = new Progress<long>(n => StatusText.Text = $"Scanning… {n:N0} files");
        try
        {
            var root = await DiskUsageScanner.ScanAsync(_rootPath, progress, _cts.Token);
            _scanning = false;
            ScanProgress.Visibility = Visibility.Collapsed;
            CancelButton.Content = "Close";

            if (root.Bytes == 0)
            {
                StatusText.Text = "This folder is empty (or its contents are inaccessible).";
                TotalText.Text  = "0 B";
                return;
            }

            FolderTree.ItemsSource = new[] { root };
            FolderTree.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (FolderTree.ItemContainerGenerator.ContainerFromIndex(0) is TreeViewItem tvi)
                    tvi.IsExpanded = true;
            }), DispatcherPriority.Loaded);

            ShowNode(root);
        }
        catch (OperationCanceledException)
        {
            Close();
        }
        catch (Exception ex)
        {
            _scanning = false;
            ScanProgress.Visibility = Visibility.Collapsed;
            CancelButton.Content = "Close";
            StatusText.Text = $"Error: {ex.Message}";
        }
    }

    private void ShowNode(UsageNode node)
    {
        _displayRoot = node;
        Treemap.SetRoot(node);
        PathText.Text  = node.FullPath;
        TotalText.Text = $"{Format(node.Bytes)}  ·  {node.Files:N0} files";
        UpButton.IsEnabled = node.Parent is not null;
        BuildTypeTable(node);
        StatusText.Text = "Hover a block to see its file and size · double-click a folder to zoom in";
    }

    private void OnHoverChanged(UsageNode? node)
    {
        StatusText.Text = node is null
            ? "Hover a block to see its file and size · double-click a folder to zoom in"
            : $"{node.FullPath}  —  {Format(node.Bytes)}";
    }

    // Double-clicking a region selects it in the tree, which drives the view.
    private void OnDrillRequested(UsageNode node)
    {
        if (!node.HasChildren) return;
        SelectInTree(node);
        if (!ReferenceEquals(_displayRoot, node)) ShowNode(node);
    }

    private void FolderTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is UsageNode node && !ReferenceEquals(node, _displayRoot)) ShowNode(node);
    }

    private void Up_Click(object sender, RoutedEventArgs e)
    {
        if (_displayRoot?.Parent is not { } parent) return;
        SelectInTree(parent);
        if (!ReferenceEquals(_displayRoot, parent)) ShowNode(parent);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_scanning) _cts?.Cancel();
        else Close();
    }

    // Expand ancestors and select the node in the tree. TreeView isn't virtualised, so
    // containers materialise synchronously after expanding + a layout pass.
    private void SelectInTree(UsageNode node)
    {
        var chain = new List<UsageNode>();
        for (var n = node; n is not null; n = n.Parent) chain.Add(n);
        chain.Reverse();

        ItemsControl container = FolderTree;
        for (int i = 0; i < chain.Count; i++)
        {
            container.UpdateLayout();
            if (container.ItemContainerGenerator.ContainerFromItem(chain[i]) is not TreeViewItem tvi) return;
            if (i == chain.Count - 1)
            {
                tvi.IsSelected = true;
                tvi.BringIntoView();
            }
            else
            {
                tvi.IsExpanded = true;
                tvi.UpdateLayout();
                container = tvi;
            }
        }
    }

    // ── File-type table ───────────────────────────────────────────────────────

    private void BuildTypeTable(UsageNode root)
    {
        var stats = new Dictionary<string, (long bytes, long files)>(StringComparer.OrdinalIgnoreCase);
        AccumulateTypes(root, stats);

        long total = root.Bytes;
        var rows = stats.Select(kv => new TypeStatRow
        {
            Ext      = kv.Key.Length == 0 ? "(none)" : "." + kv.Key,
            FileType = FileTypeDescriptionService.Describe(kv.Key),
            Swatch   = FileTypeColorService.GetBrush(kv.Key),
            Bytes    = kv.Value.bytes,
            Files    = kv.Value.files,
            Percent  = total > 0 ? kv.Value.bytes * 100.0 / total : 0,
        }).ToList();

        FileTypeList.ItemsSource = rows;
        var view = CollectionViewSource.GetDefaultView(rows);
        view.SortDescriptions.Clear();
        view.SortDescriptions.Add(new SortDescription(_sortKey, _sortDir));
        view.Refresh();
    }

    private void TypeHeader_Click(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader { Content: string headerText }) return;
        string? key = headerText switch
        {
            "Extension" => "Ext",
            "File Type" => "FileType",
            "%"         => "Percent",
            "Size"      => "Bytes",
            "Files"     => "Files",
            _           => null,
        };
        if (key is null) return;

        if (_sortKey == key)
            _sortDir = _sortDir == ListSortDirection.Ascending
                ? ListSortDirection.Descending : ListSortDirection.Ascending;
        else
        {
            _sortKey = key;
            // Text columns read best ascending; numeric columns descending (biggest first).
            _sortDir = key is "Ext" or "FileType"
                ? ListSortDirection.Ascending : ListSortDirection.Descending;
        }

        if (FileTypeList.ItemsSource is null) return;
        var view = CollectionViewSource.GetDefaultView(FileTypeList.ItemsSource);
        view.SortDescriptions.Clear();
        view.SortDescriptions.Add(new SortDescription(_sortKey, _sortDir));
        view.Refresh();
    }

    private static void AccumulateTypes(UsageNode node, Dictionary<string, (long bytes, long files)> stats)
    {
        if (!node.IsDirectory)
        {
            var key = node.Extension.TrimStart('.');
            var cur = stats.GetValueOrDefault(key);
            stats[key] = (cur.bytes + node.Bytes, cur.files + 1);
            return;
        }
        foreach (var child in node.Children) AccumulateTypes(child, stats);
    }

    private static string Format(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):F2} GB"
    };

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private void ApplyDarkTitleBar()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        int val  = 1;
        DwmSetWindowAttribute(hwnd, 20, ref val, sizeof(int));
    }
}

/// <summary>One row in the file-type table.</summary>
public sealed class TypeStatRow
{
    public required string Ext { get; init; }
    public required string FileType { get; init; }
    public required Brush Swatch { get; init; }
    public long Bytes { get; init; }
    public long Files { get; init; }
    public double Percent { get; init; }

    public string SizeDisplay    => Bytes switch
    {
        < 1024 => $"{Bytes} B",
        < 1024 * 1024 => $"{Bytes / 1024.0:F1} KB",
        < 1024L * 1024 * 1024 => $"{Bytes / (1024.0 * 1024):F1} MB",
        _ => $"{Bytes / (1024.0 * 1024 * 1024):F2} GB"
    };
    public string FilesDisplay   => Files.ToString("N0");
    public string PercentDisplay => Percent.ToString("0.0") + "%";
}
