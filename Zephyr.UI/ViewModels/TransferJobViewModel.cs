using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Zephyr.Core.FileSystem;

namespace Zephyr.UI.ViewModels;

public enum TransferState { Queued, Running, Paused, Completed, Failed, Canceled }

/// <summary>
/// A single copy/move job shown in the transfer panel. Owns its cancellation and pause
/// tokens, tracks live throughput, and exposes commands for pause/resume, cancel and dismiss.
/// </summary>
public partial class TransferJobViewModel : ObservableObject
{
    public TransferOperation                        Operation { get; }
    public IReadOnlyList<string>                    Sources   { get; }
    public string                                   DestFolder { get; }
    public FileOperationsService.ConflictResolution Conflict   { get; }

    internal readonly CancellationTokenSource Cts   = new();
    internal readonly PauseTokenSource        Pause = new();

    /// <summary>Raised (on the UI thread) when the job should be removed from the panel.</summary>
    public event Action? RemoveRequested;

    public TransferJobViewModel(TransferOperation op, IReadOnlyList<string> sources,
        string destFolder, FileOperationsService.ConflictResolution conflict)
    {
        Operation  = op;
        Sources    = sources;
        DestFolder = destFolder;
        Conflict   = conflict;

        var verb = op == TransferOperation.Move ? "Moving" : "Copying";
        var what = sources.Count == 1 ? Path.GetFileName(sources[0].TrimEnd('\\', '/')) : $"{sources.Count} items";
        var dest = Path.GetFileName(destFolder.TrimEnd('\\', '/'));
        if (string.IsNullOrEmpty(dest)) dest = destFolder;
        Title = $"{verb} {what} to {dest}";
    }

    // ── Observable state ────────────────────────────────────────────────────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsActive), nameof(IsFinished), nameof(CanControl),
        nameof(PauseGlyph), nameof(PauseTooltip), nameof(StatusGlyph))]
    private TransferState _state = TransferState.Queued;

    [ObservableProperty] private string _title;
    [ObservableProperty] private string _currentFile = "";
    [ObservableProperty] private double _percent;            // 0..100
    [ObservableProperty] private string _detailText = "Queued";

    public bool IsActive   => State is TransferState.Queued or TransferState.Running or TransferState.Paused;
    public bool IsFinished => !IsActive;
    public bool CanControl => State is TransferState.Running or TransferState.Paused;

    // Segoe Fluent Icons: Play (E768) / Pause (E769).
    public string PauseGlyph   => State == TransferState.Paused ? "" : "";
    public string PauseTooltip => State == TransferState.Paused ? "Resume" : "Pause";

    public string StatusGlyph => State switch
    {
        TransferState.Completed => "", // CheckMark
        TransferState.Failed    => "", // Warning
        TransferState.Canceled  => "", // Cancel
        _                       => "",
    };

    // ── Throughput tracking (all updated on the UI thread) ───────────────────
    private readonly Stopwatch _sw = new();
    private long   _lastBytes;
    private double _lastTime;
    private double _speed; // smoothed bytes/sec

    public void ApplyProgress(TransferProgress p)
    {
        if (!IsActive) return;
        CurrentFile = p.CurrentFile;

        if (p.TotalBytes > 0)
            Percent = 100.0 * p.BytesCompleted / p.TotalBytes;
        else if (p.TotalFiles > 0)
            Percent = 100.0 * p.FilesCompleted / p.TotalFiles;

        double now = _sw.Elapsed.TotalSeconds;
        double dt  = now - _lastTime;
        if (dt >= 0.4)
        {
            double inst = (p.BytesCompleted - _lastBytes) / dt;
            _speed     = _speed <= 0 ? inst : _speed * 0.6 + inst * 0.4;
            _lastBytes = p.BytesCompleted;
            _lastTime  = now;
        }

        var done  = FormatBytes(p.BytesCompleted);
        var total = p.TotalBytes > 0 ? FormatBytes(p.TotalBytes) : "—";
        var parts = new List<string> { $"{done} / {total}" };
        if (_speed > 1)
        {
            parts.Add($"{FormatBytes((long)_speed)}/s");
            if (p.TotalBytes > p.BytesCompleted)
                parts.Add($"{FormatTime((p.TotalBytes - p.BytesCompleted) / _speed)} left");
        }
        DetailText = string.Join("  ·  ", parts);
    }

    // ── State transitions (called by TransferManager on the UI thread) ───────
    public void MarkRunning()
    {
        State      = TransferState.Running;
        DetailText = "Preparing…";
        _sw.Restart();
        _lastTime = 0;
    }

    public void MarkCompleted()
    {
        State      = TransferState.Completed;
        Percent    = 100;
        DetailText = "Completed";
        // Auto-dismiss successful jobs after a short delay so the panel stays tidy.
        ScheduleDismiss(TimeSpan.FromSeconds(4));
    }

    public void MarkCanceled()
    {
        State      = TransferState.Canceled;
        DetailText = "Canceled";
    }

    public void MarkFailed(string message)
    {
        State      = TransferState.Failed;
        DetailText = message;
    }

    private void ScheduleDismiss(TimeSpan after)
    {
        var timer = new DispatcherTimer { Interval = after };
        timer.Tick += (_, _) => { timer.Stop(); RemoveRequested?.Invoke(); };
        timer.Start();
    }

    // ── Commands ─────────────────────────────────────────────────────────────
    [RelayCommand]
    private void PauseResume()
    {
        if (State == TransferState.Running) { Pause.Pause();  State = TransferState.Paused;  DetailText = "Paused"; }
        else if (State == TransferState.Paused) { Pause.Resume(); State = TransferState.Running; }
    }

    [RelayCommand]
    private void Cancel()
    {
        if (IsFinished) return;
        Cts.Cancel();
        Pause.Resume(); // release a paused worker so it can observe cancellation
        if (State == TransferState.Queued) MarkCanceled(); // never started — settle immediately
    }

    [RelayCommand]
    private void Dismiss() => RemoveRequested?.Invoke();

    // ── Formatting helpers ───────────────────────────────────────────────────
    private static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double size = bytes;
        int u = 0;
        while (size >= 1024 && u < units.Length - 1) { size /= 1024; u++; }
        return u == 0 ? $"{bytes} B" : $"{size:0.#} {units[u]}";
    }

    private static string FormatTime(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0) return "…";
        if (seconds < 1)  return "less than 1s";
        if (seconds < 60) return $"{Math.Ceiling(seconds):0}s";
        if (seconds < 3600)
        {
            int m = (int)(seconds / 60), s = (int)(seconds % 60);
            return s > 0 ? $"{m}m {s}s" : $"{m}m";
        }
        int h = (int)(seconds / 3600), min = (int)(seconds % 3600 / 60);
        return min > 0 ? $"{h}h {min}m" : $"{h}h";
    }
}
