using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using Zephyr.Core.Archives;
using Zephyr.UI.Dialogs;

namespace Zephyr.UI.ViewModels;

// Toolbar archive commands: extract one or many archives, and compress the current
// selection (optionally appending to an existing zip), both behind the progress dialog.
public partial class MainViewModel
{
    [RelayCommand]
    private void ExtractZip()
    {
        var tab = ActiveTab;
        if (tab is null) return;

        var archives = tab.SelectedItems
            .Where(i => !i.IsDirectory && ZephyrArchiveService.CanExtract(i.FullPath))
            .ToList();
        if (archives.Count == 0) return;

        // Single archive defaults to its own subfolder; a batch defaults to the current folder.
        var defaultDest = archives.Count == 1
            ? Path.Combine(tab.CurrentPath, ZephyrArchiveService.StripArchiveExtension(archives[0].Name))
            : tab.CurrentPath;

        var dlg = new ExtractDialog(archives.Select(a => a.Name).ToList(), defaultDest)
            { Owner = Application.Current.MainWindow };
        if (dlg.ShowDialog() != true) return;

        var opts  = new ZephyrArchiveService.ExtractOptions(Password: dlg.Password);
        var title = archives.Count == 1 ? $"Extracting {archives[0].Name}…" : $"Extracting {archives.Count} archives…";

        ArchiveProgressDialog.Run(Application.Current.MainWindow, title, async (prog, ct) =>
        {
            for (int i = 0; i < archives.Count; i++)
            {
                var archive = archives[i];
                var dest = archives.Count == 1 ? dlg.Destination
                         : dlg.EachToOwnSubfolder ? Path.Combine(dlg.Destination, ZephyrArchiveService.StripArchiveExtension(archive.Name))
                         : dlg.Destination;

                // For a batch, prefix each report with "(i/n) name" so the user sees which archive.
                int idx = i + 1;
                IProgress<ZephyrArchiveService.ArchiveProgress> sub = archives.Count == 1
                    ? prog
                    : new Progress<ZephyrArchiveService.ArchiveProgress>(p =>
                        prog.Report(p with { CurrentEntry = $"({idx}/{archives.Count}) {archive.Name} — {p.CurrentEntry}" }));

                await ZephyrArchiveService.ExtractAsync(archive.FullPath, dest, opts, sub, ct);
            }
        });
        tab.Reload();
    }

    [RelayCommand]
    private void CreateZip()
    {
        var tab   = ActiveTab;
        var items = tab?.SelectedItems;
        if (tab is null || items is null || items.Count == 0) return;

        var defaultName = items.Count == 1 ? Path.GetFileNameWithoutExtension(items[0].Name) : "Archive";
        var dlg = new CompressDialog(defaultName, tab.CurrentPath, items.Count)
            { Owner = Application.Current.MainWindow };
        if (dlg.ShowDialog() != true) return;

        var sources = items.Select(i => i.FullPath).ToList();
        var name    = Path.GetFileName(dlg.ResultPath);
        if (dlg.AddToExisting)
            ArchiveProgressDialog.Run(Application.Current.MainWindow, $"Adding to {name}…",
                (prog, ct) => ZephyrArchiveService.AppendToZipAsync(dlg.ResultPath, sources, dlg.Options.Level, prog, ct));
        else
            ArchiveProgressDialog.Run(Application.Current.MainWindow, $"Compressing {name}…",
                (prog, ct) => ZephyrArchiveService.CreateAsync(dlg.ResultPath, sources, dlg.Options, prog, ct));
        tab.Reload();
    }
}
