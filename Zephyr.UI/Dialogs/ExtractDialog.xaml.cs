using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace Zephyr.UI.Dialogs;

public partial class ExtractDialog : Window
{
    /// <summary>Destination folder. Valid only when DialogResult is true.</summary>
    public string Destination { get; private set; } = "";

    /// <summary>When extracting multiple archives, give each its own subfolder.</summary>
    public bool EachToOwnSubfolder { get; private set; } = true;

    /// <summary>Password for encrypted archives, or null if left blank.</summary>
    public string? Password { get; private set; }

    public ExtractDialog(IReadOnlyList<string> archiveNames, string defaultDestination)
    {
        InitializeComponent();
        DestBox.Text = defaultDestination;

        if (archiveNames.Count == 1)
            HeaderText.Text = $"Extract \"{archiveNames[0]}\"";
        else
        {
            HeaderText.Text     = $"Extract {archiveNames.Count} archives";
            LayoutPanel.Visibility = Visibility.Visible;
        }

        SourceInitialized += (_, _) => ApplyDarkTitleBar();
        Loaded += (_, _) => DestBox.Focus();
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private void ApplyDarkTitleBar()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        int val = 1;
        DwmSetWindowAttribute(hwnd, 20, ref val, sizeof(int));
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog
        {
            Title            = "Choose extraction folder",
            InitialDirectory = Directory.Exists(DestBox.Text) ? DestBox.Text : "",
        };
        if (dlg.ShowDialog() == true)
            DestBox.Text = dlg.FolderName;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(DestBox.Text))
        {
            ZephyrMessageBox.Show("Please choose a destination folder.", "Extract");
            return;
        }

        Destination        = DestBox.Text;
        EachToOwnSubfolder = SubfolderRadio.IsChecked == true;
        Password           = string.IsNullOrEmpty(PasswordBox.Password) ? null : PasswordBox.Password;
        DialogResult       = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
