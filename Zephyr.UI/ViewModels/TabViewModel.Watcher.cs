using System.IO;
using System.Windows;

namespace Zephyr.UI.ViewModels;

// Live folder watching: a debounced FileSystemWatcher that reloads the current folder on
// change, and recovers gracefully when the watched path disappears.
public partial class TabViewModel
{
    private CancellationTokenSource? _watcherDebounce;
    private FileSystemWatcher?       _watcher;

    private void SetupWatcher(string? path)
    {
        _watcher?.Dispose();
        _watcher = null;
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            _watcher = new FileSystemWatcher(path)
            {
                NotifyFilter          = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
                IncludeSubdirectories = false,
                EnableRaisingEvents   = true,
            };
            _watcher.Created += OnWatcherEvent;
            _watcher.Deleted += OnWatcherEvent;
            _watcher.Renamed += OnWatcherEvent;
            _watcher.Changed += OnWatcherEvent;
            _watcher.Error   += OnWatcherError;
        }
        catch { _watcher = null; }
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (!string.IsNullOrEmpty(CurrentPath) && !Directory.Exists(CurrentPath))
                HandlePathLost();
        });
    }

    private void OnWatcherEvent(object sender, FileSystemEventArgs e)
    {
        _watcherDebounce?.Cancel();
        _watcherDebounce = new CancellationTokenSource();
        var cts = _watcherDebounce;
        _ = Task.Delay(600, cts.Token).ContinueWith(t =>
        {
            if (!t.IsCompletedSuccessfully) return;
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                if (!IsSearchMode) _ = LoadDirectoryAsync(CurrentPath);
            });
        }, TaskScheduler.Default);
    }
}
