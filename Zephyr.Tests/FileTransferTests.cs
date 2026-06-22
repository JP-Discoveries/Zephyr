using System.IO;
using Zephyr.Core.FileSystem;

namespace Zephyr.Tests;

public class FileTransferTests : IDisposable
{
    private readonly string _root;
    private readonly FileOperationsService _ops = new();

    public FileTransferTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ZephyrXfer_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string MakeDir(string name)
    {
        var p = Path.Combine(_root, name);
        Directory.CreateDirectory(p);
        return p;
    }

    private static string MakeFile(string dir, string name, int bytes)
    {
        var p = Path.Combine(dir, name);
        var data = new byte[bytes];
        new Random(bytes).NextBytes(data);
        File.WriteAllBytes(p, data);
        return p;
    }

    private static (PauseTokenSource pause, Progress<TransferProgress> progress) Plumbing(Action<TransferProgress>? onReport = null)
        => (new PauseTokenSource(), new Progress<TransferProgress>(p => onReport?.Invoke(p)));

    [Fact]
    public async Task Copy_File_CopiesContentAndReportsCompletion()
    {
        var src = MakeDir("src");
        var dst = MakeDir("dst");
        var file = MakeFile(src, "a.bin", 3 * 1024 * 1024 + 17); // spans multiple 1 MB chunks
        var last = default(TransferProgress);
        var (pause, progress) = Plumbing(p => last = p);

        var outcome = await _ops.RunTransferAsync(TransferOperation.Copy, new[] { file }, dst,
            FileOperationsService.ConflictResolution.KeepBoth, pause, progress, CancellationToken.None);

        var destFile = Path.Combine(dst, "a.bin");
        Assert.True(File.Exists(destFile));
        Assert.True(File.Exists(file)); // copy leaves the source
        Assert.Equal(File.ReadAllBytes(file), File.ReadAllBytes(destFile));
        Assert.Contains(destFile, outcome.CreatedRoots);
        // Final report is 100%.
        Assert.Equal(last.TotalBytes, last.BytesCompleted);
        Assert.True(last.TotalBytes > 0);
    }

    [Fact]
    public async Task Copy_Directory_CopiesNestedTreeAndCountsAllFiles()
    {
        var src = MakeDir("tree");
        MakeFile(src, "root.txt", 1000);
        var sub = Path.Combine(src, "sub");
        Directory.CreateDirectory(sub);
        MakeFile(sub, "nested.txt", 2000);
        var dst = MakeDir("dst");

        int totalFiles = 0;
        var (pause, progress) = Plumbing(p => { if (p.TotalFiles > totalFiles) totalFiles = p.TotalFiles; });

        await _ops.RunTransferAsync(TransferOperation.Copy, new[] { src }, dst,
            FileOperationsService.ConflictResolution.KeepBoth, pause, progress, CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(dst, "tree", "root.txt")));
        Assert.True(File.Exists(Path.Combine(dst, "tree", "sub", "nested.txt")));
        Assert.Equal(2, totalFiles);
    }

    [Fact]
    public async Task Move_SameVolume_RemovesSourceAndKeepsDest()
    {
        var src = MakeDir("src");
        var dst = MakeDir("dst");
        var file = MakeFile(src, "m.bin", 50_000);
        var (pause, progress) = Plumbing();

        var outcome = await _ops.RunTransferAsync(TransferOperation.Move, new[] { file }, dst,
            FileOperationsService.ConflictResolution.KeepBoth, pause, progress, CancellationToken.None);

        Assert.False(File.Exists(file));                       // source gone
        Assert.True(File.Exists(Path.Combine(dst, "m.bin")));  // dest present
        Assert.Single(outcome.RootPairs);
        Assert.Equal(file, outcome.RootPairs[0].Src);
    }

    [Fact]
    public async Task Copy_KeepBoth_DoesNotOverwriteExisting()
    {
        var src = MakeDir("src");
        var dst = MakeDir("dst");
        MakeFile(src, "dup.txt", 100);
        MakeFile(dst, "dup.txt", 200); // pre-existing different file
        var (pause, progress) = Plumbing();

        await _ops.RunTransferAsync(TransferOperation.Copy,
            new[] { Path.Combine(src, "dup.txt") }, dst,
            FileOperationsService.ConflictResolution.KeepBoth, pause, progress, CancellationToken.None);

        Assert.Equal(200, new FileInfo(Path.Combine(dst, "dup.txt")).Length); // original untouched
        Assert.True(File.Exists(Path.Combine(dst, "dup (2).txt")));           // copy kept separately
    }

    [Fact]
    public async Task Cancel_StopsTransferAndLeavesSourcesIntact()
    {
        var src = MakeDir("src");
        var dst = MakeDir("dst");
        var files = Enumerable.Range(0, 4)
            .Select(i => MakeFile(src, $"f{i}.bin", 512 * 1024))
            .ToArray();
        using var cts = new CancellationTokenSource();
        var pause = new PauseTokenSource();
        // Synchronous progress (runs on the worker thread) — cancel once the first file finishes.
        IProgress<TransferProgress> progress = new SyncProgress(p =>
        {
            if (p.FilesCompleted >= 1) cts.Cancel();
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _ops.RunTransferAsync(TransferOperation.Copy, files, dst,
                FileOperationsService.ConflictResolution.KeepBoth, pause, progress, cts.Token));

        Assert.All(files, f => Assert.True(File.Exists(f)));           // copy never touches sources
        var copied = Directory.GetFiles(dst).Length;
        Assert.True(copied < files.Length, $"expected partial copy, got {copied}/{files.Length}");
    }

    private sealed class SyncProgress(Action<TransferProgress> cb) : IProgress<TransferProgress>
    {
        public void Report(TransferProgress value) => cb(value);
    }
}
