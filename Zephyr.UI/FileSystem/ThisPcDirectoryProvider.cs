using System.IO;
using Zephyr.Core.FileSystem;
using Zephyr.Core.Models;
using Zephyr.UI.ViewModels;

namespace Zephyr.UI.FileSystem;

// The "This PC" root: lists drives as folder-like items.
public sealed class ThisPcDirectoryProvider(FileSystemService fs) : IDirectoryProvider
{
    public bool CanHandle(string path) => path == TabViewModel.ThisPcPath;

    public async Task<DirectoryListing> LoadAsync(string path, DirectoryLoadContext ctx, CancellationToken ct)
    {
        var drives = await Task.Run(() => fs.GetDrives()
            .Select(d => new FileItem
            {
                Name         = d.DisplayName,
                FullPath     = d.Name,
                IsDirectory  = true,
                Attributes   = FileAttributes.Directory,
                LastModified = DateTime.MinValue,
                Created      = DateTime.MinValue,
            })
            .ToList(), ct);

        return ct.IsCancellationRequested
            ? DirectoryListing.Aborted
            : new DirectoryListing { Items = drives };
    }
}
