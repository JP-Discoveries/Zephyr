namespace Zephyr.Core.FileSystem;

/// <summary>Whether a transfer copies its sources or moves (cut) them.</summary>
public enum TransferOperation { Copy, Move }

/// <summary>
/// A cooperative pause primitive. The transfer worker calls <see cref="Wait"/> at safe
/// points; it blocks while paused and returns immediately otherwise. Honors cancellation.
/// </summary>
public sealed class PauseTokenSource
{
    private readonly ManualResetEventSlim _gate = new(initialState: true);

    public bool IsPaused => !_gate.IsSet;

    public void Pause()  => _gate.Reset();
    public void Resume() => _gate.Set();

    /// <summary>Blocks while paused; returns immediately when running. Throws if cancelled.</summary>
    public void Wait(CancellationToken ct) => _gate.Wait(ct);
}

/// <summary>Snapshot of transfer progress, reported periodically from the worker thread.</summary>
public readonly record struct TransferProgress(
    string CurrentFile,
    long   BytesCompleted,
    long   TotalBytes,
    int    FilesCompleted,
    int    TotalFiles);

/// <summary>Result of a completed transfer, used for tab refresh and undo.</summary>
public sealed class TransferOutcome
{
    /// <summary>Top-level destination paths that were created (copy undo / selection).</summary>
    public List<string> CreatedRoots { get; } = [];

    /// <summary>(source, destination) pairs for each top-level item (move undo).</summary>
    public List<(string Src, string Dest)> RootPairs { get; } = [];
}
