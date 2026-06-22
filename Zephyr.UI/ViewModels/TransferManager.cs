using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Zephyr.Core.FileSystem;

namespace Zephyr.UI.ViewModels;

/// <summary>
/// Owns the queue of copy/move jobs and runs them sequentially on a background thread while
/// reporting live progress to the transfer panel. A single app-wide instance is shared so that
/// drag-drop (TabViewModel) and clipboard paste (MainViewModel) feed the same queue.
/// </summary>
public partial class TransferManager : ObservableObject
{
    public static TransferManager Instance { get; } = new();

    // FileOperationsService is stateless, so the manager owns its own instance.
    private readonly FileOperationsService _fileOps = new();

    public ObservableCollection<TransferJobViewModel> Jobs { get; } = [];

    private readonly Queue<(TransferJobViewModel Job, TaskCompletionSource<TransferOutcome?> Tcs)> _pending = new();
    private bool _processing;

    public bool HasJobs => Jobs.Count > 0;

    [ObservableProperty] private bool _isPanelCollapsed;

    private TransferManager()
    {
        Jobs.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasJobs));
    }

    /// <summary>
    /// Queues a transfer and returns a task that completes with its outcome when the job finishes
    /// (null if it was cancelled or failed). Must be called on the UI thread.
    /// </summary>
    public Task<TransferOutcome?> EnqueueAsync(TransferOperation op, IReadOnlyList<string> sources,
        string destFolder, FileOperationsService.ConflictResolution conflict)
    {
        var job = new TransferJobViewModel(op, sources, destFolder, conflict);
        var tcs = new TaskCompletionSource<TransferOutcome?>();
        job.RemoveRequested += () => Jobs.Remove(job);
        Jobs.Add(job);
        IsPanelCollapsed = false;
        _pending.Enqueue((job, tcs));
        EnsureProcessing();
        return tcs.Task;
    }

    [RelayCommand]
    private void ClearFinished()
    {
        foreach (var job in Jobs.Where(j => j.IsFinished).ToList())
            Jobs.Remove(job);
    }

    [RelayCommand]
    private void ToggleCollapsed() => IsPanelCollapsed = !IsPanelCollapsed;

    private async void EnsureProcessing()
    {
        if (_processing) return;
        _processing = true;
        try
        {
            while (_pending.Count > 0)
            {
                var (job, tcs) = _pending.Dequeue();
                await RunJobAsync(job, tcs);
            }
        }
        finally { _processing = false; }
    }

    private async Task RunJobAsync(TransferJobViewModel job, TaskCompletionSource<TransferOutcome?> tcs)
    {
        if (job.Cts.IsCancellationRequested)
        {
            job.MarkCanceled();
            tcs.SetResult(null);
            return;
        }

        job.MarkRunning();
        // Constructed on the UI thread, so reports marshal back to the UI thread.
        var progress = new Progress<TransferProgress>(job.ApplyProgress);
        try
        {
            var outcome = await _fileOps.RunTransferAsync(
                job.Operation, job.Sources, job.DestFolder, job.Conflict,
                job.Pause, progress, job.Cts.Token);
            job.MarkCompleted();
            tcs.SetResult(outcome);
        }
        catch (OperationCanceledException)
        {
            job.MarkCanceled();
            tcs.SetResult(null);
        }
        catch (Exception ex)
        {
            job.MarkFailed(ex.Message);
            tcs.SetResult(null);
        }
    }
}
