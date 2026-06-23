using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Microsoft.Win32;

namespace Zephyr.UI.Dialogs;

public partial class AddNetworkLocationDialog : Window
{
    public string LocationPath { get; private set; } = "";
    public string LocationName { get; private set; } = "";

    private bool _nameEditedManually;

    public AddNetworkLocationDialog()
    {
        InitializeComponent();
        NameBox.TextChanged += (_, _) => { if (NameBox.IsKeyboardFocusWithin) _nameEditedManually = true; };
        SourceInitialized += (_, _) => ApplyDarkTitleBar();
        Loaded += (_, _) => PathBox.Focus();
    }

    private void Path_Changed(object sender, TextChangedEventArgs e)
    {
        // Auto-fill the display name from the path's last segment until the user edits it.
        if (_nameEditedManually) return;
        var path = PathBox.Text.Trim().TrimEnd('\\', '/');
        NameBox.Text = path.Length == 0 ? "" : Path.GetFileName(path) is { Length: > 0 } leaf ? leaf : path;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Select a folder or network location" };
        if (!string.IsNullOrWhiteSpace(PathBox.Text)) dlg.InitialDirectory = PathBox.Text.Trim();
        if (dlg.ShowDialog(this) == true) PathBox.Text = dlg.FolderName;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var path = PathBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(path))
        {
            ZephyrMessageBox.Show("Enter a location to pin.", "Add Network Location", this);
            return;
        }

        // Network shares may be temporarily offline — warn but allow pinning anyway.
        if (!Directory.Exists(path) &&
            !ZephyrMessageBox.Confirm(
                $"“{path}” isn't reachable right now. Pin it anyway?",
                "Add Network Location", "Pin", this))
            return;

        LocationPath = path;
        LocationName = string.IsNullOrWhiteSpace(NameBox.Text) ? path : NameBox.Text.Trim();
        DialogResult = true;
    }

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
