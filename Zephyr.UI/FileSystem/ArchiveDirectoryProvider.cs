using System.IO;
using System.Windows;
using Zephyr.Core.Archives;
using Zephyr.Core.Models;
using Zephyr.UI.Dialogs;
using Zephyr.UI.ViewModels;

namespace Zephyr.UI.FileSystem;

// Browsing inside an archive (read-only). Owns the per-tab password cache so the rest of
// the tab (preview, open-entry) can reuse a once-entered password.
public sealed class ArchiveDirectoryProvider : IDirectoryProvider
{
    // Per-archive password cache (key present = auth handled; value null = not encrypted).
    private readonly Dictionary<string, string?> _auth = new(StringComparer.OrdinalIgnoreCase);

    public bool CanHandle(string path) => ArchivePath.IsArchivePath(path);

    /// <summary>Cached password for an archive, or null if none/unknown.</summary>
    public string? GetCachedPassword(string archiveFile) => _auth.GetValueOrDefault(archiveFile);

    public async Task<DirectoryListing> LoadAsync(string path, DirectoryLoadContext ctx, CancellationToken ct)
    {
        var (archiveFile, inner) = ArchivePath.Parse(path);

        // Resolve a password once per archive (encrypted only). Cancel → leave the archive.
        if (!_auth.TryGetValue(archiveFile, out var pw))
        {
            if (await Task.Run(() => ZephyrArchiveService.IsEncrypted(archiveFile), ct))
            {
                pw = PromptPassword(archiveFile);
                if (pw is null)
                {
                    var folder = Path.GetDirectoryName(archiveFile);
                    return DirectoryListing.Redirect(
                        folder != null && Directory.Exists(folder) ? folder : TabViewModel.ThisPcPath);
                }
            }
            _auth[archiveFile] = pw;
        }

        var children = await Task.Run(() => ZephyrArchiveService.GetChildren(archiveFile, inner, pw), ct);
        if (ct.IsCancellationRequested) return DirectoryListing.Aborted;

        var items = children.Select(c => new FileItem
        {
            Name         = Path.GetFileName(c.Path),
            FullPath     = ArchivePath.Make(archiveFile, c.Path),
            IsDirectory  = c.IsDirectory,
            Size         = c.Size,
            LastModified = c.Modified,
            Created      = c.Modified,
            Extension    = c.IsDirectory ? string.Empty : Path.GetExtension(c.Path).ToLowerInvariant(),
            Attributes   = c.IsDirectory ? FileAttributes.Directory : FileAttributes.Normal,
        }).ToList();

        return new DirectoryListing { Items = items, LoadsThumbnails = true };
    }

    // Shows a password prompt (with retry on a wrong password) until the user enters a valid
    // password or cancels. Returns the validated password, or null if cancelled.
    private static string? PromptPassword(string archiveFile)
    {
        bool retry = false;
        while (true)
        {
            var dlg = new PasswordDialog(Path.GetFileName(archiveFile), retry) { Owner = Application.Current.MainWindow };
            if (dlg.ShowDialog() != true) return null;
            if (ZephyrArchiveService.ValidatePassword(archiveFile, dlg.Password)) return dlg.Password;
            retry = true;
        }
    }
}
