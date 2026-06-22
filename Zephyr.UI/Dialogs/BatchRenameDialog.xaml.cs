using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace Zephyr.UI.Dialogs;

public partial class BatchRenameDialog : Window
{
    private readonly List<string> _paths;
    public  List<(string OldPath, string NewName)> Results { get; private set; } = [];

    private record PreviewRow(string Original, string NewName);

    public BatchRenameDialog(IEnumerable<string> paths)
    {
        InitializeComponent();
        _paths = paths.ToList();
        PatternBox.Text = "{name}.{ext}";
        SourceInitialized += (_, _) => ApplyDarkTitleBar();
        UpdatePreview();
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private void ApplyDarkTitleBar()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        int val = 1;
        DwmSetWindowAttribute(hwnd, 20, ref val, sizeof(int));
    }

    private void Token_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b) return;
        var token = b.Tag?.ToString() ?? "";
        var idx = PatternBox.CaretIndex;
        PatternBox.Text = PatternBox.Text.Insert(idx, token);
        PatternBox.CaretIndex = idx + token.Length;
        PatternBox.Focus();
    }

    private void Pattern_Changed(object sender, RoutedEventArgs e) => UpdatePreview();

    private void UpdatePreview()
    {
        if (PreviewList is null) return;
        int start = int.TryParse(StartNumBox?.Text, out var n) ? n : 1;
        var rows = new List<PreviewRow>(_paths.Count);
        for (int i = 0; i < _paths.Count; i++)
            rows.Add(new PreviewRow(Path.GetFileName(_paths[i]), ComputeName(_paths[i], i, start)));
        PreviewList.ItemsSource = rows;
    }

    private string ComputeName(string path, int index, int start)
    {
        var ext      = Path.GetExtension(path).TrimStart('.');
        var nameOnly = Path.GetFileNameWithoutExtension(path);
        var pattern  = PatternBox?.Text ?? "{name}.{ext}";
        var n        = index + start;

        var result = pattern
            .Replace("{name}", nameOnly)
            .Replace("{ext}",  ext)
            .Replace("{nnn}",  n.ToString("D3"))
            .Replace("{nn}",   n.ToString("D2"))
            .Replace("{n}",    n.ToString());

        var find    = FindBox?.Text ?? "";
        var replace = ReplaceBox?.Text ?? "";
        if (!string.IsNullOrEmpty(find))
            result = result.Replace(find, replace, StringComparison.OrdinalIgnoreCase);

        if      (CaseLower?.IsChecked == true) result = result.ToLowerInvariant();
        else if (CaseUpper?.IsChecked == true) result = result.ToUpperInvariant();
        else if (CaseTitle?.IsChecked == true) result = CultureInfo.CurrentCulture.TextInfo.ToTitleCase(result.ToLowerInvariant());

        return result;
    }

    private void Rename_Click(object sender, RoutedEventArgs e)
    {
        int start = int.TryParse(StartNumBox?.Text, out var n) ? n : 1;
        Results = [];
        for (int i = 0; i < _paths.Count; i++)
        {
            var newName = ComputeName(_paths[i], i, start);
            if (newName != Path.GetFileName(_paths[i]))
                Results.Add((_paths[i], newName));
        }
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
