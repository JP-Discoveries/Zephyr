using Zephyr.Core.Models;
using Zephyr.Core.Settings;

namespace Zephyr.UI.FileSystem;

/// <summary>Inputs a provider may need to produce a listing.</summary>
public sealed record DirectoryLoadContext(ZephyrSettings Settings, bool FlatView);

/// <summary>
/// Outcome of a directory load. <see cref="Items"/> == null signals the load was aborted
/// (e.g. a password prompt was cancelled); <see cref="RedirectPath"/>, if set, is where
/// the caller should navigate instead.
/// </summary>
public sealed record DirectoryListing
{
    public List<FileItem>? Items { get; init; }
    public string? RedirectPath { get; init; }
    /// <summary>Local on-disk folders get the full enrichment treatment plus a file watcher.</summary>
    public bool IsLocalFolder { get; init; }
    /// <summary>Real filesystem path to watch (local folders only); null disables the watcher.</summary>
    public string? WatchPath { get; init; }
    /// <summary>Whether to kick off image-thumbnail prefetch after load (false for This PC).</summary>
    public bool LoadsThumbnails { get; init; }

    public static readonly DirectoryListing Aborted = new() { Items = null };
    public static DirectoryListing Redirect(string path) => new() { Items = null, RedirectPath = path };
}

/// <summary>
/// Produces the item list for a given location. Each tab holds an ordered set of providers;
/// the first whose <see cref="CanHandle"/> returns true wins, with the local-folder provider
/// acting as the catch-all fallback.
/// </summary>
public interface IDirectoryProvider
{
    bool CanHandle(string path);
    Task<DirectoryListing> LoadAsync(string path, DirectoryLoadContext ctx, CancellationToken ct);
}
