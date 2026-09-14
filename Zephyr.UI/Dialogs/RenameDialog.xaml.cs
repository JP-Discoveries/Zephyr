using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace Zephyr.UI.Dialogs;

/// <summary>
/// Rename prompt that keeps the base name and the extension in separate boxes so the
/// extension can't be wiped out by an accidental select-all-and-type.
/// </summary>
public partial class RenameDialog : Window
{
    public string Result { get; private set; } = string.Empty;

    public RenameDialog(string originalName, bool isDirectory)
    {
        InitializeComponent();

        SplitName(originalName, isDirectory, out var baseName, out var extension);
        NameBox.Text = baseName;
        ExtBox.Text  = extension;

        if (isDirectory)
        {
            // Folders have no extension to protect - hide the second box entirely.
            ExtLabel.Visibility = Visibility.Collapsed;
            DotLabel.Visibility = Visibility.Collapsed;
            ExtBox.Visibility   = Visibility.Collapsed;
        }

        SourceInitialized += (_, _) => ApplyDarkTitleBar();
        Loaded += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
    }

    /// <summary>
    /// Splits into base name + extension (without the dot). Leading-dot names such as
    /// ".gitignore" are treated as all base name, matching how Explorer shows them.
    /// </summary>
    private static void SplitName(string name, bool isDirectory, out string baseName, out string extension)
    {
        baseName  = name;
        extension = string.Empty;
        if (isDirectory) return;

        var dot = name.LastIndexOf('.');
        if (dot <= 0 || dot == name.Length - 1) return;

        baseName  = name[..dot];
        extension = name[(dot + 1)..];
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private void ApplyDarkTitleBar()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        int val = 1;
        DwmSetWindowAttribute(hwnd, 20, ref val, sizeof(int));
    }

    private void OK_Click(object sender, RoutedEventArgs e)     => Commit();
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Box_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)  { Commit();             e.Handled = true; }
        if (e.Key == Key.Escape) { DialogResult = false; e.Handled = true; }
    }

    private void Commit()
    {
        var baseName = NameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(baseName)) return;

        var extension = ExtBox.Visibility == Visibility.Visible
            ? ExtBox.Text.Trim().TrimStart('.')
            : string.Empty;

        Result       = string.IsNullOrEmpty(extension) ? baseName : $"{baseName}.{extension}";
        DialogResult = true;
    }
}
