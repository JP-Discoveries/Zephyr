using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Zephyr.Core.Models;
using Zephyr.Core.Security;

namespace Zephyr.Core.Search;

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

    public async IAsyncEnumerable<FileItem> SearchAsync(
        SearchOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var item in ScanAsync(options.SearchRoot, options, ct))
            yield return item;
    }

    private async IAsyncEnumerable<FileItem> ScanAsync(
        string directory, SearchOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (ct.IsCancellationRequested) yield break;

        DirectoryInfo dir;
        try { dir = new DirectoryInfo(directory); }
        catch { yield break; }

        IEnumerable<FileSystemInfo> entries;
        try { entries = dir.EnumerateFileSystemInfos(); }
        catch { yield break; }

        int count = 0;
        foreach (var entry in entries)
        {
            if (ct.IsCancellationRequested) yield break;

            var isHidden = (entry.Attributes & FileAttributes.Hidden) != 0;
            var isSystem = (entry.Attributes & FileAttributes.System) != 0;
            if (!options.IncludeHidden && isHidden) goto recurse;
            if (!options.IncludeSystem && isSystem) goto recurse;

            if (Matches(entry, options))
            {
                yield return new FileItem
                {
                    Name           = entry.Name,
                    FullPath       = entry.FullName,
                    IsDirectory    = entry is DirectoryInfo,
                    Size           = entry is FileInfo fi ? fi.Length : 0,
                    LastModified   = entry.LastWriteTime,
                    Created        = entry.CreationTime,
                    Extension      = entry is FileInfo
                                     ? Path.GetExtension(entry.Name).ToLowerInvariant()
                                     : string.Empty,
                    Attributes     = entry.Attributes,
                    SearchLocation = Path.GetDirectoryName(entry.FullName) ?? string.Empty
                };
            }

            recurse:
            // Don't descend into a locked folder that hasn't been unlocked this session —
            // otherwise search would leak the names of files the lock is meant to hide.
            if (entry is DirectoryInfo sub && options.Scope == SearchScope.Recursive
                && !FolderLockService.IsGated(sub.FullName))
                await foreach (var r in ScanAsync(sub.FullName, options, ct))
                    yield return r;

            if (++count % 100 == 0) await Task.Yield();
        }
    }

    private static bool Matches(FileSystemInfo entry, SearchOptions options)
    {
        // Name / regex match
        if (!string.IsNullOrEmpty(options.Query))
        {
            bool hit;
            if (options.UseRegex)
            {
                try
                {
                    var rx = options.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;
                    hit = Regex.IsMatch(entry.Name, options.Query, rx);
                }
                catch { return false; }
            }
            else
            {
                var cmp = options.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
                hit = entry.Name.Contains(options.Query, cmp);
            }
            if (!hit) return false;
        }

        // Type filter
        if (options.TypeFilter == FileTypeFilter.Folders)
            return entry is DirectoryInfo;

        if (options.CustomExtensions != null)
        {
            if (entry is not FileInfo cfi) return false;
            if (!options.CustomExtensions.Contains(cfi.Extension.ToLowerInvariant())) return false;
        }
        else if (options.TypeFilter != FileTypeFilter.All)
        {
            if (entry is not FileInfo fileInfo) return false;
            if (!TypeExtensions.TryGetValue(options.TypeFilter, out var exts)) return false;
            if (!exts.Contains(fileInfo.Extension.ToLowerInvariant())) return false;
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
