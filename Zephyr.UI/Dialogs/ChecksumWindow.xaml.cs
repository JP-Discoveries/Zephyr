using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using Zephyr.Core.FileSystem;

namespace Zephyr.UI.Dialogs;

public partial class ChecksumWindow : Window
{
    private static readonly Brush MatchBrush    = Freeze("#16C60C");
    private static readonly Brush MismatchBrush = Freeze("#E81123");

    private readonly string _path;
    private CancellationTokenSource? _cts;
    private FileHashes? _hashes;

    public ChecksumWindow(string path)
    {
        InitializeComponent();
        _path = path;

        var info = new FileInfo(path);
        NameText.Text = info.Name;
        SubText.Text  = $"{FormatSize(info.Length)}  ·  {info.DirectoryName}";

        SourceInitialized += (_, _) => ApplyDarkTitleBar();
        Loaded += async (_, _) => await ComputeAsync();
    }

    private async Task ComputeAsync()
    {
        _cts = new CancellationTokenSource();
        SetComputingUi(true);
        StatusText.Text = "Computing…";

        var progress = new Progress<double>(p =>
        {
            HashProgress.Value = p;
            StatusText.Text    = $"{p * 100:0}%";
        });

        try
        {
            _hashes = await HashService.ComputeAsync(_path, progress, _cts.Token);
            Md5Box.Text    = _hashes.Md5;
            Sha1Box.Text   = _hashes.Sha1;
            Sha256Box.Text = _hashes.Sha256;
            SetComputingUi(false);
            UpdateVerify();
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Canceled";
            HashProgress.Visibility   = Visibility.Collapsed;
            CancelHashButton.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Error: {ex.Message}";
            HashProgress.Visibility     = Visibility.Collapsed;
            CancelHashButton.Visibility = Visibility.Collapsed;
        }
    }

    // While computing, show the progress bar + cancel and disable compare.
    private void SetComputingUi(bool computing)
    {
        ProgressRow.Visibility  = computing ? Visibility.Visible : Visibility.Collapsed;
        CompareButton.IsEnabled = !computing;
    }

    private void CancelHash_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: TextBox box } && !string.IsNullOrEmpty(box.Text))
        {
            try { Clipboard.SetText(box.Text); } catch { /* clipboard busy */ }
        }
    }

    private void VerifyBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateVerify();

    private void UpdateVerify()
    {
        var expected = Normalize(VerifyBox.Text);
        if (_hashes is null || expected.Length == 0)
        {
            VerifyResult.Text = "";
            return;
        }

        string? matched =
            expected == _hashes.Md5    ? "MD5"     :
            expected == _hashes.Sha1   ? "SHA-1"   :
            expected == _hashes.Sha256 ? "SHA-256" : null;

        if (matched is not null)
        {
            VerifyResult.Text       = $"✓ Match ({matched})";
            VerifyResult.Foreground = MatchBrush;
        }
        else
        {
            VerifyResult.Text       = "✗ No match";
            VerifyResult.Foreground = MismatchBrush;
        }
    }

    private async void Compare_Click(object sender, RoutedEventArgs e)
    {
        if (_hashes is null) return;

        var dlg = new OpenFileDialog { Title = "Compare with file", CheckFileExists = true };
        if (dlg.ShowDialog(this) != true) return;

        CompareButton.IsEnabled = false;
        CompareResult.Foreground = (Brush)FindResource("ZephyrTextSecondary");
        CompareResult.Text = "Hashing…";
        try
        {
            var other = await HashService.ComputeAsync(dlg.FileName, null, CancellationToken.None);
            bool same = other.Sha256 == _hashes.Sha256;
            CompareResult.Text       = same
                ? $"✓ Identical to {Path.GetFileName(dlg.FileName)}"
                : $"✗ Differs from {Path.GetFileName(dlg.FileName)}";
            CompareResult.Foreground = same ? MatchBrush : MismatchBrush;
        }
        catch (Exception ex)
        {
            CompareResult.Text       = $"Error: {ex.Message}";
            CompareResult.Foreground = MismatchBrush;
        }
        finally { CompareButton.IsEnabled = true; }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        Close();
    }

    // Strips whitespace and lowercases so pasted hashes compare regardless of formatting.
    private static string Normalize(string s) =>
        new string(s.Where(c => !char.IsWhiteSpace(c)).ToArray()).ToLowerInvariant();

    private static Brush Freeze(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):F1} GB"
    };

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private void ApplyDarkTitleBar()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        int val = 1;
        DwmSetWindowAttribute(hwnd, 20, ref val, sizeof(int));
    }
}
