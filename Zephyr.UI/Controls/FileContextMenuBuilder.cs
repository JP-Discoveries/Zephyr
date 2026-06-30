using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Zephyr.Core.Archives;
using Zephyr.Core.Models;
using Zephyr.Core.Security;
using Zephyr.UI.Dialogs;
using Zephyr.UI.Services;
using Zephyr.UI.ViewModels;

namespace Zephyr.UI.Controls;

// Builds and shows the file/folder right-click menu: a searchable, flat (no nested
// submenus) command list plus a colour-label swatch row. Extracted from FilePane so the
// pane code-behind stays focused on layout/interaction wiring.
//
// Constructed with the owning FrameworkElement (the FilePane) purely so resource lookups
// (FindResource), the owner Window for dialogs, and the dispatcher all resolve exactly as
// they did when this lived in the code-behind.
internal sealed class FileContextMenuBuilder(FrameworkElement owner)
{
    private readonly FrameworkElement _owner = owner;

    private static readonly HashSet<string> _elevatableExts = new(StringComparer.OrdinalIgnoreCase)
        { ".exe", ".msi", ".bat", ".cmd", ".com", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".ps1" };

    public void Show(FileItem item, TabViewModel tab, UIElement anchor)
    {
        var vm       = Window.GetWindow(_owner)?.DataContext as MainViewModel;
        var sepStyle = (Style)_owner.FindResource("MenuSep");
        var menu     = new ContextMenu { HorizontalContentAlignment = HorizontalAlignment.Stretch };

        var filterItems = new List<(MenuItem mi, string label)>();
        var sepList     = new List<Separator>();
        MenuItem? labelRow = null;

        // ── Search bar (icon | input | clear ×) ──────────────────────────────
        var searchBar = new Border
        {
            Margin              = new Thickness(0),
            Padding             = new Thickness(0),
            BorderThickness     = new Thickness(0, 0, 0, 1),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        searchBar.SetResourceReference(Border.BackgroundProperty,  "ZephyrSurface");
        searchBar.SetResourceReference(Border.BorderBrushProperty, "ZephyrBorder");

        var searchGrid = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch };
        searchGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        searchGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        searchGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Magnifier icon
        var magIcon = new TextBlock
        {
            FontFamily        = new FontFamily("Segoe Fluent Icons"),
            Text              = "",
            FontSize          = 13,
            Margin            = new Thickness(12, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible  = false,
        };
        magIcon.SetResourceReference(TextBlock.ForegroundProperty, "ZephyrTextSecondary");
        Grid.SetColumn(magIcon, 0);
        searchGrid.Children.Add(magIcon);

        // Placeholder (overlaid on input column, behind TextBox)
        var placeholder = new TextBlock
        {
            Text              = "Search actions…",
            FontSize          = 13,
            Margin            = new Thickness(3, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible  = false,
        };
        placeholder.SetResourceReference(TextBlock.ForegroundProperty, "ZephyrTextSecondary");
        Grid.SetColumn(placeholder, 1);
        searchGrid.Children.Add(placeholder);

        // Actual TextBox — transparent, borderless, sits on top of placeholder
        var searchBox = new TextBox
        {
            FontSize          = 13,
            Padding           = new Thickness(2, 8, 2, 8),
            BorderThickness   = new Thickness(0),
            Background        = Brushes.Transparent,
            VerticalAlignment = VerticalAlignment.Center,
        };
        searchBox.SetResourceReference(TextBox.ForegroundProperty, "ZephyrTextPrimary");
        Grid.SetColumn(searchBox, 1);
        searchGrid.Children.Add(searchBox);

        // Clear × button (hidden until text is entered)
        var clearIcon = new TextBlock
        {
            FontFamily        = new FontFamily("Segoe Fluent Icons"),
            Text              = "",
            FontSize          = 10,
            Margin            = new Thickness(4, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor            = Cursors.Arrow,
            Visibility        = Visibility.Collapsed,
        };
        clearIcon.SetResourceReference(TextBlock.ForegroundProperty, "ZephyrTextSecondary");
        clearIcon.MouseLeftButtonUp += (_, _) =>
        {
            searchBox.Clear();
            searchBox.Focus();
            Keyboard.Focus(searchBox);
        };
        Grid.SetColumn(clearIcon, 2);
        searchGrid.Children.Add(clearIcon);

        searchBar.Child = searchGrid;

        // Wrap in a MenuItem with a bare template so WPF doesn't apply hover
        // highlight or extra padding. The ContentPresenter is stretched to fill
        // the full menu width so the search bar goes edge-to-edge.
        var cpFactory = new FrameworkElementFactory(typeof(ContentPresenter));
        cpFactory.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
        cpFactory.SetBinding(ContentPresenter.ContentProperty, new System.Windows.Data.Binding
        {
            RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent),
            Path           = new PropertyPath(MenuItem.HeaderProperty),
        });
        var bareTemplate = new ControlTemplate(typeof(MenuItem)) { VisualTree = cpFactory };

        menu.Items.Add(new MenuItem
        {
            Header                     = searchBar,
            Template                   = bareTemplate,
            Padding                    = new Thickness(0),
            Margin                     = new Thickness(0),
            Focusable                  = false,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        });

        Separator AddSep()
        {
            var s = new Separator { Style = sepStyle };
            sepList.Add(s);
            menu.Items.Add(s);
            return s;
        }

        void Add(MenuItem mi, string label)
        {
            filterItems.Add((mi, label.ToLowerInvariant()));
            menu.Items.Add(mi);
        }

        // ── Primary actions ───────────────────────────────────────────────────
        bool inArchive = tab.IsArchiveView; // browsing inside an archive → read-only menu

        Add(MakeMenuItem("Open", () =>
        {
            if (item.IsDirectory) tab.Navigate(item.FullPath);
            else                  tab.OpenFile(item.FullPath);
        }), "open");

        if (!inArchive)
        {
        if (item.IsDirectory)
            Add(MakeMenuItem("Add to Bookmarks", () =>
            {
                if (Window.GetWindow(_owner)?.DataContext is MainViewModel mvm)
                    mvm.AddBookmark(item.Name, item.FullPath);
            }), "add bookmarks favorite");

        if (!item.IsDirectory)
        {
            var openWithMi = new MenuItem { Header = "Open With…" };
            openWithMi.Click += (_, _) =>
            {
                menu.IsOpen = false;
                var path = item.FullPath;
                var t = new System.Threading.Thread(() => ShellIntegrationService.ShowOpenWith(path));
                t.SetApartmentState(System.Threading.ApartmentState.STA);
                t.IsBackground = true;
                t.Start();
            };
            Add(openWithMi, "open with");
            if (_elevatableExts.Contains(item.Extension))
            {
                var runAsAdminMi = new MenuItem { Header = "Run as Administrator" };
                runAsAdminMi.Click += (_, _) =>
                {
                    menu.IsOpen = false;
                    var path = item.FullPath;
                    var t = new System.Threading.Thread(() =>
                    {
                        try { ShellIntegrationService.RunAsAdmin(path); }
                        catch (Exception ex) { _owner.Dispatcher.Invoke(() => ShowError(ex.Message)); }
                    });
                    t.SetApartmentState(System.Threading.ApartmentState.STA);
                    t.IsBackground = true;
                    t.Start();
                };
                Add(runAsAdminMi, "run administrator admin elevated");
            }
        }

        Add(MakeMenuItem("Open in Terminal", () =>
        {
            var dir = item.IsDirectory ? item.FullPath : Path.GetDirectoryName(item.FullPath)!;
            TerminalService.OpenAt(dir);
        }), "open terminal console cmd powershell");
        }

        var owner   = Window.GetWindow(_owner);
        var itemDir = Path.GetDirectoryName(item.FullPath)!;

        AddSep();

        // ── Archive actions ───────────────────────────────────────────────────
        // Flat top-level items: the themed MenuItem template has no submenu popup.
        if (inArchive)
        {
            // Inside an archive (read-only): extract the selected entries to disk.
            var (archiveFile, baseInner) = ArchivePath.Parse(tab.CurrentPath);
            var selItems = tab.SelectedItems.Count > 0 ? tab.SelectedItems.ToList() : [item];
            var selLabel = selItems.Count == 1 ? "Extract selected…" : $"Extract {selItems.Count} items…";
            Add(MakeMenuItem(selLabel, () =>
            {
                var defaultDest = Path.GetDirectoryName(archiveFile) ?? tab.CurrentPath;
                var dlg = new ExtractDialog([Path.GetFileName(archiveFile)], defaultDest) { Owner = owner };
                if (dlg.ShowDialog() != true) return;
                var opts   = new ZephyrArchiveService.ExtractOptions(Password: dlg.Password ?? tab.CurrentArchivePassword);
                var inners = selItems.Select(i => ArchivePath.Parse(i.FullPath).Inner).ToList();
                ArchiveProgressDialog.Run(owner,$"Extracting from {Path.GetFileName(archiveFile)}…",
                    (prog, ct) => ZephyrArchiveService.ExtractEntriesAsync(archiveFile, inners, baseInner, dlg.Destination, opts, prog, ct));
            }), "extract selected archive entry");
        }
        else
        {
        // Compress: all selected items, falling back to the right-clicked item.
        var compressSources = tab.SelectedItems.Count > 0 ? tab.SelectedItems.ToList() : [item];
        var compressName    = compressSources.Count == 1
                                ? Path.GetFileNameWithoutExtension(compressSources[0].Name)
                                : "Archive";

        Add(MakeMenuItem("Compress…", () =>
        {
            var dlg = new CompressDialog(compressName, tab.CurrentPath, compressSources.Count) { Owner = owner };
            if (dlg.ShowDialog() != true) return;
            var sources = compressSources.Select(i => i.FullPath).ToList();
            var name    = Path.GetFileName(dlg.ResultPath);
            if (dlg.AddToExisting)
                ArchiveProgressDialog.Run(owner,$"Adding to {name}…",
                    (prog, ct) => ZephyrArchiveService.AppendToZipAsync(dlg.ResultPath, sources, dlg.Options.Level, prog, ct));
            else
                ArchiveProgressDialog.Run(owner,$"Compressing {name}…",
                    (prog, ct) => ZephyrArchiveService.CreateAsync(dlg.ResultPath, sources, dlg.Options, prog, ct));
            tab.Reload();
        }), "compress archive zip tar gz create");

        // Extract: selected archives, falling back to the right-clicked item if it's one.
        var extractSources = (tab.SelectedItems.Count > 0 ? tab.SelectedItems.ToList() : [item])
            .Where(i => !i.IsDirectory && ZephyrArchiveService.CanExtract(i.FullPath))
            .ToList();

        if (extractSources.Count > 0)
        {
            var extractLabel = extractSources.Count == 1 ? "Extract…" : $"Extract {extractSources.Count} archives…";
            Add(MakeMenuItem(extractLabel, () =>
            {
                var defaultDest = extractSources.Count == 1
                    ? Path.Combine(tab.CurrentPath, ZephyrArchiveService.StripArchiveExtension(extractSources[0].Name))
                    : tab.CurrentPath;
                var dlg = new ExtractDialog(extractSources.Select(a => a.Name).ToList(), defaultDest) { Owner = owner };
                if (dlg.ShowDialog() != true) return;
                var opts  = new ZephyrArchiveService.ExtractOptions(Password: dlg.Password);
                var title = extractSources.Count == 1 ? $"Extracting {extractSources[0].Name}…" : $"Extracting {extractSources.Count} archives…";
                ArchiveProgressDialog.Run(owner,title, async (prog, ct) =>
                {
                    for (int i = 0; i < extractSources.Count; i++)
                    {
                        var a = extractSources[i];
                        var dest = extractSources.Count == 1 ? dlg.Destination
                                 : dlg.EachToOwnSubfolder ? Path.Combine(dlg.Destination, ZephyrArchiveService.StripArchiveExtension(a.Name))
                                 : dlg.Destination;
                        int idx = i + 1;
                        IProgress<ZephyrArchiveService.ArchiveProgress> sub = extractSources.Count == 1
                            ? prog
                            : new Progress<ZephyrArchiveService.ArchiveProgress>(p =>
                                prog.Report(p with { CurrentEntry = $"({idx}/{extractSources.Count}) {a.Name} — {p.CurrentEntry}" }));
                        await ZephyrArchiveService.ExtractAsync(a.FullPath, dest, opts, sub, ct);
                    }
                });
                tab.Reload();
            }), "extract archive unzip zip 7z rar tar gz bz2 xz");

            Add(MakeMenuItem("Test Archive", () =>
            {
                _ = RunArchiveAsync(async () =>
                {
                    var sb     = new StringBuilder();
                    bool allOk = true;
                    foreach (var a in extractSources)
                    {
                        var r = await ZephyrArchiveService.TestAsync(a.FullPath);
                        if (r.AllOk)
                            sb.AppendLine($"✔ {a.Name} — all {r.Total} entries OK");
                        else
                        {
                            allOk = false;
                            sb.AppendLine($"✘ {a.Name} — {r.Failed} of {r.Total} failed:");
                            foreach (var f in r.FailedEntries.Take(10)) sb.AppendLine($"      • {f}");
                            if (r.Failed > 10) sb.AppendLine($"      …and {r.Failed - 10} more");
                        }
                    }
                    ZephyrMessageBox.Show(sb.ToString().TrimEnd(), allOk ? "Test Archive — OK" : "Test Archive — Problems Found");
                });
            }), "test archive verify integrity");
        }
        }

        if (!inArchive)
        {
        AddSep();

        // ── Clipboard operations ──────────────────────────────────────────────
        if (vm != null)
        {
            Add(MakeMenuItem("Cut",    () => vm.CutCommand.Execute(null),   "Ctrl+X"), "cut move");
            Add(MakeMenuItem("Copy",   () => vm.CopyCommand.Execute(null),  "Ctrl+C"), "copy");
            Add(MakeMenuItem("Paste",  () => vm.PasteCommand.Execute(null), "Ctrl+V",
                enabled: ClipboardService.HasFiles()), "paste");
            AddSep();
            Add(MakeMenuItem("Rename", () => vm.RenameCommand.Execute(null), "F2"),    "rename");
            Add(MakeMenuItem("Delete", () => vm.DeleteCommand.Execute(null), "Del"),   "delete remove trash");

            var duplicateSources = tab.SelectedItems.Count > 0
                ? tab.SelectedItems.Select(i => i.FullPath)
                : [item.FullPath];
            Add(MakeMenuItem("Create Copy", () => _ = tab.DuplicateAsync(duplicateSources)), "create copy duplicate");
        }

        AddSep();

        // ── Shell utilities ───────────────────────────────────────────────────
        Add(MakeMenuItem("Copy Path", () => Clipboard.SetText(item.FullPath)), "copy path location");

        Add(MakeMenuItem("Create Shortcut", () =>
        {
            try   { ShellIntegrationService.CreateShortcut(item.FullPath, itemDir); tab.Reload(); }
            catch (Exception ex) { ShowError(ex.Message); }
        }), "create shortcut link lnk");

        Add(MakeMenuItem("Create Link…", () =>
        {
            var dlg = new Zephyr.UI.Dialogs.CreateLinkDialog(item.FullPath)
                { Owner = Window.GetWindow(_owner) };
            if (dlg.ShowDialog() != true) return;
            try
            {
                Zephyr.Core.FileSystem.LinkService.Create(dlg.SelectedKind, dlg.LinkPath, dlg.TargetPath);
                tab.Reload();
            }
            catch (Exception ex)
            {
                ShowError(dlg.SelectedKind == Zephyr.Core.FileSystem.LinkKind.Symbolic
                    ? $"{ex.Message}\n\nSymbolic links need administrator rights or Windows Developer Mode. " +
                      "For folders try a Junction; for files try a Hard link instead."
                    : ex.Message);
            }
        }), "create link symbolic junction hardlink hard symlink");

        if (!item.IsDirectory)
            Add(MakeMenuItem("Pin to Start",
                () => ShellIntegrationService.PinToStart(item.FullPath)), "pin start menu");

        if (!item.IsDirectory)
            Add(MakeMenuItem("Checksum…", () =>
            {
                var win = new Zephyr.UI.Dialogs.ChecksumWindow(item.FullPath)
                    { Owner = Window.GetWindow(_owner) };
                win.ShowDialog();
            }), "checksum hash md5 sha verify compare integrity");

        if (item.IsDirectory)
            Add(MakeMenuItem("Disk usage…", () =>
            {
                var win = new Zephyr.UI.Dialogs.DiskUsageWindow(item.FullPath)
                    { Owner = Window.GetWindow(_owner) };
                win.Show();
            }), "disk usage size treemap heatmap space analyze");

        // ── Colour label ──────────────────────────────────────────────────────
        AddSep();
        var labelTargets = tab.SelectedItems.Count > 0 ? tab.SelectedItems.ToList() : [item];
        labelRow = BuildLabelRow(labelTargets, menu);
        menu.Items.Add(labelRow);

        // ── Hide / Unhide ─────────────────────────────────────────────────────
        var hideTargets = tab.SelectedItems.Count > 0 ? tab.SelectedItems.ToList() : [item];
        bool unhide = item.IsHidden;
        Add(MakeMenuItem(unhide ? "Unhide" : "Hide", () =>
        {
            try
            {
                foreach (var t in hideTargets)
                {
                    var attr = File.GetAttributes(t.FullPath);
                    attr = unhide ? attr & ~FileAttributes.Hidden : attr | FileAttributes.Hidden;
                    File.SetAttributes(t.FullPath, attr);
                }
                tab.Reload();
            }
            catch (Exception ex) { ShowError(ex.Message); }
        }), unhide ? "unhide show reveal folder" : "hide conceal folder");

        // ── Lock / Unlock folder ──────────────────────────────────────────────
        if (item.IsDirectory)
        {
            AddSep();
            var lockRoot = FolderLockService.FindLockRoot(item.FullPath);
            if (!FolderLockService.IsLockRoot(item.FullPath))
            {
                Add(MakeMenuItem("Lock Folder…", () =>
                {
                    var dlg = new SetPasswordDialog(item.Name) { Owner = Window.GetWindow(_owner) };
                    if (dlg.ShowDialog() != true) return;
                    FolderLockService.Lock(item.FullPath, dlg.Password);
                    tab.Reload();
                }), "lock folder password protect privacy");
            }
            else
            {
                if (FolderLockService.IsUnlocked(item.FullPath))
                    Add(MakeMenuItem("Lock Now", () =>
                    {
                        FolderLockService.Relock(item.FullPath);
                        tab.Reload();
                    }), "lock now relock secure");
                else
                    Add(MakeMenuItem("Unlock…", () =>
                    {
                        if (lockRoot is null) return;
                        var pw = PromptFolderPassword(lockRoot, "Locked Folder",
                            $"\"{item.Name}\" is locked. Enter its password to open it.");
                        if (pw is null) return;
                        FolderLockService.Unlock(lockRoot, pw);
                        tab.Reload();
                    }), "unlock open password");

                Add(MakeMenuItem("Remove Lock…", () =>
                {
                    if (lockRoot is null) return;
                    var pw = PromptFolderPassword(lockRoot, "Remove Lock",
                        $"Enter the password for \"{item.Name}\" to remove its lock.");
                    if (pw is null) return;
                    FolderLockService.RemoveLock(item.FullPath, pw);
                    tab.Reload();
                }), "remove lock unprotect delete password");
            }
        }

        AddSep();
        Add(MakeMenuItem("Attributes & Timestamps…", () =>
        {
            var targets = (tab.SelectedItems.Count > 0 ? tab.SelectedItems : [item])
                .Select(i => i.FullPath).ToList();
            var win = new Zephyr.UI.Dialogs.BatchAttributesWindow(targets)
                { Owner = Window.GetWindow(_owner) };
            if (win.ShowDialog() == true) tab.Reload();
        }), "attributes timestamps read-only hidden system archive date modified created");

        Add(MakeMenuItem("Properties", () =>
        {
            var win = new Zephyr.UI.Windows.FilePropertiesWindow(item.FullPath)
                { Owner = Window.GetWindow(_owner) };
            win.ShowDialog();
        }), "properties info details");
        }

        // ── Search filtering ──────────────────────────────────────────────────
        searchBox.TextChanged += (_, _) =>
        {
            var hasText = !string.IsNullOrEmpty(searchBox.Text);
            placeholder.Visibility = hasText ? Visibility.Collapsed : Visibility.Visible;
            clearIcon.Visibility   = hasText ? Visibility.Visible   : Visibility.Collapsed;

            var q = searchBox.Text.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(q))
            {
                foreach (var (mi, _) in filterItems) mi.Visibility = Visibility.Visible;
                foreach (var s in sepList)           s.Visibility  = Visibility.Visible;
                if (labelRow != null) labelRow.Visibility = Visibility.Visible;
                return;
            }
            // Hide separators (and the colour-label row) while searching so results render flat
            foreach (var s in sepList) s.Visibility = Visibility.Collapsed;
            if (labelRow != null) labelRow.Visibility = Visibility.Collapsed;
            foreach (var (mi, label) in filterItems)
                mi.Visibility = label.Contains(q) ? Visibility.Visible : Visibility.Collapsed;
        };

        // Move keyboard focus to the search box once the menu is rendered
        menu.Opened += (_, _) =>
        {
            searchBox.Focus();
            Keyboard.Focus(searchBox);
        };

        menu.PlacementTarget = anchor;
        menu.Placement       = PlacementMode.MousePoint;
        menu.IsOpen          = true;
    }

    // Builds the colour-label swatch row for the context menu: a circle per palette colour
    // (ringed if currently applied) plus a clear (×) chip. Clicking applies/clears the label
    // on every target (the full selection, or just the right-clicked item) and closes the menu.
    private MenuItem BuildLabelRow(IReadOnlyList<FileItem> targets, ContextMenu menu)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 5, 12, 5) };

        // Show the current selection's ring only when a single item is selected.
        string? currentKey = targets.Count == 1 ? FileLabelService.GetKey(targets[0].FullPath) : null;

        foreach (var lbl in FileLabels.All)
        {
            var fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(lbl.Hex));
            fill.Freeze();
            var swatch = new Border
            {
                Width           = 18,
                Height          = 18,
                CornerRadius    = new CornerRadius(9),
                Margin          = new Thickness(0, 0, 7, 0),
                Background      = fill,
                Cursor          = Cursors.Hand,
                ToolTip         = lbl.Name,
                BorderThickness = new Thickness(currentKey == lbl.Key ? 2 : 0),
            };
            swatch.SetResourceReference(Border.BorderBrushProperty, "ZephyrTextPrimary");
            var key = lbl.Key;
            swatch.MouseLeftButtonUp += (_, _) =>
            {
                foreach (var t in targets)
                {
                    FileLabelService.Set(t.FullPath, key);
                    t.LabelColor = FileLabelService.GetHex(t.FullPath);
                }
                menu.IsOpen = false;
            };
            row.Children.Add(swatch);
        }

        // Clear (×) chip — removes any label from the targets.
        var clear = new Border
        {
            Width           = 18,
            Height          = 18,
            CornerRadius    = new CornerRadius(9),
            Margin          = new Thickness(3, 0, 0, 0),
            Cursor          = Cursors.Hand,
            ToolTip         = "No label",
            BorderThickness = new Thickness(1),
        };
        clear.SetResourceReference(Border.BorderBrushProperty, "ZephyrBorder");
        clear.SetResourceReference(Border.BackgroundProperty, "ZephyrElevated");
        var x = new TextBlock
        {
            FontFamily          = new FontFamily("Segoe Fluent Icons"),
            Text                = "",
            FontSize            = 9,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Center,
        };
        x.SetResourceReference(TextBlock.ForegroundProperty, "ZephyrTextSecondary");
        clear.Child = x;
        clear.MouseLeftButtonUp += (_, _) =>
        {
            foreach (var t in targets)
            {
                FileLabelService.Set(t.FullPath, null);
                t.LabelColor = string.Empty;
            }
            menu.IsOpen = false;
        };
        row.Children.Add(clear);

        // Bare template so the row gets no MenuItem hover highlight or padding.
        var cpFactory = new FrameworkElementFactory(typeof(ContentPresenter));
        cpFactory.SetBinding(ContentPresenter.ContentProperty, new System.Windows.Data.Binding
        {
            RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent),
            Path           = new PropertyPath(MenuItem.HeaderProperty),
        });
        var bare = new ControlTemplate(typeof(MenuItem)) { VisualTree = cpFactory };

        return new MenuItem
        {
            Header     = row,
            Template   = bare,
            Padding    = new Thickness(0),
            Margin     = new Thickness(0),
            Focusable  = false,
        };
    }

    // Runs an archive operation off the menu, surfacing any failure to the user.
    private static async Task RunArchiveAsync(Func<Task> op)
    {
        try   { await op(); }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private static void ShowError(string msg) =>
        ZephyrMessageBox.Show(msg, "Error");

    // Prompts (with retry) for a locked folder's password until it verifies or the user cancels.
    private string? PromptFolderPassword(LockedFolder root, string title, string prompt)
        => PasswordPrompt.Ask(Window.GetWindow(_owner), title, prompt, pw => FolderLockService.Verify(root, pw));

    private static MenuItem MakeMenuItem(string header, Action onClick,
        string? gestureText = null, bool enabled = true)
    {
        var mi = new MenuItem
        {
            Header           = header,
            IsEnabled        = enabled,
            InputGestureText = gestureText ?? string.Empty
        };
        mi.Click += (_, _) => onClick();
        return mi;
    }
}
