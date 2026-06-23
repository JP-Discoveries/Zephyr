using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;
using Zephyr.Core.FileSystem;

namespace Zephyr.UI.Dialogs;

public partial class CreateLinkDialog : Window
{
    private readonly string _target;

    public LinkKind SelectedKind { get; private set; }
    public string LinkPath { get; private set; } = "";
    public string TargetPath => _target;

    public CreateLinkDialog(string targetPath)
    {
        InitializeComponent();
        _target = targetPath;
        TargetText.Text = targetPath;

        bool isDir = Directory.Exists(targetPath);
        RbJunction.Visibility = isDir  ? Visibility.Visible : Visibility.Collapsed;
        RbHardlink.Visibility = !isDir ? Visibility.Visible : Visibility.Collapsed;

        // Default to the no-elevation option for the target kind.
        if (isDir) RbJunction.IsChecked = true; else RbHardlink.IsChecked = true;

        LocationBox.Text = Path.GetDirectoryName(targetPath) ?? "";

        var name = Path.GetFileName(targetPath.TrimEnd('\\', '/'));
        var ext  = isDir ? "" : Path.GetExtension(name);
        var bare = name[..^ext.Length];
        NameBox.Text = $"{bare} - link{ext}";

        SourceInitialized += (_, _) => ApplyDarkTitleBar();
        Loaded += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Choose where to create the link" };
        if (!string.IsNullOrWhiteSpace(LocationBox.Text)) dlg.InitialDirectory = LocationBox.Text.Trim();
        if (dlg.ShowDialog(this) == true) LocationBox.Text = dlg.FolderName;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var location = LocationBox.Text.Trim();
        var name     = NameBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(location) || !Directory.Exists(location))
        {
            ZephyrMessageBox.Show("Choose a valid folder to create the link in.", "Create Link", this);
            return;
        }
        if (string.IsNullOrWhiteSpace(name))
        {
            ZephyrMessageBox.Show("Enter a name for the link.", "Create Link", this);
            return;
        }

        SelectedKind = RbSymbolic.IsChecked == true ? LinkKind.Symbolic
                     : RbJunction.IsChecked == true ? LinkKind.Junction
                     : LinkKind.HardLink;
        LinkPath = Path.Combine(location, name);
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
