using System.IO;
using Zephyr.Core.Models;
using Zephyr.UI.Services;

namespace Zephyr.UI.FileSystem;

// Portable (WPD/MTP) device browsing — phones, cameras, media players.
public sealed class WpdDirectoryProvider : IDirectoryProvider
{
    public bool CanHandle(string path) => WpdProvider.IsWpdPath(path);

    public async Task<DirectoryListing> LoadAsync(string path, DirectoryLoadContext ctx, CancellationToken ct)
    {
        var (deviceId, objectId) = WpdProvider.ParsePath(path);
        var wpdItems = await Task.Run(() => WpdProvider.GetChildren(deviceId, objectId), ct);
        if (ct.IsCancellationRequested) return DirectoryListing.Aborted;

        var items = wpdItems.Select(w => new FileItem
        {
            Name         = w.Name,
            FullPath     = WpdProvider.MakePath(deviceId, w.ObjectId),
            IsDirectory  = w.IsFolder,
            Size         = w.Size,
            LastModified = w.DateModified,
            Created      = w.DateModified,
            Extension    = w.IsFolder ? string.Empty
                         : Path.GetExtension(w.Name).ToLowerInvariant(),
            Attributes   = w.IsFolder ? FileAttributes.Directory : FileAttributes.Normal,
        }).ToList();

        return new DirectoryListing { Items = items, LoadsThumbnails = true };
    }
}
