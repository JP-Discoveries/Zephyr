using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Zephyr.Core.Models;
using Zephyr.Core.Security;

namespace Zephyr.Core.Search;

/// <summary>
/// Directory walker behind the search bar. The tree is walked once by a single producer
/// (an explicit stack, not async recursion — nesting async iterators per directory level
/// makes every result pay the full depth on each step). Name/type/size/date filtering
/// happens in the producer; content matching is fanned out to a bounded set of workers
/// because it is IO-bound and far more expensive than the walk itself.
/// </summary>
public class SearchEngine
{
    public static readonly IReadOnlyDictionary<FileTypeFilter, string[]> TypeExtensions =
        new Dictionary<FileTypeFilter, string[]>
        {
            [FileTypeFilter.Documents]   = [".txt",".doc",".docx",".pdf",".xls",".xlsx",".ppt",".pptx",".odt",".rtf",".md",".csv"],
            [FileTypeFilter.Images]      = [".jpg",".jpeg",".png",".gif",".bmp",".svg",".webp",".ico",".tiff",".raw"],
            [FileTypeFilter.Video]       = [".mp4",".avi",".mkv",".mov",".wmv",".flv",".webm",".m4v"],
            [FileTypeFilter.Audio]       = [".mp3",".wav",".flac",".aac",".ogg",".wma",".m4a",".opus"],
            [FileTypeFilter.Archives]    = [".zip",".rar",".7z",".tar",".gz",".bz2",".xz",".zst"],
            [FileTypeFilter.Code]        = [".cs",".py",".js",".ts",".java",".cpp",".c",".h",".go",".rs",".rb",".php",".html",".css",".sql",".json",".xml",".yaml",".yml",".sh",".ps1",".jsx",".tsx",".vue",".swift",".kt",".dart"],
            [FileTypeFilter.Executables] = [".exe",".dll",".msi",".bat",".cmd",".ps1",".sh",".app",".deb",".rpm"],
        };

    // Files larger than this are skipped in content search to keep scans bounded.
    private const long MaxContentBytes = 20L * 1024 * 1024;

    // Bounded queues give the producer backpressure so a fast walk can't buffer an
    // entire drive's worth of candidates ahead of a slow consumer.
    private const int WalkQueueCapacity   = 2048;
    private const int ResultQueueCapacity = 1024;

    private static readonly EnumerationOptions EnumOptions = new()
    {
        // Keep walking past folders we can't open instead of losing the rest of the
        // parent's entries, which is what an unguarded enumerator does when it throws.
        IgnoreInaccessible    = true,
        RecurseSubdirectories = false,
        AttributesToSkip      = 0, // hidden/system are decided below, per user settings
    };

    public async IAsyncEnumerable<FileItem> SearchAsync(
        SearchOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Non-filesystem locations (This PC, archives, phones) have no walkable root.
        if (string.IsNullOrEmpty(options.SearchRoot) || !Directory.Exists(options.SearchRoot))
            yield break;

        // Compile the pattern once for the whole scan rather than per file.
        Regex? rx = null;
        if (options.UseRegex && !string.IsNullOrEmpty(options.Query))
        {
            var opts = RegexOptions.CultureInvariant
                       | (options.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase)
                       | (options.MatchContent ? RegexOptions.Compiled : RegexOptions.None);
            try { rx = new Regex(options.Query, opts); }
            catch { yield break; } // invalid pattern → no matches at all
        }

        var walk = Channel.CreateBounded<FileItem>(new BoundedChannelOptions(WalkQueueCapacity)
        {
            SingleReader = !options.MatchContent, // content mode fans out to grep workers
        });

        // In name mode the walk output *is* the result stream; content mode inserts a grep
        // stage between the two. Failures travel to the reader as channel completion, so
        // there is no task left for the caller to await.
        var results = options.MatchContent
            ? Channel.CreateBounded<FileItem>(new BoundedChannelOptions(ResultQueueCapacity) { SingleReader = true })
            : walk;

        // ct is honoured inside the bodies rather than handed to Task.Run: a task cancelled
        // before it ever starts would never complete its channel, hanging the reader.
        _ = Task.Run(async () =>
        {
            try { await WalkAsync(options, rx, walk.Writer, ct); walk.Writer.TryComplete(); }
            catch (Exception ex) { walk.Writer.TryComplete(ex); }
        }, CancellationToken.None);

        if (options.MatchContent)
            _ = Task.Run(async () =>
            {
                try
                {
                    await Parallel.ForEachAsync(
                        walk.Reader.ReadAllAsync(ct),
                        new ParallelOptions
                        {
                            MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 2, 8),
                            CancellationToken      = ct,
                        },
                        async (candidate, token) =>
                        {
                            if (await ContentMatches(candidate, options, rx, token))
                                await results.Writer.WriteAsync(candidate, token);
                        });
                    results.Writer.TryComplete();
                }
                catch (Exception ex) { results.Writer.TryComplete(ex); }
            }, CancellationToken.None);

        try
        {
            await foreach (var item in results.Reader.ReadAllAsync(ct))
                yield return item;
        }
        finally
        {
            // The reader is gone — cancelled, or the caller stopped early. Close the
            // pipeline so the walk and grep stages unblock instead of parking forever on
            // a bounded channel nobody is draining.
            walk.Writer.TryComplete();
            results.Writer.TryComplete();
        }
    }

    /// <summary>
    /// Walks the tree, writing everything that passes the name/type/size/date filters.
    /// In content mode files are written unfiltered by name — the query is applied to
    /// their contents by the grep workers instead.
    /// <para>
    /// Directories are handed out to a small pool of workers from a shared queue. Walking
    /// is dominated by per-directory metadata latency rather than CPU, so a single
    /// sequential walker spends most of its time waiting; overlapping several folders is
    /// what makes a deep tree finish in a usable amount of time.
    /// </para>
    /// </summary>
    private static async Task WalkAsync(
        SearchOptions options, Regex? rx, ChannelWriter<FileItem> output, CancellationToken ct)
    {
        var queue = new ConcurrentQueue<string>();
        queue.Enqueue(options.SearchRoot);

        // Directories queued but not yet finished. Reaching zero means the walk is done —
        // there is no other way to know, since any folder may still add more.
        var outstanding = 1;
        // One permit per queued directory, so a worker only wakes when there is work.
        using var available = new SemaphoreSlim(1);

        var workerCount = options.Scope == SearchScope.Recursive
            ? Math.Clamp(Environment.ProcessorCount / 2, 2, 8)
            : 1;

        var workers = new Task[workerCount];
        for (var i = 0; i < workerCount; i++) workers[i] = Task.Run(WorkAsync, ct);
        await Task.WhenAll(workers);

        async Task WorkAsync()
        {
            while (true)
            {
                await available.WaitAsync(ct);
                // Woken with an empty queue = the completion flood below; nothing left.
                if (!queue.TryDequeue(out var directory)) return;

                try { await ScanDirectoryAsync(directory); }
                finally
                {
                    if (Interlocked.Decrement(ref outstanding) == 0)
                        available.Release(workerCount); // wake every worker so they can exit
                }
            }
        }

        async Task ScanDirectoryAsync(string directory)
        {
            IEnumerator<FileSystemInfo> entries;
            try { entries = new DirectoryInfo(directory).EnumerateFileSystemInfos("*", EnumOptions).GetEnumerator(); }
            catch { return; }

            using (entries)
            {
                while (true)
                {
                    ct.ThrowIfCancellationRequested();

                    FileSystemInfo entry;
                    try
                    {
                        if (!entries.MoveNext()) break;
                        entry = entries.Current;
                    }
                    catch { break; } // enumeration died mid-folder; keep the rest of the tree

                    var attrs = entry.Attributes;
                    var excluded = (!options.IncludeHidden && (attrs & FileAttributes.Hidden) != 0)
                                || (!options.IncludeSystem && (attrs & FileAttributes.System) != 0);
                    var isDirectory = (attrs & FileAttributes.Directory) != 0;

                    if (isDirectory && options.Scope == SearchScope.Recursive
                        // A folder the user has excluded from results is not worth the
                        // scan either — descending into $Recycle.Bin and AppData was most
                        // of the cost of searching a drive or a user profile.
                        && !excluded
                        // Junctions and symlinks alias trees we either already walk or
                        // that loop back on themselves (AppData\Local\Application Data).
                        && (attrs & FileAttributes.ReparsePoint) == 0
                        // Don't descend into a locked folder that hasn't been unlocked this
                        // session — search would leak the names the lock is meant to hide.
                        && !FolderLockService.IsGated(entry.FullName))
                    {
                        Interlocked.Increment(ref outstanding);
                        queue.Enqueue(entry.FullName);
                        available.Release();
                    }

                    if (excluded) continue;
                    if (options.MatchContent && isDirectory) continue; // folders have no contents to grep
                    if (!Matches(entry, attrs, options, rx)) continue;

                    await output.WriteAsync(ToItem(entry, attrs, isDirectory), ct);
                }
            }
        }
    }

    private static FileItem ToItem(FileSystemInfo entry, FileAttributes attrs, bool isDirectory) => new()
    {
        Name           = entry.Name,
        FullPath       = entry.FullName,
        IsDirectory    = isDirectory,
        Size           = entry is FileInfo fi ? fi.Length : 0,
        LastModified   = entry.LastWriteTime,
        Created        = entry.CreationTime,
        Extension      = isDirectory ? string.Empty : Path.GetExtension(entry.Name).ToLowerInvariant(),
        Attributes     = attrs,
        SearchLocation = Path.GetDirectoryName(entry.FullName) ?? string.Empty,
    };

    /// <summary>In content mode, returns true if the file's text contains the query.
    /// Binary files, empty files and files over the size cap never match.</summary>
    private static async Task<bool> ContentMatches(
        FileItem item, SearchOptions options, Regex? rx, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(options.Query)) return true;
        if (item.Size == 0 || item.Size > MaxContentBytes) return false;

        try
        {
            await using var stream = new FileStream(
                item.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                64 * 1024, FileOptions.SequentialScan | FileOptions.Asynchronous);

            // Binary sniff: a NUL byte in the first 8 KB means it isn't grep-able text.
            var headLength = (int)Math.Min(8192, item.Size);
            var head = ArrayPool<byte>.Shared.Rent(headLength);
            try
            {
                int read = await stream.ReadAsync(head.AsMemory(0, headLength), ct);
                if (head.AsSpan(0, read).IndexOf((byte)0) >= 0) return false;
            }
            finally { ArrayPool<byte>.Shared.Return(head); }
            stream.Position = 0;

            using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);

            if (rx is not null)
            {
                string? line;
                while ((line = await reader.ReadLineAsync(ct)) is not null)
                    if (rx.IsMatch(line)) return true;
                return false;
            }

            var cmp = options.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            return await ContainsAsync(reader, options.Query, cmp, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch { return false; } // unreadable/locked file → skip
    }

    /// <summary>Scans the reader for a literal substring over pooled char buffers,
    /// carrying query.Length-1 chars across reads so a match spanning a buffer
    /// boundary is still found. Avoids the string-per-line cost of ReadLineAsync.</summary>
    private static async Task<bool> ContainsAsync(
        StreamReader reader, string query, StringComparison cmp, CancellationToken ct)
    {
        var size = Math.Max(64 * 1024, query.Length * 2);
        var buffer = ArrayPool<char>.Shared.Rent(size);
        try
        {
            int overlap = query.Length - 1;
            int carried = 0;
            while (true)
            {
                int read = await reader.ReadAsync(buffer.AsMemory(carried, buffer.Length - carried), ct);
                if (read == 0) return false;

                int total = carried + read;
                if (buffer.AsSpan(0, total).IndexOf(query.AsSpan(), cmp) >= 0) return true;

                carried = Math.Min(overlap, total);
                if (carried > 0)
                    buffer.AsSpan(total - carried, carried).CopyTo(buffer.AsSpan(0, carried));
            }
        }
        finally { ArrayPool<char>.Shared.Return(buffer); }
    }

    private static bool Matches(FileSystemInfo entry, FileAttributes attrs, SearchOptions options, Regex? rx)
    {
        var isDirectory = (attrs & FileAttributes.Directory) != 0;

        // Name / regex match — skipped in content mode, where the query is matched
        // against file contents instead (see ContentMatches).
        if (!options.MatchContent && !string.IsNullOrEmpty(options.Query))
        {
            var hit = rx is not null
                ? rx.IsMatch(entry.Name)
                : entry.Name.Contains(options.Query,
                    options.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
            if (!hit) return false;
        }

        // Type filter
        if (options.TypeFilter == FileTypeFilter.Folders)
            return isDirectory;

        if (options.CustomExtensions != null)
        {
            if (isDirectory) return false;
            if (!options.CustomExtensions.Contains(Path.GetExtension(entry.Name).ToLowerInvariant())) return false;
        }
        else if (options.TypeFilter != FileTypeFilter.All)
        {
            if (isDirectory) return false;
            if (!TypeExtensions.TryGetValue(options.TypeFilter, out var exts)) return false;
            if (!exts.Contains(Path.GetExtension(entry.Name).ToLowerInvariant())) return false;
        }

        // Size filter
        if (options.SizeFilter != SizeFilter.All && entry is FileInfo sfi)
        {
            var len = sfi.Length;
            var ok = options.SizeFilter switch
            {
                SizeFilter.Tiny   => len < 100 * 1024,
                SizeFilter.Small  => len < 1024 * 1024,
                SizeFilter.Medium => len < 100L * 1024 * 1024,
                SizeFilter.Large  => len < 1024L * 1024 * 1024,
                SizeFilter.Huge   => len >= 1024L * 1024 * 1024,
                _                 => true
            };
            if (!ok) return false;
        }

        // Date filter
        if (options.DateFilter != DateFilter.All)
        {
            var now = DateTime.Now;
            var ok = options.DateFilter switch
            {
                DateFilter.Today     => entry.LastWriteTime.Date == now.Date,
                DateFilter.Yesterday => entry.LastWriteTime.Date == now.Date.AddDays(-1),
                DateFilter.ThisWeek  => entry.LastWriteTime >= now.AddDays(-7),
                DateFilter.ThisMonth => entry.LastWriteTime.Month == now.Month && entry.LastWriteTime.Year == now.Year,
                DateFilter.ThisYear  => entry.LastWriteTime.Year == now.Year,
                _                   => true
            };
            if (!ok) return false;
        }

        return true;
    }
}
