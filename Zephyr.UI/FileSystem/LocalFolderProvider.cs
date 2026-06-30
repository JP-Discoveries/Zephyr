using System.IO;
using System.Windows;
using Zephyr.Core.FileSystem;
using Zephyr.Core.Models;
using Zephyr.Core.Security;
using Zephyr.Core.Settings;
using Zephyr.UI.Dialogs;
using Zephyr.UI.ViewModels;

namespace Zephyr.UI.FileSystem;

// Real on-disk folders — the catch-all provider. Handles the folder-lock gate and the
// optional flat (recursive) enumeration; the tab applies the rest of the local enrichment.
public sealed class LocalFolderProvider(FileSystemService fs) : IDirectoryProvider
{
    // Fallback: registered last, so it only runs when no virtual provider matched.
    public bool CanHandle(string path) => true;

    public async Task<DirectoryListing> LoadAsync(string path, DirectoryLoadContext ctx, CancellationToken ct)
    {
        // ── Folder lock gate (mirror the archive auth flow) ───────────────────
        if (FolderLockService.FindLockRoot(path) is { } lockRoot
            && !FolderLockService.IsUnlocked(lockRoot.Path))
        {
            if (!PromptFolderUnlock(lockRoot))
            {
                // Cancelled — bounce out to the nearest accessible location.
                var parent = fs.GetParent(lockRoot.Path);
                return DirectoryListing.Redirect(
                    parent != null && Directory.Exists(parent) && !FolderLockService.IsGated(parent)
                        ? parent : TabViewModel.ThisPcPath);
            }
        }

        var s = ctx.Settings;
        var items = ctx.FlatView
            ? await Task.Run(() => LoadFlatItems(path, s, ct), ct)
            : await Task.Run(() => fs.GetContents(path, s.ShowHiddenFiles, s.ShowSystemFiles).ToList(), ct);

        if (ct.IsCancellationRequested) return DirectoryListing.Aborted;

        return new DirectoryListing
        {
            Items           = items,
            IsLocalFolder   = true,
            WatchPath       = path,
            LoadsThumbnails = true,
        };
    }

    // Prompts for a locked folder's password, retrying until correct or cancelled.
    // On success the root is marked unlocked for the rest of the session.
    private static bool PromptFolderUnlock(LockedFolder root)
    {
        var name = Path.GetFileName(root.Path.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : root.Path;
        return PasswordPrompt.Ask(Application.Current.MainWindow, "Locked Folder",
            $"\"{name}\" is locked. Enter its password to open it.",
            pw => FolderLockService.Unlock(root, pw)) != null;
    }

    private static List<FileItem> LoadFlatItems(string path, ZephyrSettings s, CancellationToken ct)
    {
        var results = new List<FileItem>();
        EnumerateFlat(new DirectoryInfo(path), results, s, ct);
        return results;
    }

    private static void EnumerateFlat(DirectoryInfo dir, List<FileItem> results, ZephyrSettings s, CancellationToken ct)
    {
        if (ct.IsCancellationRequested || results.Count > 10_000) return;
        try
        {
            foreach (var entry in dir.EnumerateFileSystemInfos())
            {
                if (ct.IsCancellationRequested || results.Count > 10_000) return;
                if (!s.ShowHiddenFiles && (entry.Attributes & FileAttributes.Hidden) != 0) continue;
                if (!s.ShowSystemFiles && (entry.Attributes & FileAttributes.System) != 0) continue;
                results.Add(new FileItem
                {
                    Name           = entry.Name,
                    FullPath       = entry.FullName,
                    IsDirectory    = entry is DirectoryInfo,
                    Size           = entry is FileInfo fi ? fi.Length : 0,
                    LastModified   = entry.LastWriteTime,
                    Created        = entry.CreationTime,
                    Extension      = entry is FileInfo ? Path.GetExtension(entry.Name).ToLowerInvariant() : string.Empty,
                    Attributes     = entry.Attributes,
                    SearchLocation = dir.FullName
                });
                if (entry is DirectoryInfo sub)
                    EnumerateFlat(sub, results, s, ct);
            }
        }
        catch (UnauthorizedAccessException) { }
    }
}
