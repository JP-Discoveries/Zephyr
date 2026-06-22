using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;
using SharpZip = ICSharpCode.SharpZipLib.Zip;
using NetCompressionLevel = System.IO.Compression.CompressionLevel;

namespace Zephyr.Core.Archives;

/// <summary>
/// Native-backed archive engine for Zephyr. Writes zip/tar/tar.gz/gz using the
/// .NET BCL (native zlib + System.Formats.Tar). Extracts those natively and falls
/// back to SharpCompress (managed) for formats the BCL can't read: 7z, rar, bz2, xz.
/// All operations stream in chunks and report byte-level progress.
/// </summary>
public static class ZephyrArchiveService
{
    /// <summary>Formats Zephyr can create. All are written with native BCL code.</summary>
    public enum WriteFormat { Zip, TarGz, Tar, Gz }

    /// <summary>User-facing compression effort, mapped to native levels per format.</summary>
    public enum Level { Store, Fastest, Normal, Maximum }

    /// <summary>Zip encryption method when a password is set. AES-256 is secure but not readable by
    /// Windows Explorer's built-in zip; ZipCrypto is weak but universally compatible.</summary>
    public enum ZipEncryption { Aes256, ZipCrypto }

    public sealed record CompressOptions(
        WriteFormat   Format     = WriteFormat.Zip,
        Level         Level      = Level.Normal,
        string?       Password   = null,
        ZipEncryption Encryption = ZipEncryption.Aes256);

    public sealed record ExtractOptions(
        string? Password  = null,
        bool    Overwrite = true);

    /// <summary>Byte-level progress snapshot reported during compress/extract.</summary>
    public sealed record ArchiveProgress(long ProcessedBytes, long TotalBytes, string CurrentEntry)
    {
        public double Fraction => TotalBytes > 0
            ? Math.Clamp((double)ProcessedBytes / TotalBytes, 0, 1)
            : 0;
    }

    /// <summary>A single child node when browsing inside an archive.</summary>
    public sealed record ArchiveEntryInfo(string Path, bool IsDirectory, long Size, DateTime Modified);

    private const int ChunkSize = 1 << 20; // 1 MB — granularity of progress + cancellation

    // Extensions we can extract. Compound (.tar.*) entries are matched first.
    private static readonly string[] CompoundExts = { ".tar.gz", ".tar.bz2", ".tar.xz" };

    private static readonly IReadOnlySet<string> ExtractableExts =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".zip", ".tar", ".gz", ".tgz", ".tbz2", ".txz",
            ".bz2", ".xz", ".7z", ".rar",
        };

    /// <summary>True if the path looks like an archive Zephyr can extract.</summary>
    public static bool CanExtract(string path)
    {
        var lower = path.ToLowerInvariant();
        foreach (var c in CompoundExts) if (lower.EndsWith(c)) return true;
        return ExtractableExts.Contains(Path.GetExtension(path));
    }

    /// <summary>True if the archive is password-protected (header- or entry-encrypted).</summary>
    public static bool IsEncrypted(string archivePath)
    {
        try
        {
            using var archive = ArchiveFactory.OpenArchive(archivePath, new ReaderOptions());
            return archive.IsEncrypted || archive.Entries.Any(e => e.IsEncrypted);
        }
        catch
        {
            return true; // can't even read the listing without a password → treat as encrypted
        }
    }

    /// <summary>True if <paramref name="password"/> can decrypt the archive's first file entry.</summary>
    public static bool ValidatePassword(string archivePath, string password)
    {
        try
        {
            using var archive = ArchiveFactory.OpenArchive(archivePath, new ReaderOptions { Password = password });
            var entry = archive.Entries.FirstOrDefault(e => !e.IsDirectory);
            if (entry is null) return true; // nothing encrypted to verify against
            using var s = entry.OpenEntryStream();
            return s.Read(new byte[1], 0, 1) >= 0; // throws on a wrong password
        }
        catch
        {
            return false;
        }
    }

    // ── Browse (read-only, inside an archive) ─────────────────────────────────

    /// <summary>Immediate children of <paramref name="innerPath"/> inside the archive,
    /// synthesizing directory nodes for paths the archive only stores implicitly.</summary>
    public static IReadOnlyList<ArchiveEntryInfo> GetChildren(string archivePath, string innerPath, string? password = null)
    {
        var prefix = string.IsNullOrEmpty(innerPath) ? "" : innerPath.Replace('\\', '/').Trim('/') + "/";
        var dirs   = new Dictionary<string, ArchiveEntryInfo>(StringComparer.OrdinalIgnoreCase);
        var files  = new Dictionary<string, ArchiveEntryInfo>(StringComparer.OrdinalIgnoreCase);

        using var archive = ArchiveFactory.OpenArchive(archivePath, new ReaderOptions { Password = password });
        foreach (var e in archive.Entries)
        {
            if (e.Key is not { } raw) continue;
            var key       = raw.Replace('\\', '/');
            bool isDir    = e.IsDirectory || key.EndsWith('/');
            key = key.TrimEnd('/');
            if (key.Length == 0) continue;
            if (prefix.Length > 0 && !key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;

            var remainder = prefix.Length > 0 ? key[prefix.Length..] : key;
            if (remainder.Length == 0) continue;

            int slash = remainder.IndexOf('/');
            if (slash < 0)
            {
                if (isDir) dirs.TryAdd(remainder, new ArchiveEntryInfo(prefix + remainder, true, 0, When(e)));
                else       files[remainder] = new ArchiveEntryInfo(prefix + remainder, false, e.Size, When(e));
            }
            else
            {
                var name = remainder[..slash];
                dirs.TryAdd(name, new ArchiveEntryInfo(prefix + name, true, 0, DateTime.MinValue));
            }
        }

        foreach (var d in dirs.Keys) files.Remove(d); // a name that's a folder wins over a stray file entry
        return dirs.Values.Concat(files.Values).ToList();

        static DateTime When(IEntry e) => e.LastModifiedTime ?? e.ArchivedTime ?? DateTime.MinValue;
    }

    /// <summary>Extracts a single file entry to a unique temp path and returns it (for opening).</summary>
    public static string ExtractEntryToTemp(string archivePath, string innerPath, string? password = null)
    {
        var norm = innerPath.Replace('\\', '/').Trim('/');
        using var archive = ArchiveFactory.OpenArchive(archivePath, new ReaderOptions { Password = password });
        var entry = archive.Entries.FirstOrDefault(e =>
                        !e.IsDirectory && e.Key is { } k &&
                        k.Replace('\\', '/').TrimEnd('/').Equals(norm, StringComparison.OrdinalIgnoreCase))
                    ?? throw new FileNotFoundException($"Entry not found in archive: {innerPath}");

        var tempDir = Path.Combine(Path.GetTempPath(), "Zephyr_Archive", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        var outPath = Path.Combine(tempDir, Path.GetFileName(norm));
        using (var src = entry.OpenEntryStream())
        using (var dst = File.Create(outPath))
            src.CopyTo(dst, ChunkSize);
        return outPath;
    }

    /// <summary>Extracts the selected entries (files and/or whole folders) to <paramref name="destFolder"/>,
    /// preserving each entry's path relative to <paramref name="baseInner"/> (the folder being browsed).</summary>
    public static Task ExtractEntriesAsync(
        string archivePath, IReadOnlyCollection<string> selectedInner, string baseInner, string destFolder,
        ExtractOptions? options = null, IProgress<ArchiveProgress>? progress = null, CancellationToken ct = default)
    {
        options ??= new ExtractOptions();
        return Task.Run(() =>
        {
            Directory.CreateDirectory(destFolder);
            var basePrefix = string.IsNullOrEmpty(baseInner) ? "" : baseInner.Replace('\\', '/').Trim('/') + "/";
            var selFiles   = new HashSet<string>(selectedInner.Select(s => s.Replace('\\', '/').Trim('/')), StringComparer.OrdinalIgnoreCase);
            var selDirs    = selFiles.Select(s => s + "/").ToList();
            var safeRoot   = Path.GetFullPath(destFolder) + Path.DirectorySeparatorChar;

            using var archive = ArchiveFactory.OpenArchive(archivePath, new ReaderOptions { Password = options.Password });

            // First pass: which entries are in scope, and the total bytes.
            var matches = new List<(IArchiveEntry Entry, string Rel)>();
            long total = 0;
            foreach (var e in archive.Entries)
            {
                ct.ThrowIfCancellationRequested();
                if (e.IsDirectory || e.Key is not { } raw) continue;
                var key = raw.Replace('\\', '/').TrimEnd('/');
                bool inScope = selFiles.Contains(key) || selDirs.Any(d => key.StartsWith(d, StringComparison.OrdinalIgnoreCase));
                if (!inScope) continue;
                var rel = key.StartsWith(basePrefix, StringComparison.OrdinalIgnoreCase) ? key[basePrefix.Length..] : key;
                matches.Add((e, rel));
                total += e.Size;
            }

            var tracker = new ProgressTracker(progress, total);
            foreach (var (entry, rel) in matches)
            {
                ct.ThrowIfCancellationRequested();
                var fullDest = Path.GetFullPath(Path.Combine(destFolder, rel.Replace('/', Path.DirectorySeparatorChar)));
                if (!fullDest.StartsWith(safeRoot, StringComparison.OrdinalIgnoreCase)) continue; // zip-slip guard
                if (File.Exists(fullDest) && !options.Overwrite) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(fullDest)!);
                tracker.Current = rel;
                using var src = entry.OpenEntryStream();
                using var dst = File.Create(fullDest);
                CopyWithProgress(src, dst, tracker, ct);
            }
        }, ct);
    }

    // ── Create ──────────────────────────────────────────────────────────────

    public static Task CreateAsync(
        string dest, IEnumerable<string> sources, CompressOptions? options = null,
        IProgress<ArchiveProgress>? progress = null, CancellationToken ct = default)
    {
        options ??= new CompressOptions();
        var srcList = sources.ToList();
        return Task.Run(() =>
        {
            var tracker = new ProgressTracker(progress, SumSourceSize(srcList));
            try
            {
                switch (options.Format)
                {
                    case WriteFormat.Zip when !string.IsNullOrEmpty(options.Password):
                                            CreateEncryptedZip(dest, srcList, options, tracker, ct);          break;
                    case WriteFormat.Zip:   CreateZip(dest, srcList, options.Level, tracker, ct);              break;
                    case WriteFormat.Tar:   CreateTar(dest, srcList, compress: false, options.Level, tracker, ct); break;
                    case WriteFormat.TarGz: CreateTar(dest, srcList, compress: true,  options.Level, tracker, ct); break;
                    case WriteFormat.Gz:    CreateGz(dest, srcList, options.Level, tracker, ct);               break;
                }
            }
            catch
            {
                // Never leave a half-written archive behind on cancel/failure.
                try { if (File.Exists(dest)) File.Delete(dest); } catch { }
                throw;
            }
        }, ct);
    }

    private static void CreateZip(string dest, List<string> sources, Level level, ProgressTracker tracker, CancellationToken ct)
    {
        var zipLevel = ToZipLevel(level);
        using var archive = ZipFile.Open(dest, ZipArchiveMode.Create);
        foreach (var src in sources)
        {
            ct.ThrowIfCancellationRequested();
            if      (Directory.Exists(src)) AddZipDirectory(archive, src, Path.GetFileName(src), zipLevel, tracker, ct);
            else if (File.Exists(src))      AddZipFile(archive, src, Path.GetFileName(src), zipLevel, tracker, ct);
        }
    }

    private static void AddZipFile(ZipArchive archive, string file, string entryName, NetCompressionLevel level, ProgressTracker tracker, CancellationToken ct, bool replaceExisting = false)
    {
        var name = entryName.Replace('\\', '/');
        if (replaceExisting) archive.GetEntry(name)?.Delete(); // avoid duplicate entries when appending
        var entry = archive.CreateEntry(name, level);
        try { entry.LastWriteTime = File.GetLastWriteTime(file); } catch { }
        tracker.Current = name;
        using var input       = File.OpenRead(file);
        using var entryStream = entry.Open();
        CopyWithProgress(input, entryStream, tracker, ct);
    }

    private static void AddZipDirectory(ZipArchive archive, string dir, string prefix, NetCompressionLevel level, ProgressTracker tracker, CancellationToken ct, bool replaceExisting = false)
    {
        // Preserve empty directories with an explicit entry.
        if (!Directory.EnumerateFileSystemEntries(dir).Any())
        {
            if (replaceExisting) archive.GetEntry(prefix.Replace('\\', '/') + "/")?.Delete();
            archive.CreateEntry(prefix.Replace('\\', '/') + "/");
            return;
        }
        foreach (var file in Directory.EnumerateFiles(dir))
        {
            ct.ThrowIfCancellationRequested();
            AddZipFile(archive, file, Path.Combine(prefix, Path.GetFileName(file)), level, tracker, ct, replaceExisting);
        }
        foreach (var sub in Directory.EnumerateDirectories(dir))
        {
            ct.ThrowIfCancellationRequested();
            AddZipDirectory(archive, sub, Path.Combine(prefix, Path.GetFileName(sub)), level, tracker, ct, replaceExisting);
        }
    }

    /// <summary>Adds files/folders to an existing .zip (entries with the same name are replaced).</summary>
    public static Task AppendToZipAsync(
        string zipPath, IEnumerable<string> sources, Level level = Level.Normal,
        IProgress<ArchiveProgress>? progress = null, CancellationToken ct = default)
    {
        var srcList = sources.ToList();
        return Task.Run(() =>
        {
            var tracker  = new ProgressTracker(progress, SumSourceSize(srcList));
            var zipLevel = ToZipLevel(level);
            using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Update);
            foreach (var src in srcList)
            {
                ct.ThrowIfCancellationRequested();
                if      (Directory.Exists(src)) AddZipDirectory(archive, src, Path.GetFileName(src), zipLevel, tracker, ct, replaceExisting: true);
                else if (File.Exists(src))      AddZipFile(archive, src, Path.GetFileName(src), zipLevel, tracker, ct, replaceExisting: true);
            }
        }, ct);
    }

    // ── Encrypted zip (SharpZipLib — WinZip AES-256 or legacy ZipCrypto) ──────

    private static void CreateEncryptedZip(string dest, List<string> sources, CompressOptions options, ProgressTracker tracker, CancellationToken ct)
    {
        int aesKeySize = options.Encryption == ZipEncryption.Aes256 ? 256 : 0; // 0 = traditional ZipCrypto
        using var fileStream = File.Create(dest);
        using var zip = new SharpZip.ZipOutputStream(fileStream) { Password = options.Password, IsStreamOwner = false };
        zip.SetLevel(ToDeflateLevel(options.Level));
        foreach (var src in sources)
        {
            ct.ThrowIfCancellationRequested();
            if      (Directory.Exists(src)) AddEncryptedDirectory(zip, src, Path.GetFileName(src), aesKeySize, tracker, ct);
            else if (File.Exists(src))      AddEncryptedFile(zip, src, Path.GetFileName(src), aesKeySize, tracker, ct);
        }
        zip.Finish();
    }

    private static void AddEncryptedFile(SharpZip.ZipOutputStream zip, string file, string entryName, int aesKeySize, ProgressTracker tracker, CancellationToken ct)
    {
        var fi = new FileInfo(file);
        var entry = new SharpZip.ZipEntry(SharpZip.ZipEntry.CleanName(entryName))
        {
            DateTime   = fi.LastWriteTime,
            Size       = fi.Length,
            AESKeySize = aesKeySize,
        };
        tracker.Current = entry.Name;
        zip.PutNextEntry(entry);
        using (var input = File.OpenRead(file))
            CopyWithProgress(input, zip, tracker, ct);
        zip.CloseEntry();
    }

    private static void AddEncryptedDirectory(SharpZip.ZipOutputStream zip, string dir, string prefix, int aesKeySize, ProgressTracker tracker, CancellationToken ct)
    {
        if (!Directory.EnumerateFileSystemEntries(dir).Any())
        {
            zip.PutNextEntry(new SharpZip.ZipEntry(SharpZip.ZipEntry.CleanName(prefix) + "/"));
            zip.CloseEntry();
            return;
        }
        foreach (var file in Directory.EnumerateFiles(dir))
        {
            ct.ThrowIfCancellationRequested();
            AddEncryptedFile(zip, file, Path.Combine(prefix, Path.GetFileName(file)), aesKeySize, tracker, ct);
        }
        foreach (var sub in Directory.EnumerateDirectories(dir))
        {
            ct.ThrowIfCancellationRequested();
            AddEncryptedDirectory(zip, sub, Path.Combine(prefix, Path.GetFileName(sub)), aesKeySize, tracker, ct);
        }
    }

    private static void CreateTar(string dest, List<string> sources, bool compress, Level level, ProgressTracker tracker, CancellationToken ct)
    {
        using var fileStream = File.Create(dest);
        Stream tarTarget = compress
            ? new GZipStream(fileStream, ToGZipLevel(level), leaveOpen: false)
            : fileStream;
        try
        {
            using var writer = new TarWriter(tarTarget, TarEntryFormat.Pax, leaveOpen: false);
            foreach (var src in sources)
            {
                ct.ThrowIfCancellationRequested();
                if      (Directory.Exists(src)) AddTarDirectory(writer, src, Path.GetFileName(src), tracker, ct);
                else if (File.Exists(src))      AddTarFile(writer, src, Path.GetFileName(src), tracker, ct);
            }
        }
        finally
        {
            if (compress) tarTarget.Dispose();
        }
    }

    private static void AddTarFile(TarWriter writer, string file, string entryName, ProgressTracker tracker, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var name = entryName.Replace('\\', '/');
        tracker.Current = name;
        // TarWriter requires a seekable DataStream when the archive stream is unseekable
        // (tar.gz), so we can't intercept with CountingStream here — report per file instead.
        using var input = File.OpenRead(file);
        var entry = new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = input };
        try { entry.ModificationTime = File.GetLastWriteTimeUtc(file); } catch { }
        writer.WriteEntry(entry);
        tracker.Advance(input.Length);
    }

    private static void AddTarDirectory(TarWriter writer, string dir, string prefix, ProgressTracker tracker, CancellationToken ct)
    {
        writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, prefix.Replace('\\', '/').TrimEnd('/') + "/"));
        foreach (var file in Directory.EnumerateFiles(dir))
        {
            ct.ThrowIfCancellationRequested();
            AddTarFile(writer, file, Path.Combine(prefix, Path.GetFileName(file)), tracker, ct);
        }
        foreach (var sub in Directory.EnumerateDirectories(dir))
        {
            ct.ThrowIfCancellationRequested();
            AddTarDirectory(writer, sub, Path.Combine(prefix, Path.GetFileName(sub)), tracker, ct);
        }
    }

    private static void CreateGz(string dest, List<string> sources, Level level, ProgressTracker tracker, CancellationToken ct)
    {
        // .gz holds a single stream; only the first file is compressed.
        var file = sources.FirstOrDefault(File.Exists)
                   ?? throw new InvalidOperationException("Gzip (.gz) can only compress a single file.");
        tracker.Current = Path.GetFileName(file);
        using var input  = File.OpenRead(file);
        using var output = File.Create(dest);
        using var gz     = new GZipStream(output, ToGZipLevel(level), leaveOpen: false);
        CopyWithProgress(input, gz, tracker, ct);
    }

    // ── Extract ─────────────────────────────────────────────────────────────

    public static Task ExtractAsync(
        string archivePath, string destFolder, ExtractOptions? options = null,
        IProgress<ArchiveProgress>? progress = null, CancellationToken ct = default)
    {
        options ??= new ExtractOptions();
        return Task.Run(() =>
        {
            Directory.CreateDirectory(destFolder);
            var lower = archivePath.ToLowerInvariant();

            if (lower.EndsWith(".zip"))
            {
                // The BCL zip reader can't decrypt — route password-protected zips via SharpCompress.
                if (string.IsNullOrEmpty(options.Password))
                    ExtractZipNative(archivePath, destFolder, options.Overwrite, progress, ct);
                else
                    ExtractWithSharpCompress(archivePath, destFolder, options, progress, ct);
            }
            else if (lower.EndsWith(".tar"))
                ExtractTarStream(archivePath, destFolder, compressed: false, options.Overwrite, progress, ct);
            else if (lower.EndsWith(".tar.gz") || lower.EndsWith(".tgz"))
                ExtractTarStream(archivePath, destFolder, compressed: true, options.Overwrite, progress, ct);
            else if (lower.EndsWith(".gz"))
                ExtractGzNative(archivePath, destFolder, options.Overwrite, progress, ct);
            else
                ExtractWithSharpCompress(archivePath, destFolder, options, progress, ct);
        }, ct);
    }

    private static void ExtractZipNative(string zipPath, string destFolder, bool overwrite, IProgress<ArchiveProgress>? progress, CancellationToken ct)
    {
        var safeRoot = Path.GetFullPath(destFolder) + Path.DirectorySeparatorChar;
        using var archive = ZipFile.OpenRead(zipPath);
        long total = 0;
        foreach (var e in archive.Entries) total += e.Length;
        var tracker = new ProgressTracker(progress, total);

        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var fullDest = Path.GetFullPath(Path.Combine(destFolder, entry.FullName));
            if (!fullDest.StartsWith(safeRoot, StringComparison.OrdinalIgnoreCase)) continue; // zip-slip guard
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                Directory.CreateDirectory(fullDest);
                continue;
            }
            if (File.Exists(fullDest) && !overwrite) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(fullDest)!);
            tracker.Current = entry.FullName;
            using (var src = entry.Open())
            using (var dst = File.Create(fullDest))
                CopyWithProgress(src, dst, tracker, ct);
            try { File.SetLastWriteTime(fullDest, entry.LastWriteTime.LocalDateTime); } catch { }
        }
    }

    private static void ExtractTarStream(string archivePath, string destFolder, bool compressed, bool overwrite, IProgress<ArchiveProgress>? progress, CancellationToken ct)
    {
        var safeRoot = Path.GetFullPath(destFolder) + Path.DirectorySeparatorChar;
        // Progress is measured by bytes consumed from the archive file (known up front).
        var tracker = new ProgressTracker(progress, SafeLen(archivePath));
        using var fileStream = File.OpenRead(archivePath);
        using var counting   = new CountingStream(fileStream, tracker.Advance);
        Stream tarSource = compressed ? new GZipStream(counting, CompressionMode.Decompress) : counting;
        try
        {
            using var reader = new TarReader(tarSource, leaveOpen: true);
            while (reader.GetNextEntry() is { } entry)
            {
                ct.ThrowIfCancellationRequested();
                var fullDest = Path.GetFullPath(Path.Combine(destFolder, entry.Name.Replace('/', Path.DirectorySeparatorChar)));
                if (!fullDest.StartsWith(safeRoot, StringComparison.OrdinalIgnoreCase)) continue;
                tracker.Current = entry.Name;
                if (entry.EntryType is TarEntryType.Directory)
                {
                    Directory.CreateDirectory(fullDest);
                    continue;
                }
                if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile)) continue;
                if (File.Exists(fullDest) && !overwrite) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(fullDest)!);
                entry.ExtractToFile(fullDest, overwrite); // reads DataStream → counting advances progress
            }
        }
        finally
        {
            if (compressed) tarSource.Dispose();
        }
    }

    private static void ExtractGzNative(string archivePath, string destFolder, bool overwrite, IProgress<ArchiveProgress>? progress, CancellationToken ct)
    {
        var innerName = Path.GetFileNameWithoutExtension(archivePath); // strips trailing .gz
        var outPath   = Path.Combine(destFolder, innerName);
        if (File.Exists(outPath) && !overwrite) return;
        var tracker = new ProgressTracker(progress, SafeLen(archivePath)) { Current = innerName };
        using var fileStream = File.OpenRead(archivePath);
        using var counting   = new CountingStream(fileStream, tracker.Advance);
        using var gz         = new GZipStream(counting, CompressionMode.Decompress);
        using var output     = File.Create(outPath);
        var buffer = new byte[ChunkSize];
        int read;
        while ((read = gz.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            output.Write(buffer, 0, read); // progress advances via counting as gz pulls compressed bytes
        }
    }

    private static void ExtractWithSharpCompress(string archivePath, string destFolder, ExtractOptions options, IProgress<ArchiveProgress>? progress, CancellationToken ct)
    {
        using var archive = ArchiveFactory.OpenArchive(archivePath, new ReaderOptions { Password = options.Password });
        long total = archive.TotalUncompressedSize > 0 ? archive.TotalUncompressedSize : SafeLen(archivePath);
        var tracker = new ProgressTracker(progress, total);
        var safeRoot = Path.GetFullPath(destFolder) + Path.DirectorySeparatorChar;
        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (entry.IsDirectory || entry.Key is not { } key) continue;

            // Zip-slip guard: resolve the target and ensure it stays under destFolder.
            var fullDest = Path.GetFullPath(Path.Combine(destFolder, key.Replace('/', Path.DirectorySeparatorChar)));
            if (!fullDest.StartsWith(safeRoot, StringComparison.OrdinalIgnoreCase)) continue;
            if (File.Exists(fullDest) && !options.Overwrite) continue;

            Directory.CreateDirectory(Path.GetDirectoryName(fullDest)!);
            tracker.Current = key;
            using var src = entry.OpenEntryStream();
            using var dst = File.Create(fullDest);
            CopyWithProgress(src, dst, tracker, ct);
        }
    }

    // ── Test ────────────────────────────────────────────────────────────────

    /// <summary>Outcome of an integrity test: how many file entries were checked and which failed.</summary>
    public sealed record TestResult(int Total, IReadOnlyList<string> FailedEntries)
    {
        public int  Failed => FailedEntries.Count;
        public int  Passed => Total - Failed;
        public bool AllOk  => FailedEntries.Count == 0;
    }

    /// <summary>Verifies archive integrity by reading every entry, continuing past failures so
    /// the full list of corrupt entries is reported rather than just the first.</summary>
    public static Task<TestResult> TestAsync(string archivePath, string? password = null, CancellationToken ct = default)
        => Task.Run(() =>
        {
            using var archive = ArchiveFactory.OpenArchive(archivePath, new ReaderOptions { Password = password });
            var buffer = new byte[ChunkSize];
            int total = 0;
            var failed = new List<string>();
            foreach (var entry in archive.Entries)
            {
                ct.ThrowIfCancellationRequested();
                if (entry.IsDirectory) continue;
                total++;
                try
                {
                    using var stream = entry.OpenEntryStream();
                    while (stream.Read(buffer, 0, buffer.Length) > 0) ct.ThrowIfCancellationRequested();
                }
                catch (OperationCanceledException) { throw; }
                catch { failed.Add(entry.Key ?? "(unnamed entry)"); }
            }
            return new TestResult(total, failed);
        }, ct);

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static long SumSourceSize(IEnumerable<string> sources)
    {
        long total = 0;
        foreach (var s in sources)
        {
            if (Directory.Exists(s))
                foreach (var f in Directory.EnumerateFiles(s, "*", SearchOption.AllDirectories))
                    total += SafeLen(f);
            else if (File.Exists(s))
                total += SafeLen(s);
        }
        return total;
    }

    private static long SafeLen(string path)
    {
        try { return new FileInfo(path).Length; } catch { return 0; }
    }

    private static void CopyWithProgress(Stream src, Stream dst, ProgressTracker tracker, CancellationToken ct)
    {
        var buffer = new byte[ChunkSize];
        int read;
        while ((read = src.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            dst.Write(buffer, 0, read);
            tracker.Advance(read);
        }
    }

    /// <summary>Accumulates processed bytes and pushes snapshots to the caller's IProgress.</summary>
    private sealed class ProgressTracker(IProgress<ArchiveProgress>? sink, long total)
    {
        private long _processed;
        public string Current = "";

        public void Advance(long delta)
        {
            _processed += delta;
            sink?.Report(new ArchiveProgress(_processed, total, Current));
        }
    }

    /// <summary>Read-through stream that reports each chunk's byte count as it passes.</summary>
    private sealed class CountingStream(Stream inner, Action<long> onRead) : Stream
    {
        public override bool CanRead  => true;
        public override bool CanSeek  => false;
        public override bool CanWrite => false;
        public override long Length   => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = inner.Read(buffer, offset, count);
            if (n > 0) onRead(n);
            return n;
        }

        public override int Read(Span<byte> buffer)
        {
            int n = inner.Read(buffer);
            if (n > 0) onRead(n);
            return n;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }

    // ── Level mapping ─────────────────────────────────────────────────────────

    private static NetCompressionLevel ToZipLevel(Level level) => level switch
    {
        Level.Store    => NetCompressionLevel.NoCompression,
        Level.Fastest  => NetCompressionLevel.Fastest,
        Level.Normal   => NetCompressionLevel.Optimal,
        Level.Maximum  => NetCompressionLevel.SmallestSize,
        _              => NetCompressionLevel.Optimal,
    };

    private static NetCompressionLevel ToGZipLevel(Level level) => level switch
    {
        Level.Store    => NetCompressionLevel.NoCompression,
        Level.Fastest  => NetCompressionLevel.Fastest,
        Level.Normal   => NetCompressionLevel.Optimal,
        Level.Maximum  => NetCompressionLevel.SmallestSize,
        _              => NetCompressionLevel.Optimal,
    };

    // SharpZipLib uses a 0–9 Deflate level (encrypted-zip path).
    private static int ToDeflateLevel(Level level) => level switch
    {
        Level.Store   => 0,
        Level.Fastest => 1,
        Level.Normal  => 6,
        Level.Maximum => 9,
        _             => 6,
    };
}
