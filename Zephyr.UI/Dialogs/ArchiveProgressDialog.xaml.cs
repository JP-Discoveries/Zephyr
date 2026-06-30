using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Zephyr.Core.Archives;

namespace Zephyr.UI.Dialogs;

/// <summary>
/// Modal progress dialog for a compress/extract operation. Runs the supplied work,
/// shows a live progress bar with an ETA, and supports cancellation.
/// </summary>
public partial class ArchiveProgressDialog : Window
{
    private readonly Func<IProgress<ZephyrArchiveService.ArchiveProgress>, CancellationToken, Task> _work;
    private readonly CancellationTokenSource _cts = new();
    private readonly Stopwatch _stopwatch = new();
    private bool _completed;

    /// <summary>Set if the operation threw (other than cancellation).</summary>
    public Exception? Error { get; private set; }

    /// <summary>True if the user cancelled.</summary>
    public bool Canceled { get; private set; }

    /// <summary>Shows the progress dialog for an archive operation and surfaces any
    /// error to the user. Shared by the toolbar commands and the context menu.</summary>
    public static void Run(Window? owner, string title,
        Func<IProgress<ZephyrArchiveService.ArchiveProgress>, CancellationToken, Task> work)
    {
        var dlg = new ArchiveProgressDialog(title, work) { Owner = owner };
        dlg.ShowDialog();
        if (dlg.Error is { } ex) ZephyrMessageBox.Show(ex.Message, "Error");
    }

    public ArchiveProgressDialog(string title,
        Func<IProgress<ZephyrArchiveService.ArchiveProgress>, CancellationToken, Task> work)
    {
        InitializeComponent();
        TitleText.Text = title;
        Title          = title;
        _work          = work;
        SourceInitialized += (_, _) => ApplyDarkTitleBar();
        Loaded += OnLoaded;
        Closing += (_, e) =>
        {
            // X button / Alt-F4 cancels rather than closing mid-operation.
            if (!_completed) { _cts.Cancel(); e.Cancel = true; }
        };
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private void ApplyDarkTitleBar()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        int val = 1;
        DwmSetWindowAttribute(hwnd, 20, ref val, sizeof(int));
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _stopwatch.Start();
        var progress = new Progress<ZephyrArchiveService.ArchiveProgress>(OnProgress);
        try
        {
            await _work(progress, _cts.Token);
            _completed   = true;
            DialogResult = true;
        }
        catch (OperationCanceledException)
        {
            _completed = true;
            Canceled   = true;
            DialogResult = false;
        }
        catch (Exception ex)
        {
            _completed = true;
            Error      = ex;
            DialogResult = false;
        }
    }

    private void OnProgress(ZephyrArchiveService.ArchiveProgress p)
    {
        if (Bar.IsIndeterminate) Bar.IsIndeterminate = false;
        Bar.Value     = p.Fraction * 100;
        FileText.Text = p.CurrentEntry;

        var processed = FormatBytes(p.ProcessedBytes);
        var total     = p.TotalBytes > 0 ? FormatBytes(p.TotalBytes) : "—";
        var elapsed   = _stopwatch.Elapsed.TotalSeconds;

        if (p.ProcessedBytes > 0 && p.TotalBytes > 0 && elapsed > 0.6)
        {
            double rate      = p.ProcessedBytes / elapsed;            // cumulative average — stable
            double remaining = (p.TotalBytes - p.ProcessedBytes) / rate;
            StatsText.Text   = $"{processed} / {total}  ·  about {FormatTime(remaining)} remaining";
        }
        else
        {
            StatsText.Text = $"{processed} / {total}";
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _cts.Cancel();
        CancelButton.IsEnabled = false;
        CancelButton.Content   = "Cancelling…";
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double size = bytes;
        int u = 0;
        while (size >= 1024 && u < units.Length - 1) { size /= 1024; u++; }
        return u == 0 ? $"{bytes} B" : $"{size:0.#} {units[u]}";
    }

    private static string FormatTime(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0) return "…";
        if (seconds < 1)   return "less than a second";
        if (seconds < 60)  return $"{Math.Ceiling(seconds):0}s";
        if (seconds < 3600)
        {
            int m = (int)(seconds / 60), s = (int)(seconds % 60);
            return s > 0 ? $"{m}m {s}s" : $"{m}m";
        }
        int h = (int)(seconds / 3600), min = (int)(seconds % 3600 / 60);
        return min > 0 ? $"{h}h {min}m" : $"{h}h";
    }
}
