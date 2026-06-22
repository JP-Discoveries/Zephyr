using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace Zephyr.UI.Windows;

public partial class FilePropertiesWindow : Window
{
    private readonly string _path;

    // Pending changes from Customize tab (null = no change, "" = remove/restore)
    private string? _pendingIconResource;
    private string? _pendingFolderPicture;

    public FilePropertiesWindow(string path)
    {
        _path = path;
        InitializeComponent();
        SourceInitialized += (_, _) => ApplyDarkTitleBar();
        Loaded += (_, _) =>
        {
            LoadGeneral();
            LoadDetails();
            LoadSecurity();
            LoadCustomize();
        };
    }

    // ── General tab ───────────────────────────────────────────────────────────

    private void LoadGeneral()
    {
        bool isDir = Directory.Exists(_path);
        Title = Path.GetFileName(_path) + " Properties";

        if (isDir)
        {
            var di = new DirectoryInfo(_path);
            IconGlyph.Text    = "";
            NameText.Text     = di.Name;
            TypeText.Text     = "File folder";
            LocationText.Text = di.Parent?.FullName ?? _path;
            SizeText.Text     = "Calculating…";
            ContainsText.Text = "Calculating…";
            ContainsLabel.Visibility = Visibility.Visible;
            ContainsText.Visibility  = Visibility.Visible;
            CreatedText.Text  = di.CreationTime.ToString("dddd, MMMM d, yyyy,  h:mm:ss tt");
            ModifiedText.Text = di.LastWriteTime.ToString("dddd, MMMM d, yyyy,  h:mm:ss tt");
            AccessedText.Text = di.LastAccessTime.ToString("dddd, MMMM d, yyyy,  h:mm:ss tt");
            ReadOnlyCheck.IsChecked = di.Attributes.HasFlag(FileAttributes.ReadOnly);
            HiddenCheck.IsChecked   = di.Attributes.HasFlag(FileAttributes.Hidden);
            _ = CalculateFolderSizeAsync();
        }
        else
        {
            var fi = new FileInfo(_path);
            IconGlyph.Text    = "";
            NameText.Text     = fi.Name;
            TypeText.Text     = fi.Extension.Length > 1
                ? fi.Extension.TrimStart('.').ToUpperInvariant() + " File"
                : "File";
            LocationText.Text = fi.DirectoryName ?? "";
            SizeText.Text     = $"{FormatSize(fi.Length)}  ({fi.Length:N0} bytes)";
            CreatedText.Text  = fi.CreationTime.ToString("dddd, MMMM d, yyyy,  h:mm:ss tt");
            ModifiedText.Text = fi.LastWriteTime.ToString("dddd, MMMM d, yyyy,  h:mm:ss tt");
            AccessedText.Text = fi.LastAccessTime.ToString("dddd, MMMM d, yyyy,  h:mm:ss tt");
            ReadOnlyCheck.IsChecked = fi.Attributes.HasFlag(FileAttributes.ReadOnly);
            HiddenCheck.IsChecked   = fi.Attributes.HasFlag(FileAttributes.Hidden);
        }
    }

    private async Task CalculateFolderSizeAsync()
    {
        var (size, files, folders) = await Task.Run(() =>
        {
            long sz = 0, fc = 0, dc = 0;
            try
            {
                var opts = new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible    = true,
                    AttributesToSkip      = FileAttributes.ReparsePoint
                };
                foreach (var entry in Directory.EnumerateFileSystemEntries(_path, "*", opts))
                {
                    try
                    {
                        var attr = File.GetAttributes(entry);
                        if (attr.HasFlag(FileAttributes.Directory)) dc++;
                        else { fc++; sz += new FileInfo(entry).Length; }
                    }
                    catch { }
                }
            }
            catch { }
            return (sz, fc, dc);
        });

        await Dispatcher.InvokeAsync(() =>
        {
            SizeText.Text     = $"{FormatSize(size)}  ({size:N0} bytes)";
            ContainsText.Text = $"{files:N0} Files, {folders:N0} Folders";
        });
    }

    // ── Details tab ───────────────────────────────────────────────────────────

    private void LoadDetails()
    {
        var rows = new List<PropRow>();
        bool isDir = Directory.Exists(_path);

        rows.Add(new PropRow("FILE SYSTEM", null, IsSection: true));

        if (isDir)
        {
            var di = new DirectoryInfo(_path);
            rows.Add(new PropRow("Name",        di.Name));
            rows.Add(new PropRow("Type",        "File folder"));
            rows.Add(new PropRow("Folder path", di.Parent?.FullName ?? _path));
            rows.Add(new PropRow("Created",     di.CreationTime.ToString("g")));
            rows.Add(new PropRow("Modified",    di.LastWriteTime.ToString("g")));
            rows.Add(new PropRow("Accessed",    di.LastAccessTime.ToString("g")));
            rows.Add(new PropRow("Attributes",  FmtAttr(di.Attributes)));
        }
        else
        {
            var fi = new FileInfo(_path);
            rows.Add(new PropRow("Name",        fi.Name));
            rows.Add(new PropRow("Extension",   fi.Extension));
            rows.Add(new PropRow("Type",        fi.Extension.Length > 1
                ? fi.Extension.TrimStart('.').ToUpperInvariant() + " File" : "File"));
            rows.Add(new PropRow("Folder path", fi.DirectoryName ?? ""));
            rows.Add(new PropRow("Size",        $"{FormatSize(fi.Length)} ({fi.Length:N0} bytes)"));
            rows.Add(new PropRow("Created",     fi.CreationTime.ToString("g")));
            rows.Add(new PropRow("Modified",    fi.LastWriteTime.ToString("g")));
            rows.Add(new PropRow("Accessed",    fi.LastAccessTime.ToString("g")));
            rows.Add(new PropRow("Attributes",  FmtAttr(fi.Attributes)));

            var imageExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tiff", ".tif", ".webp", ".ico", ".heic", ".heif" };
            if (imageExts.Contains(fi.Extension))
                rows.AddRange(GetImageRows(fi.FullName));
        }

        DetailsItems.ItemsSource = rows;
    }

    private static IEnumerable<PropRow> GetImageRows(string path)
    {
        var rows = new List<PropRow>();
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var decoder = BitmapDecoder.Create(stream,
                BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.Default);
            var frame = decoder.Frames[0];

            rows.Add(new PropRow("IMAGE", null, IsSection: true));
            rows.Add(new PropRow("Dimensions",           $"{frame.PixelWidth} × {frame.PixelHeight} pixels"));
            rows.Add(new PropRow("Width",                $"{frame.PixelWidth} pixels"));
            rows.Add(new PropRow("Height",               $"{frame.PixelHeight} pixels"));
            if (frame.DpiX > 0)
                rows.Add(new PropRow("Horizontal resolution", $"{frame.DpiX:F0} dpi"));
            if (frame.DpiY > 0)
                rows.Add(new PropRow("Vertical resolution",   $"{frame.DpiY:F0} dpi"));
            rows.Add(new PropRow("Bit depth",            $"{frame.Format.BitsPerPixel}"));

            if (frame.Metadata is BitmapMetadata m)
            {
                bool hasMetaHeader = false;
                void AddMeta(string label, string? val)
                {
                    if (string.IsNullOrWhiteSpace(val)) return;
                    if (!hasMetaHeader) { rows.Add(new PropRow("METADATA", null, IsSection: true)); hasMetaHeader = true; }
                    rows.Add(new PropRow(label, val));
                }

                AddMeta("Title",          m.Title);
                AddMeta("Subject",        m.Subject);
                AddMeta("Authors",        m.Author?.Count > 0 ? string.Join(", ", m.Author) : null);
                AddMeta("Comment",        m.Comment);
                AddMeta("Copyright",      m.Copyright);
                AddMeta("Camera make",    m.CameraManufacturer);
                AddMeta("Camera model",   m.CameraModel);
                AddMeta("Date taken",     ParseExifDate(m.DateTaken));
                AddMeta("Software",       m.ApplicationName);
                AddMeta("Tags",           m.Keywords?.Count > 0 ? string.Join(", ", m.Keywords) : null);
                if (m.Rating > 0) AddMeta("Rating", $"{m.Rating}");

                // EXIF camera settings
                AddMeta("Exposure time",  TryExif(m, "/app1/ifd/exif:{uint=33434}", FmtRational, "sec"));
                AddMeta("F-stop",         TryExif(m, "/app1/ifd/exif:{uint=33437}", v => "f/" + FmtRational(v)));
                AddMeta("ISO",            TryExif(m, "/app1/ifd/exif:{uint=34855}"));
                AddMeta("Focal length",   TryExif(m, "/app1/ifd/exif:{uint=37386}", FmtRational, "mm"));
                AddMeta("Flash",          TryExif(m, "/app1/ifd/exif:{uint=37385}", v =>
                    v is ushort f ? ((f & 1) == 1 ? "Flash fired" : "No flash") : v.ToString() ?? ""));
                AddMeta("White balance",  TryExif(m, "/app1/ifd/exif:{uint=41987}", v =>
                    v is ushort wb ? (wb == 0 ? "Auto" : "Manual") : v.ToString() ?? ""));
                AddMeta("Exposure mode",  TryExif(m, "/app1/ifd/exif:{uint=41986}", v =>
                    v is ushort em ? em switch { 0 => "Auto", 1 => "Manual", 2 => "Auto bracket", _ => em.ToString() } : v.ToString() ?? ""));
                AddMeta("Metering mode",  TryExif(m, "/app1/ifd/exif:{uint=37383}", v =>
                    v is ushort mm2 ? mm2 switch { 1 => "Average", 2 => "Center-weighted", 3 => "Spot", 4 => "Multi-spot", 5 => "Multi-segment", _ => mm2.ToString() } : v.ToString() ?? ""));
            }
        }
        catch { }
        return rows;
    }

    private static string? TryExif(BitmapMetadata m, string query,
        Func<object, string>? fmt = null, string? suffix = null)
    {
        try
        {
            var v = m.GetQuery(query);
            if (v == null) return null;
            var text = fmt != null ? fmt(v) : v.ToString() ?? "";
            return string.IsNullOrEmpty(text) ? null
                 : suffix != null ? text + " " + suffix : text;
        }
        catch { return null; }
    }

    private static string FmtRational(object v)
    {
        // WPF stores EXIF rationals as ulong: high 32 = numerator, low 32 = denominator
        ulong ul = v is ulong u ? u : v is long l ? (ulong)l : 0;
        if (ul == 0) return v.ToString() ?? "";
        uint num = (uint)(ul >> 32);
        uint den = (uint)(ul & 0xFFFFFFFF);
        if (den == 0) return num.ToString();
        if (num % den == 0) return (num / den).ToString();
        // simplify
        uint g = Gcd(num, den);
        return g > 1 ? $"{num/g}/{den/g}" : $"{num}/{den}";
    }

    private static uint Gcd(uint a, uint b) { while (b != 0) { (a, b) = (b, a % b); } return a; }

    private static string? ParseExifDate(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        if (raw.Length >= 10 && raw[4] == ':' && raw[7] == ':')
        {
            var normalized = raw[..4] + "-" + raw[5..7] + "-" + raw[8..10] + (raw.Length > 10 ? raw[10..] : "");
            if (DateTime.TryParse(normalized, out var dt))
                return dt.ToString("g");
        }
        return raw;
    }

    // ── Security tab ─────────────────────────────────────────────────────────

    private void LoadSecurity()
    {
        SecurityPathText.Text = _path;
        var rows = new List<PermRow>();

        try
        {
            FileSystemSecurity sec = Directory.Exists(_path)
                ? new DirectoryInfo(_path).GetAccessControl()
                : (FileSystemSecurity)new FileInfo(_path).GetAccessControl();

            OwnerText.Text = sec.GetOwner(typeof(NTAccount))?.Value ?? "Unknown";

            foreach (FileSystemAccessRule rule in sec.GetAccessRules(true, true, typeof(NTAccount)))
            {
                rows.Add(new PermRow(
                    rule.IdentityReference.Value,
                    rule.AccessControlType == AccessControlType.Allow ? "Allow" : "Deny",
                    FmtRights(rule.FileSystemRights)));
            }
        }
        catch (UnauthorizedAccessException) { OwnerText.Text = "(access denied)"; }
        catch (Exception ex)               { OwnerText.Text = $"Error: {ex.Message}"; }

        PermissionsList.ItemsSource = rows;
    }

    private static string FmtRights(FileSystemRights r)
    {
        if ((r & FileSystemRights.FullControl)   == FileSystemRights.FullControl)   return "Full control";
        if ((r & FileSystemRights.Modify)        == FileSystemRights.Modify)        return "Modify";
        if ((r & FileSystemRights.ReadAndExecute)== FileSystemRights.ReadAndExecute) return "Read & execute";

        var parts = new List<string>();
        if ((r & FileSystemRights.Read)          != 0) parts.Add("Read");
        if ((r & FileSystemRights.Write)         != 0) parts.Add("Write");
        if ((r & FileSystemRights.Delete)        != 0) parts.Add("Delete");
        if ((r & FileSystemRights.ExecuteFile)   != 0) parts.Add("Execute");
        return parts.Count > 0 ? string.Join(", ", parts) : r.ToString();
    }

    // ── Buttons ───────────────────────────────────────────────────────────────

    private void OK_Click(object sender, RoutedEventArgs e)     { ApplyAttributes(); ApplyCustomize(); Close(); }
    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
    private void Apply_Click(object sender, RoutedEventArgs e)  { ApplyAttributes(); ApplyCustomize(); }

    private void ApplyAttributes()
    {
        try
        {
            var attr = File.GetAttributes(_path);
            attr = ReadOnlyCheck.IsChecked == true ? attr | FileAttributes.ReadOnly  : attr & ~FileAttributes.ReadOnly;
            attr = HiddenCheck.IsChecked   == true ? attr | FileAttributes.Hidden    : attr & ~FileAttributes.Hidden;
            File.SetAttributes(_path, attr);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ── Customize tab ────────────────────────────────────────────────────────

    private void LoadCustomize()
    {
        bool isDir = Directory.Exists(_path);
        if (!isDir)
        {
            NotFolderNotice.Visibility  = Visibility.Visible;
            CustomizeContent.Visibility = Visibility.Collapsed;
            return;
        }

        var ini = ReadDesktopIni(_path);

        // Folder type
        if (ini.TryGetValue("FolderType", out var ft) && !string.IsNullOrEmpty(ft))
        {
            foreach (System.Windows.Controls.ComboBoxItem ci in FolderTypeCombo.Items)
                if ((string?)ci.Tag == ft) { ci.IsSelected = true; break; }
        }

        // Current icon
        if (ini.TryGetValue("IconResource", out var iconRes) && !string.IsNullOrEmpty(iconRes))
        {
            var comma    = iconRes.LastIndexOf(',');
            var iconPath = comma >= 0 ? iconRes[..comma] : iconRes;
            TryLoadIconPreview(iconPath);
        }

        // Current folder picture
        if (ini.TryGetValue("IconFile", out var picPath) && !string.IsNullOrEmpty(picPath))
            TryLoadFolderPicture(picPath);
    }

    private void TryLoadIconPreview(string iconPath)
    {
        try
        {
            if (!File.Exists(iconPath)) return;

            if (iconPath.EndsWith(".ico", StringComparison.OrdinalIgnoreCase))
            {
                using var fs = new FileStream(iconPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.StreamSource = fs;
                bmp.CacheOption  = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                CustomIconImage.Source     = bmp;
                CustomIconGlyph.Visibility = Visibility.Collapsed;
                CustomIconImage.Visibility = Visibility.Visible;
            }
            else
            {
                // .exe / .dll — extract via shell
                var large = new IntPtr[1];
                var small = new IntPtr[1];
                if (ExtractIconEx(iconPath, 0, large, small, 1) > 0 && large[0] != IntPtr.Zero)
                {
                    var bs = Imaging.CreateBitmapSourceFromHIcon(
                        large[0], Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    CustomIconImage.Source     = bs;
                    CustomIconGlyph.Visibility = Visibility.Collapsed;
                    CustomIconImage.Visibility = Visibility.Visible;
                    DestroyIcon(large[0]);
                }
                if (small[0] != IntPtr.Zero) DestroyIcon(small[0]);
            }
        }
        catch { }
    }

    private void TryLoadFolderPicture(string picPath)
    {
        try
        {
            if (!File.Exists(picPath)) return;
            using var fs = new FileStream(picPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.StreamSource = fs;
            bmp.CacheOption  = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            FolderPictureImage.Source         = bmp;
            FolderPicturePreview.Visibility   = Visibility.Visible;
        }
        catch { }
    }

    private void ChangeIcon_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title       = "Change Icon",
            Filter      = "Icon files (*.ico)|*.ico|Executables & libraries (*.exe;*.dll)|*.exe;*.dll|All files (*.*)|*.*",
            FilterIndex = 1
        };
        if (dlg.ShowDialog(this) != true) return;

        _pendingIconResource = dlg.FileName + ",0";
        TryLoadIconPreview(dlg.FileName);
    }

    private void RestoreIcon_Click(object sender, RoutedEventArgs e)
    {
        _pendingIconResource       = "";
        CustomIconImage.Source     = null;
        CustomIconGlyph.Visibility = Visibility.Visible;
        CustomIconImage.Visibility = Visibility.Collapsed;
    }

    private void ChooseFolderPicture_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title       = "Choose Folder Picture",
            Filter      = "Image files (*.jpg;*.jpeg;*.png;*.bmp;*.gif)|*.jpg;*.jpeg;*.png;*.bmp;*.gif|All files (*.*)|*.*",
            FilterIndex = 1
        };
        if (dlg.ShowDialog(this) != true) return;

        _pendingFolderPicture = dlg.FileName;
        TryLoadFolderPicture(dlg.FileName);
    }

    private void RestoreFolderPicture_Click(object sender, RoutedEventArgs e)
    {
        _pendingFolderPicture             = "";
        FolderPictureImage.Source         = null;
        FolderPicturePreview.Visibility   = Visibility.Collapsed;
    }

    private void ApplyCustomize()
    {
        if (!Directory.Exists(_path)) return;
        try
        {
            // Folder type
            if (FolderTypeCombo.SelectedItem is System.Windows.Controls.ComboBoxItem { Tag: string ft })
            {
                if (ft == "Generic")
                    RemoveDesktopIniKey(_path, "FolderType");
                else
                    WriteDesktopIni(_path, "FolderType", ft);

                if (ApplyToSubfoldersCheck.IsChecked == true)
                    ApplyFolderTypeToSubfolders(_path, ft);
            }

            // Icon resource
            if (_pendingIconResource != null)
            {
                if (_pendingIconResource == "")
                {
                    RemoveDesktopIniKey(_path, "IconResource");
                    TryCleanupSystemAttribute();
                }
                else
                {
                    WriteDesktopIni(_path, "IconResource", _pendingIconResource);
                }
            }

            // Folder picture
            if (_pendingFolderPicture != null)
            {
                if (_pendingFolderPicture == "")
                    RemoveDesktopIniKey(_path, "IconFile");
                else
                    WriteDesktopIni(_path, "IconFile", _pendingFolderPicture);
            }

            // Tell the shell to refresh so the icon/thumbnail updates immediately
            SHChangeNotify(0x08000000, 0x0000, IntPtr.Zero, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Customize Error", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void TryCleanupSystemAttribute()
    {
        var iniPath = Path.Combine(_path, "desktop.ini");
        if (!File.Exists(iniPath)) return;
        var ini = ReadDesktopIni(_path);
        if (ini.Count == 0)
        {
            try { File.Delete(iniPath); } catch { }
            var attr = File.GetAttributes(_path) & ~FileAttributes.System;
            File.SetAttributes(_path, attr);
        }
    }

    // ── desktop.ini helpers ───────────────────────────────────────────────────

    private static Dictionary<string, string> ReadDesktopIni(string folderPath)
    {
        var result  = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var iniPath = Path.Combine(folderPath, "desktop.ini");
        if (!File.Exists(iniPath)) return result;
        try
        {
            bool inSection = false;
            foreach (var raw in File.ReadAllLines(iniPath))
            {
                var line = raw.Trim();
                if (line == "[.ShellClassInfo]") { inSection = true; continue; }
                if (line.StartsWith('['))         { inSection = false; continue; }
                if (!inSection || !line.Contains('=')) continue;
                var eq = line.IndexOf('=');
                result[line[..eq].Trim()] = line[(eq + 1)..].Trim();
            }
        }
        catch { }
        return result;
    }

    private static void WriteDesktopIni(string folderPath, string key, string value)
    {
        var iniPath = Path.Combine(folderPath, "desktop.ini");
        var lines   = new List<string>();

        if (File.Exists(iniPath))
        {
            File.SetAttributes(iniPath, FileAttributes.Normal);
            lines.AddRange(File.ReadAllLines(iniPath));
        }

        bool sectionFound = false, keyWritten = false, inSection = false;

        for (int i = 0; i < lines.Count; i++)
        {
            var trim = lines[i].Trim();
            if (trim == "[.ShellClassInfo]") { sectionFound = true; inSection = true; continue; }
            if (trim.StartsWith('['))        inSection = false;
            if (inSection && trim.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase))
            {
                lines[i]   = $"{key}={value}";
                keyWritten = true;
            }
        }

        if (!sectionFound)
        {
            lines.InsertRange(0, new[] { "[.ShellClassInfo]", $"{key}={value}" });
        }
        else if (!keyWritten)
        {
            // Insert at end of the section
            inSection = false;
            int insertAt = lines.Count;
            for (int i = 0; i < lines.Count; i++)
            {
                var trim = lines[i].Trim();
                if (trim == "[.ShellClassInfo]") { inSection = true; continue; }
                if (trim.StartsWith('[') && inSection) { insertAt = i; break; }
            }
            lines.Insert(insertAt, $"{key}={value}");
        }

        File.WriteAllLines(iniPath, lines);
        File.SetAttributes(iniPath, FileAttributes.Hidden | FileAttributes.System);

        // Folder must have System attribute for Windows to read desktop.ini
        var folderAttr = File.GetAttributes(folderPath) | FileAttributes.System;
        File.SetAttributes(folderPath, folderAttr);
    }

    private static void RemoveDesktopIniKey(string folderPath, string key)
    {
        var iniPath = Path.Combine(folderPath, "desktop.ini");
        if (!File.Exists(iniPath)) return;

        File.SetAttributes(iniPath, FileAttributes.Normal);
        var lines = File.ReadAllLines(iniPath).ToList();
        bool inSection = false;

        for (int i = lines.Count - 1; i >= 0; i--)
        {
            var trim = lines[i].Trim();
            if (trim == "[.ShellClassInfo]")                                              inSection = true;
            else if (trim.StartsWith('['))                                                inSection = false;
            if (inSection && trim.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase))
                lines.RemoveAt(i);
        }

        if (lines.All(l => string.IsNullOrWhiteSpace(l) || l.TrimStart().StartsWith('[')))
        {
            try { File.Delete(iniPath); } catch { }
        }
        else
        {
            File.WriteAllLines(iniPath, lines);
            File.SetAttributes(iniPath, FileAttributes.Hidden | FileAttributes.System);
        }
    }

    private static void ApplyFolderTypeToSubfolders(string folderPath, string ft)
    {
        try
        {
            var opts = new EnumerationOptions { IgnoreInaccessible = true };
            foreach (var dir in Directory.EnumerateDirectories(folderPath, "*", opts))
            {
                if (ft == "Generic") RemoveDesktopIniKey(dir, "FolderType");
                else                 WriteDesktopIni(dir, "FolderType", ft);
            }
        }
        catch { }
    }

    // Shell P/Invokes for icon extraction and change notification

    [DllImport("shell32.dll", EntryPoint = "ExtractIconExW", CharSet = CharSet.Unicode)]
    private static extern int ExtractIconEx(string file, int index,
        IntPtr[] large, IntPtr[] small, int count);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(uint wEventId, uint uFlags,
        IntPtr dwItem1, IntPtr dwItem2);

    // ── Shared helpers ────────────────────────────────────────────────────────

    private static string FormatSize(long b) => b switch
    {
        < 1024L               => $"{b} bytes",
        < 1048576L            => $"{b / 1024.0:F1} KB",
        < 1073741824L         => $"{b / 1048576.0:F2} MB",
        _                     => $"{b / 1073741824.0:F2} GB"
    };

    private static string FmtAttr(FileAttributes a)
    {
        var p = new List<string>();
        if (a.HasFlag(FileAttributes.ReadOnly))  p.Add("Read-only");
        if (a.HasFlag(FileAttributes.Hidden))    p.Add("Hidden");
        if (a.HasFlag(FileAttributes.System))    p.Add("System");
        if (a.HasFlag(FileAttributes.Archive))   p.Add("Archive");
        return p.Count > 0 ? string.Join(", ", p) : "Normal";
    }

    // ── Dark title bar ────────────────────────────────────────────────────────

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private void ApplyDarkTitleBar()
    {
        var hwnd  = new WindowInteropHelper(this).Handle;
        int value = 1;
        DwmSetWindowAttribute(hwnd, 20, ref value, sizeof(int));
    }
}

// Data models used for Details and Security tab item sources
public record PropRow(string Name, string? Value, bool IsSection = false);
public record PermRow(string Principal, string Type, string Rights);
