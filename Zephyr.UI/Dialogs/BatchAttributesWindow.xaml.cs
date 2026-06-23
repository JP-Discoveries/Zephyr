using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Zephyr.Core.FileSystem;

namespace Zephyr.UI.Dialogs;

public partial class BatchAttributesWindow : Window
{
    private const string TimeFormat = "yyyy-MM-dd HH:mm:ss";
    private readonly IReadOnlyList<string> _paths;

    public BatchAttributesWindow(IReadOnlyList<string> paths)
    {
        InitializeComponent();
        _paths = paths;

        HeaderText.Text = paths.Count == 1
            ? Path.GetFileName(paths[0].TrimEnd('\\', '/'))
            : $"{paths.Count} items selected";

        Prefill();

        SourceInitialized += (_, _) => ApplyDarkTitleBar();
    }

    // Seed the (disabled) timestamp boxes with the first item's current values so the
    // user has a sensible starting point when they tick a box.
    private void Prefill()
    {
        try
        {
            var first = _paths[0];
            DateTime created, modified, accessed;
            if (Directory.Exists(first))
            {
                created  = Directory.GetCreationTime(first);
                modified = Directory.GetLastWriteTime(first);
                accessed = Directory.GetLastAccessTime(first);
            }
            else
            {
                var fi = new FileInfo(first);
                created  = fi.CreationTime;
                modified = fi.LastWriteTime;
                accessed = fi.LastAccessTime;
            }
            CreatedBox.Text  = created.ToString(TimeFormat);
            ModifiedBox.Text = modified.ToString(TimeFormat);
            AccessedBox.Text = accessed.ToString(TimeFormat);
        }
        catch { /* leave boxes empty */ }
    }

    private void TimestampToggle(object sender, RoutedEventArgs e)
    {
        if (sender == CreatedCheck)  CreatedBox.IsEnabled  = CreatedCheck.IsChecked  == true;
        if (sender == ModifiedCheck) ModifiedBox.IsEnabled = ModifiedCheck.IsChecked == true;
        if (sender == AccessedCheck) AccessedBox.IsEnabled = AccessedCheck.IsChecked == true;
    }

    private void Now_Click(object sender, RoutedEventArgs e)
    {
        var now = DateTime.Now.ToString(TimeFormat);
        switch ((sender as Button)?.Tag as string)
        {
            case "Created":  CreatedCheck.IsChecked  = true; CreatedBox.Text  = now; break;
            case "Modified": ModifiedCheck.IsChecked = true; ModifiedBox.Text = now; break;
            case "Accessed": AccessedCheck.IsChecked = true; AccessedBox.Text = now; break;
        }
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        var edit = new AttributeEdit
        {
            ReadOnly  = FromCombo(ReadOnlyBox),
            Hidden    = FromCombo(HiddenBox),
            System    = FromCombo(SystemBox),
            Archive   = FromCombo(ArchiveBox),
            Recursive = RecursiveCheck.IsChecked == true,
        };

        if (!TryReadTimestamp(CreatedCheck,  CreatedBox,  "Created",  out var created))  return;
        if (!TryReadTimestamp(ModifiedCheck, ModifiedBox, "Modified", out var modified)) return;
        if (!TryReadTimestamp(AccessedCheck, AccessedBox, "Accessed", out var accessed)) return;
        edit.Created = created;
        edit.Modified = modified;
        edit.Accessed = accessed;

        if (!edit.HasWork)
        {
            ZephyrMessageBox.Show("Nothing to change — choose an attribute or timestamp to edit.",
                "Attributes & Timestamps", this);
            return;
        }

        IsEnabled = false;
        var (changed, failed) = await Task.Run(() => AttributeService.Apply(_paths, edit));
        IsEnabled = true;

        if (failed > 0)
            ZephyrMessageBox.Show(
                $"Updated {changed} item{(changed == 1 ? "" : "s")}. {failed} could not be changed " +
                "(in use, protected, or access denied).",
                "Attributes & Timestamps", this);

        DialogResult = true;
    }

    private bool TryReadTimestamp(CheckBox check, TextBox box, string label, out DateTime? value)
    {
        value = null;
        if (check.IsChecked != true) return true;
        if (DateTime.TryParse(box.Text.Trim(), out var dt)) { value = dt; return true; }

        ZephyrMessageBox.Show($"\"{box.Text}\" isn't a valid {label} date/time.\nUse {TimeFormat}.",
            "Attributes & Timestamps", this);
        box.Focus();
        return false;
    }

    // 0 = leave unchanged, 1 = set, 2 = clear
    private static bool? FromCombo(ComboBox box) => box.SelectedIndex switch { 1 => true, 2 => false, _ => null };

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private void ApplyDarkTitleBar()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        int val  = 1;
        DwmSetWindowAttribute(hwnd, 20, ref val, sizeof(int));
    }
}
