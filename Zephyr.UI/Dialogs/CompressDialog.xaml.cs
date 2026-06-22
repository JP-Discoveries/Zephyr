using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Microsoft.Win32;
using Zephyr.Core.Archives;

namespace Zephyr.UI.Dialogs;

public partial class CompressDialog : Window
{
    private readonly int _sourceCount;

    /// <summary>Full path of the archive to create. Valid only when DialogResult is true.</summary>
    public string ResultPath { get; private set; } = "";

    /// <summary>Chosen compression options. Valid only when DialogResult is true.</summary>
    public ZephyrArchiveService.CompressOptions Options { get; private set; } = new();

    /// <summary>True when ResultPath is an existing .zip the user chose to add to (not overwrite).</summary>
    public bool AddToExisting { get; private set; }

    public CompressDialog(string defaultName, string defaultLocation, int sourceCount)
    {
        InitializeComponent();
        _sourceCount      = sourceCount;
        NameBox.Text      = defaultName;
        LocationBox.Text  = defaultLocation;
        SourceInitialized += (_, _) => ApplyDarkTitleBar();
        Loaded += (_, _) =>
        {
            UpdateSummary();
            NameBox.Focus();
            NameBox.SelectAll();
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

    private (ZephyrArchiveService.WriteFormat Format, string Ext) SelectedFormat()
    {
        var tag = (FormatBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "Zip";
        return tag switch
        {
            "TarGz" => (ZephyrArchiveService.WriteFormat.TarGz, ".tar.gz"),
            "Tar"   => (ZephyrArchiveService.WriteFormat.Tar,   ".tar"),
            "Gz"    => (ZephyrArchiveService.WriteFormat.Gz,    ".gz"),
            _       => (ZephyrArchiveService.WriteFormat.Zip,   ".zip"),
        };
    }

    private ZephyrArchiveService.Level SelectedLevel()
    {
        var tag = (LevelBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "Normal";
        return Enum.TryParse<ZephyrArchiveService.Level>(tag, out var lvl) ? lvl : ZephyrArchiveService.Level.Normal;
    }

    private void Format_Changed(object sender, SelectionChangedEventArgs e) => UpdateSummary();
    private void Input_Changed(object sender, RoutedEventArgs e) => UpdateSummary();
    private void Password_Changed(object sender, RoutedEventArgs e) => UpdateSummary();

    private ZephyrArchiveService.ZipEncryption SelectedEncryption()
    {
        var tag = (EncMethodBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "Aes256";
        return Enum.TryParse<ZephyrArchiveService.ZipEncryption>(tag, out var m) ? m : ZephyrArchiveService.ZipEncryption.Aes256;
    }

    private void UpdateSummary()
    {
        if (SummaryText is null) return;
        var (format, ext) = SelectedFormat();
        bool isZip = format == ZephyrArchiveService.WriteFormat.Zip;

        // Encryption is only available for .zip.
        EncryptionPanel.Visibility = isZip ? Visibility.Visible : Visibility.Collapsed;

        // .gz is a single-stream format and only the .tar variants carry compression for tar.
        bool gzSingleFileIssue = format == ZephyrArchiveService.WriteFormat.Gz && _sourceCount != 1;
        // Store/Tar have no per-file compression; level only affects zip and tar.gz/gz.
        LevelBox.IsEnabled = format != ZephyrArchiveService.WriteFormat.Tar;

        bool hasPw    = isZip && PasswordBox.Password.Length > 0;
        var  name     = NameBox.Text.Trim();
        var  fileName = name + ext;
        if (gzSingleFileIssue)
            SummaryText.Text = "⚠ .gz can only hold a single file. Use .tar.gz to bundle multiple items.";
        else if (hasPw)
            SummaryText.Text = $"Creates encrypted \"{fileName}\".";
        else if (isZip && TargetZipExists(name, ext))
            SummaryText.Text = $"\"{fileName}\" already exists — files will be added to it.";
        else
            SummaryText.Text = $"Creates \"{fileName}\" in the selected location.";
    }

    // An existing .zip at the chosen name/location → we append rather than overwrite.
    private bool TargetZipExists(string name, string ext)
    {
        if (!ext.Equals(".zip", StringComparison.OrdinalIgnoreCase)) return false;
        if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) name = name[..^ext.Length];
        return Directory.Exists(LocationBox.Text) && File.Exists(Path.Combine(LocationBox.Text, name + ext));
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog
        {
            Title            = "Choose output location",
            InitialDirectory = Directory.Exists(LocationBox.Text) ? LocationBox.Text : "",
        };
        if (dlg.ShowDialog() == true)
            LocationBox.Text = dlg.FolderName;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            ZephyrMessageBox.Show("Please enter an archive name.", "Compress");
            return;
        }
        if (!Directory.Exists(LocationBox.Text))
        {
            ZephyrMessageBox.Show("The selected location does not exist.", "Compress");
            return;
        }

        var (format, ext) = SelectedFormat();
        if (format == ZephyrArchiveService.WriteFormat.Gz && _sourceCount != 1)
        {
            ZephyrMessageBox.Show(".gz can only compress a single file. Choose .tar.gz instead.", "Compress");
            return;
        }

        // Encryption (zip only). A password requires a matching confirmation.
        string? password = null;
        if (format == ZephyrArchiveService.WriteFormat.Zip && PasswordBox.Password.Length > 0)
        {
            if (PasswordBox.Password != ConfirmBox.Password)
            {
                ZephyrMessageBox.Show("The passwords do not match.", "Compress");
                return;
            }
            password = PasswordBox.Password;
        }

        // Strip a redundant trailing extension the user may have typed.
        if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            name = name[..^ext.Length];

        var target = Path.Combine(LocationBox.Text, name + ext);
        // Append only makes sense for an unencrypted new entry into an existing plain zip.
        if (password is null && format == ZephyrArchiveService.WriteFormat.Zip && File.Exists(target))
        {
            AddToExisting = true;
            ResultPath    = target;
        }
        else
        {
            ResultPath = UniquePath(target, ext);
        }
        Options      = new ZephyrArchiveService.CompressOptions(format, SelectedLevel(), password, SelectedEncryption());
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private static string UniquePath(string path, string ext)
    {
        if (!File.Exists(path)) return path;
        var dir  = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileName(path);
        stem     = stem[..^ext.Length]; // remove compound ext safely
        int n = 2;
        string candidate;
        do { candidate = Path.Combine(dir, $"{stem} ({n++}){ext}"); }
        while (File.Exists(candidate));
        return candidate;
    }
}
