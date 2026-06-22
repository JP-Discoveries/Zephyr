using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Zephyr.Core.FileSystem;

public class FileOperationsService
{
    // ── Recycle bin via SHFileOperation ──────────────────────────────────────
    [StructLayout(LayoutKind.Sequential)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint   wFunc;
        public IntPtr pFrom;
        public IntPtr pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public IntPtr lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

    private const uint   FO_DELETE          = 0x0003;
    private const ushort FOF_ALLOWUNDO      = 0x0040;
    private const ushort FOF_NOCONFIRMATION = 0x0010;

    // ── New Folder ────────────────────────────────────────────────────────────
    public string CreateFolder(string parent, string name = "New Folder")
    {
        var path = Path.Combine(parent, name);
        int n = 2;
        while (Directory.Exists(path) || File.Exists(path))
            path = Path.Combine(parent, $"{name} ({n++})");
        Directory.CreateDirectory(path);
        return path;
    }

    // ── Rename ────────────────────────────────────────────────────────────────
    public void Rename(string path, string newName)
    {
        var parent = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("Cannot rename a root path.");
        var dest = Path.Combine(parent, newName);
        if (Directory.Exists(path)) Directory.Move(path, dest);
        else                        File.Move(path, dest);
    }

    // ── Delete ────────────────────────────────────────────────────────────────
    public void Delete(IEnumerable<string> paths, bool permanent = false, IntPtr hwnd = default)
    {
        var list = paths.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
        if (list.Count == 0) return;

        if (permanent)
        {
            foreach (var p in list)
            {
                if (Directory.Exists(p)) Directory.Delete(p, recursive: true);
                else                     File.Delete(p);
            }
            return;
        }

        // Build double-null-terminated multi-string for SHFileOperation
        var sb = new StringBuilder();
        foreach (var p in list) { sb.Append(p); sb.Append('\0'); }
        sb.Append('\0');
        var str = sb.ToString();
        var ptr = Marshal.AllocHGlobal(str.Length * 2);
        try
        {
            for (int i = 0; i < str.Length; i++)
                Marshal.WriteInt16(ptr, i * 2, (short)str[i]);
            var op = new SHFILEOPSTRUCT
            {
                hwnd   = hwnd,
                wFunc  = FO_DELETE,
                pFrom  = ptr,
                fFlags = (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMATION)
            };
            SHFileOperation(ref op);
        }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    // ── Copy ──────────────────────────────────────────────────────────────────
    public enum ConflictResolution { Skip, Replace, KeepBoth }

    // Returns the actual destination paths that were created.
    public async Task<List<string>> CopyAsync(IEnumerable<string> sources, string destFolder,
        ConflictResolution conflict = ConflictResolution.KeepBoth, CancellationToken ct = default)
    {
        var list    = sources.ToList();
        var results = new List<string>();
        await Task.Run(() =>
        {
            foreach (var src in list)
            {
                ct.ThrowIfCancellationRequested();
                if (Directory.Exists(src))
                {
                    var dest = Path.Combine(destFolder, Path.GetFileName(src));
                    CopyDir(src, dest, conflict, ct);
                    results.Add(dest);
                }
                else if (File.Exists(src))
                {
                    var dest = CopyFile(src, destFolder, conflict);
                    if (dest != null) results.Add(dest);
                }
            }
        }, ct);
        return results;
    }

    // ── Transfer engine (progress + pause + cancel) ────────────────────────────
    // Buffer sized for good throughput on large files without excessive memory.
    private const int TransferBufferSize = 1024 * 1024; // 1 MB

    private sealed class TransferPlan
    {
        public readonly List<string> Dirs = [];                          // dirs to create, parents first
        public readonly List<(string Src, string Dest, bool Overwrite)> Files = [];
        public readonly List<(string Src, string Dest)> Roots = [];      // top-level (src, dest) pairs
        public long TotalBytes;
        public int  TotalFiles;
    }

    /// <summary>
    /// Copies or moves <paramref name="sources"/> into <paramref name="destFolder"/> with live
    /// byte-level progress, cooperative pause, and cancellation. Same-volume moves use the fast
    /// rename path; copies and cross-volume moves are streamed so progress can be reported.
    /// </summary>
    public Task<TransferOutcome> RunTransferAsync(
        TransferOperation op,
        IReadOnlyList<string> sources,
        string destFolder,
        ConflictResolution conflict,
        PauseTokenSource pause,
        IProgress<TransferProgress> progress,
        CancellationToken ct)
    {
        return Task.Run(() =>
        {
            // Fast path: a move where every source already lives on the destination volume and
            // no directory merge is required — Directory.Move / File.Move are effectively instant.
            if (op == TransferOperation.Move && CanFastMove(sources, destFolder, conflict, ct))
                return FastMove(sources, destFolder, conflict, pause, progress, ct);

            var plan = BuildPlan(sources, destFolder, conflict, ct);
            return ExecutePlan(op, plan, pause, progress, ct);
        }, ct);
    }

    private static bool CanFastMove(IReadOnlyList<string> sources, string destFolder,
        ConflictResolution conflict, CancellationToken ct)
    {
        foreach (var src in sources)
        {
            ct.ThrowIfCancellationRequested();
            if (!SameVolume(src, destFolder)) return false;
            // A directory whose resolved destination already exists needs a merge copy, not a rename.
            if (Directory.Exists(src))
            {
                var dest = ResolveDest(src, destFolder, conflict);
                if (dest != null && Directory.Exists(dest)) return false;
            }
        }
        return true;
    }

    private static TransferOutcome FastMove(IReadOnlyList<string> sources, string destFolder,
        ConflictResolution conflict, PauseTokenSource pause, IProgress<TransferProgress> progress,
        CancellationToken ct)
    {
        var outcome = new TransferOutcome();
        int total = sources.Count, done = 0;
        foreach (var src in sources)
        {
            ct.ThrowIfCancellationRequested();
            pause.Wait(ct);
            var dest = ResolveDest(src, destFolder, conflict);
            if (dest == null) { done++; continue; } // skipped
            if (Directory.Exists(src)) Directory.Move(src, dest);
            else                       File.Move(src, dest, overwrite: conflict == ConflictResolution.Replace);
            outcome.CreatedRoots.Add(dest);
            outcome.RootPairs.Add((src, dest));
            progress.Report(new TransferProgress(Path.GetFileName(src), ++done, total, done, total));
        }
        return outcome;
    }

    private static TransferPlan BuildPlan(IReadOnlyList<string> sources, string destFolder,
        ConflictResolution conflict, CancellationToken ct)
    {
        var plan = new TransferPlan();
        foreach (var src in sources)
        {
            ct.ThrowIfCancellationRequested();
            var destRoot = ResolveDest(src, destFolder, conflict);
            if (destRoot == null) continue; // skipped by conflict policy
            if (Directory.Exists(src))
            {
                AddDirToPlan(src, destRoot, conflict, plan, ct);
                plan.Roots.Add((src, destRoot));
            }
            else if (File.Exists(src))
            {
                plan.Files.Add((src, destRoot, conflict == ConflictResolution.Replace));
                plan.TotalBytes += SafeLength(src);
                plan.TotalFiles++;
                plan.Roots.Add((src, destRoot));
            }
        }
        return plan;
    }

    private static void AddDirToPlan(string srcDir, string destDir, ConflictResolution conflict,
        TransferPlan plan, CancellationToken ct)
    {
        plan.Dirs.Add(destDir);
        foreach (var file in Directory.EnumerateFiles(srcDir))
        {
            ct.ThrowIfCancellationRequested();
            plan.Files.Add((file, Path.Combine(destDir, Path.GetFileName(file)),
                conflict == ConflictResolution.Replace));
            plan.TotalBytes += SafeLength(file);
            plan.TotalFiles++;
        }
        foreach (var sub in Directory.EnumerateDirectories(srcDir))
        {
            ct.ThrowIfCancellationRequested();
            AddDirToPlan(sub, Path.Combine(destDir, Path.GetFileName(sub)), conflict, plan, ct);
        }
    }

    private static TransferOutcome ExecutePlan(TransferOperation op, TransferPlan plan,
        PauseTokenSource pause, IProgress<TransferProgress> progress, CancellationToken ct)
    {
        foreach (var dir in plan.Dirs) Directory.CreateDirectory(dir);

        long bytesDone = 0;
        int  filesDone = 0;
        var  sw        = System.Diagnostics.Stopwatch.StartNew();
        long lastReport = 0;
        string? partialDest = null; // current file being written, deleted on cancel

        void Report(string name) =>
            progress.Report(new TransferProgress(name, bytesDone, plan.TotalBytes, filesDone, plan.TotalFiles));

        try
        {
            foreach (var (src, dest, overwrite) in plan.Files)
            {
                ct.ThrowIfCancellationRequested();
                pause.Wait(ct);
                var name = Path.GetFileName(src);
                partialDest = dest;
                CopyFileStreamed(src, dest, overwrite, pause, ct, chunk =>
                {
                    bytesDone += chunk;
                    // Throttle UI updates to ~80ms to avoid flooding the dispatcher.
                    if (sw.ElapsedMilliseconds - lastReport >= 80)
                    {
                        lastReport = sw.ElapsedMilliseconds;
                        Report(name);
                    }
                });
                CopyMetadata(src, dest);
                partialDest = null;
                filesDone++;
                Report(name);
            }
        }
        catch (OperationCanceledException)
        {
            // Remove the half-written file so a cancelled transfer leaves no truncated junk.
            if (partialDest != null && File.Exists(partialDest))
                try { File.Delete(partialDest); } catch { /* best-effort */ }
            throw;
        }

        // Final 100% report (covers empty/zero-byte transfers too).
        progress.Report(new TransferProgress("", plan.TotalBytes, plan.TotalBytes,
            plan.TotalFiles, plan.TotalFiles));

        if (op == TransferOperation.Move)
            foreach (var (src, _) in plan.Roots) DeleteSource(src);

        var outcome = new TransferOutcome();
        foreach (var (src, dest) in plan.Roots)
        {
            outcome.CreatedRoots.Add(dest);
            outcome.RootPairs.Add((src, dest));
        }
        return outcome;
    }

    private static void CopyFileStreamed(string src, string dest, bool overwrite,
        PauseTokenSource pause, CancellationToken ct, Action<int> onChunk)
    {
        using var input = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read,
            TransferBufferSize, FileOptions.SequentialScan);
        using var output = new FileStream(dest, overwrite ? FileMode.Create : FileMode.CreateNew,
            FileAccess.Write, FileShare.None, TransferBufferSize, FileOptions.SequentialScan);
        var buffer = new byte[TransferBufferSize];
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            pause.Wait(ct);
            output.Write(buffer, 0, read);
            onChunk(read);
        }
    }

    // Mirrors File.Copy's preservation of timestamps and attributes.
    private static void CopyMetadata(string src, string dest)
    {
        try
        {
            var fi = new FileInfo(src);
            File.SetCreationTimeUtc(dest, fi.CreationTimeUtc);
            File.SetLastWriteTimeUtc(dest, fi.LastWriteTimeUtc);
            File.SetAttributes(dest, fi.Attributes);
        }
        catch { /* metadata is best-effort */ }
    }

    private static void DeleteSource(string path)
    {
        if (Directory.Exists(path))   Directory.Delete(path, recursive: true);
        else if (File.Exists(path))   File.Delete(path);
    }

    private static long SafeLength(string file)
    {
        try { return new FileInfo(file).Length; } catch { return 0; }
    }

    private static bool SameVolume(string a, string b)
    {
        try
        {
            return string.Equals(
                Path.GetPathRoot(Path.GetFullPath(a)),
                Path.GetPathRoot(Path.GetFullPath(b)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    // ── Move ──────────────────────────────────────────────────────────────────
    // Returns (src, actualDest) pairs for undo support.
    public async Task<List<(string Src, string Dest)>> MoveAsync(IEnumerable<string> sources, string destFolder,
        ConflictResolution conflict = ConflictResolution.KeepBoth, CancellationToken ct = default)
    {
        var list    = sources.ToList();
        var results = new List<(string, string)>();
        await Task.Run(() =>
        {
            foreach (var src in list)
            {
                ct.ThrowIfCancellationRequested();
                if (Directory.Exists(src))
                {
                    var dest = Path.Combine(destFolder, Path.GetFileName(src));
                    MoveDir(src, dest, conflict, ct);
                    results.Add((src, dest));
                }
                else if (File.Exists(src))
                {
                    var dest = MoveFile(src, destFolder, conflict);
                    if (dest != null) results.Add((src, dest));
                }
            }
        }, ct);
        return results;
    }

    // ── Private helpers ───────────────────────────────────────────────────────
    private static string? CopyFile(string src, string destFolder, ConflictResolution conflict)
    {
        var dest = ResolveDest(src, destFolder, conflict);
        if (dest != null) File.Copy(src, dest, overwrite: conflict == ConflictResolution.Replace);
        return dest;
    }

    private static void CopyDir(string src, string dest, ConflictResolution conflict, CancellationToken ct)
    {
        Directory.CreateDirectory(dest);
        foreach (var f in Directory.EnumerateFiles(src))       { ct.ThrowIfCancellationRequested(); CopyFile(f, dest, conflict); }
        foreach (var d in Directory.EnumerateDirectories(src)) { ct.ThrowIfCancellationRequested(); CopyDir(d, Path.Combine(dest, Path.GetFileName(d)), conflict, ct); }
    }

    private static string? MoveFile(string src, string destFolder, ConflictResolution conflict)
    {
        var dest = ResolveDest(src, destFolder, conflict);
        if (dest != null) File.Move(src, dest, overwrite: conflict == ConflictResolution.Replace);
        return dest;
    }

    private static void MoveDir(string src, string dest, ConflictResolution conflict, CancellationToken ct)
    {
        if (!Directory.Exists(dest)) { Directory.Move(src, dest); return; }
        CopyDir(src, dest, conflict, ct);
        Directory.Delete(src, recursive: true);
    }

    private static string? ResolveDest(string src, string destFolder, ConflictResolution conflict)
    {
        var name = Path.GetFileName(src);
        var dest = Path.Combine(destFolder, name);
        if (!File.Exists(dest) && !Directory.Exists(dest)) return dest;
        return conflict switch
        {
            ConflictResolution.Skip    => null,
            ConflictResolution.Replace => dest,
            _                          => UniquePath(destFolder, name)
        };
    }

    private static string UniquePath(string folder, string name)
    {
        var ext  = Path.GetExtension(name);
        var stem = Path.GetFileNameWithoutExtension(name);
        int n    = 2;
        string path;
        do { path = Path.Combine(folder, $"{stem} ({n++}){ext}"); }
        while (File.Exists(path) || Directory.Exists(path));
        return path;
    }

    // ── Duplicate (Create Copy in same folder) ────────────────────────────────
    public async Task<List<string>> DuplicateAsync(IEnumerable<string> sources, CancellationToken ct = default)
    {
        var list    = sources.ToList();
        var results = new List<string>();
        await Task.Run(() =>
        {
            foreach (var src in list)
            {
                ct.ThrowIfCancellationRequested();
                var folder = Path.GetDirectoryName(src)!;
                if (Directory.Exists(src))
                {
                    var dest = UniqueCopyPath(folder, Path.GetFileName(src), isFile: false);
                    CopyDir(src, dest, ConflictResolution.KeepBoth, ct);
                    results.Add(dest);
                }
                else if (File.Exists(src))
                {
                    var dest = UniqueCopyPath(folder, Path.GetFileName(src), isFile: true);
                    File.Copy(src, dest);
                    results.Add(dest);
                }
            }
        }, ct);
        return results;
    }

    private static string UniqueCopyPath(string folder, string name, bool isFile)
    {
        var ext       = isFile ? Path.GetExtension(name) : string.Empty;
        var stem      = isFile ? Path.GetFileNameWithoutExtension(name) : name;
        var candidate = Path.Combine(folder, $"{stem} - Copy{ext}");
        if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        int n = 2;
        do { candidate = Path.Combine(folder, $"{stem} - Copy ({n++}){ext}"); }
        while (File.Exists(candidate) || Directory.Exists(candidate));
        return candidate;
    }
}
