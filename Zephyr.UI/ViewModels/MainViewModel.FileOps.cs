using System.IO;
using System.Windows;
using System.Windows.Interop;
using CommunityToolkit.Mvvm.Input;
using Zephyr.Core.FileSystem;
using Zephyr.UI.Dialogs;
using Zephyr.UI.Services;

namespace Zephyr.UI.ViewModels;

// File operations driven from the toolbar/hotkeys/context menu (new folder, clipboard,
// rename, delete, batch rename, terminal) plus the single-level undo stack that backs them.
public partial class MainViewModel
{
    // ── Undo stack ────────────────────────────────────────────────────────────

    private readonly Stack<Func<Task>> _undoStack = new();

    private void PushUndo(Func<Task> action)
    {
        _undoStack.Push(action);
        UndoCommand.NotifyCanExecuteChanged();
    }

    private bool CanUndo() => _undoStack.Count > 0;

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private async Task UndoAsync()
    {
        if (_undoStack.Count == 0) return;
        var action = _undoStack.Pop();
        UndoCommand.NotifyCanExecuteChanged();
        await action();
    }

    // ── File operations ───────────────────────────────────────────────────────

    [RelayCommand]
    private void NewFolder()
    {
        if (ActiveTab is not { } tab) return;
        var dlg = new InputDialog("New Folder", "Folder name:", "New Folder")
            { Owner = Application.Current.MainWindow };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var path = _fileOps.CreateFolder(tab.CurrentPath, dlg.Result);
            RecentInteractionService.Record(path);
            tab.Reload();
            PushUndo(async () =>
            {
                try
                {
                    await Task.Run(() =>
                    {
                        if (Directory.Exists(path))
                            Directory.Delete(path, recursive: false);
                    });
                    ActiveTab?.Reload();
                }
                catch (Exception ex) { ShowError($"Undo failed: {ex.Message}"); }
            });
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    [RelayCommand]
    private void Copy()
    {
        var items = ActiveTab?.SelectedItems;
        if (items is null || items.Count == 0) return;
        var paths = items.Select(i => i.FullPath).ToList();
        ClipboardService.SetFiles(paths, ClipboardEffect.Copy);
        ClipboardHighlightService.Set(paths, ClipboardEffect.Copy);
        RefreshClipboardHighlights();
    }

    [RelayCommand]
    private void Cut()
    {
        var items = ActiveTab?.SelectedItems;
        if (items is null || items.Count == 0) return;
        var paths = items.Select(i => i.FullPath).ToList();
        ClipboardService.SetFiles(paths, ClipboardEffect.Cut);
        ClipboardHighlightService.Set(paths, ClipboardEffect.Cut);
        RefreshClipboardHighlights();
    }

    [RelayCommand]
    private void ClearClipboard()
    {
        ClipboardService.Clear();
        ClipboardHighlightService.Clear();
        RefreshClipboardHighlights();
    }

    private void RefreshClipboardHighlights()
    {
        foreach (var pane in new[] { LeftPane, RightPane })
        foreach (var tab in pane.Tabs)
            tab.ApplyClipboardHighlights();
    }

    [RelayCommand]
    private async Task PasteAsync()
    {
        if (ActiveTab is not { } tab) return;
        if (!ClipboardService.HasFiles()) return;
        var (paths, effect) = ClipboardService.GetFiles();
        if (paths.Count == 0) return;
        try
        {
            if (effect == ClipboardEffect.Cut)
            {
                // Skip files that are already in the destination — moving them would
                // delete the original and produce a "(2)" copy for no reason.
                var filtered = paths
                    .Where(p => !string.Equals(
                        Path.GetDirectoryName(p), tab.CurrentPath,
                        StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (filtered.Count == 0) { ClipboardService.Clear(); ClipboardHighlightService.Clear(); RefreshClipboardHighlights(); return; }
                var outcome = await Transfers.EnqueueAsync(TransferOperation.Move, filtered,
                    tab.CurrentPath, FileOperationsService.ConflictResolution.KeepBoth);
                tab.Reload();
                ClipboardService.Clear(); // mirrors Explorer: cut clipboard is consumed after paste
                ClipboardHighlightService.Clear();
                RefreshClipboardHighlights();
                if (outcome is { RootPairs.Count: > 0 })
                {
                    var captured = outcome.RootPairs; // capture for lambda
                    PushUndo(async () =>
                    {
                        try
                        {
                            await Task.Run(() =>
                            {
                                foreach (var (src, dest) in captured)
                                {
                                    if (!File.Exists(dest) && !Directory.Exists(dest)) continue;
                                    var srcDir = Path.GetDirectoryName(src)!;
                                    Directory.CreateDirectory(srcDir);
                                    if (Directory.Exists(dest)) Directory.Move(dest, src);
                                    else                         File.Move(dest, src, overwrite: false);
                                }
                            });
                            ActiveTab?.Reload();
                        }
                        catch (Exception ex) { ShowError($"Undo failed: {ex.Message}"); }
                    });
                }
            }
            else
            {
                var outcome = await Transfers.EnqueueAsync(TransferOperation.Copy, paths,
                    tab.CurrentPath, FileOperationsService.ConflictResolution.KeepBoth);
                tab.Reload();
                ClipboardHighlightService.Clear();
                RefreshClipboardHighlights();
                if (outcome is { CreatedRoots.Count: > 0 })
                {
                    var captured = outcome.CreatedRoots;
                    PushUndo(async () =>
                    {
                        try
                        {
                            await Task.Run(() =>
                            {
                                foreach (var dest in captured)
                                {
                                    if      (File.Exists(dest))      File.Delete(dest);
                                    else if (Directory.Exists(dest)) Directory.Delete(dest, recursive: true);
                                }
                            });
                            ActiveTab?.Reload();
                        }
                        catch (Exception ex) { ShowError($"Undo failed: {ex.Message}"); }
                    });
                }
            }
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    [RelayCommand]
    private void Rename()
    {
        if (ActiveTab?.SelectedItem is not { } item) return;
        var dlg = new InputDialog("Rename", "New name:", item.Name)
            { Owner = Application.Current.MainWindow };
        if (dlg.ShowDialog() != true) return;
        var result = dlg.Result;
        if (!item.IsDirectory)
        {
            var origExt = Path.GetExtension(item.Name);
            if (!string.IsNullOrEmpty(origExt) && string.IsNullOrEmpty(Path.GetExtension(result)))
                result += origExt;
        }
        var newPath = Path.Combine(Path.GetDirectoryName(item.FullPath)!, result);
        if (!string.Equals(item.FullPath, newPath, StringComparison.OrdinalIgnoreCase) &&
            (File.Exists(newPath) || Directory.Exists(newPath)))
        {
            ZephyrMessageBox.Show($"A file named \"{result}\" already exists in this folder.", "Rename");
            return;
        }
        var oldName = item.Name;
        try
        {
            _fileOps.Rename(item.FullPath, result);
            RecentInteractionService.Record(newPath);
            ActiveTab.Reload();
            PushUndo(async () =>
            {
                try
                {
                    await Task.Run(() => _fileOps.Rename(newPath, oldName));
                    ActiveTab?.Reload();
                }
                catch (Exception ex) { ShowError($"Undo failed: {ex.Message}"); }
            });
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    [RelayCommand]
    private void Delete()
    {
        var items = ActiveTab?.SelectedItems;
        if (items is null || items.Count == 0) return;
        var label = items.Count == 1 ? $"'{items[0].Name}'" : $"{items.Count} items";
        if (!ZephyrMessageBox.Confirm($"Send {label} to the Recycle Bin?", "Delete", "Delete")) return;
        try
        {
            var hwnd = new WindowInteropHelper(Application.Current.MainWindow).Handle;
            _fileOps.Delete(items.Select(i => i.FullPath), hwnd: hwnd);
            ActiveTab!.Reload();
            // Record undo using the Windows Shell's own undo for recycle bin operations.
            // SHFileOperation with FOF_ALLOWUNDO records in the global shell undo stack.
            PushUndo(async () =>
            {
                try
                {
                    await Task.Run(() =>
                    {
                        var shellType = Type.GetTypeFromProgID("Shell.Application");
                        if (shellType == null) return;
                        dynamic shell = Activator.CreateInstance(shellType)!;
                        shell.UndoFileOperation();
                    });
                    await Task.Delay(400); // allow Shell to restore before reloading
                    ActiveTab?.Reload();
                }
                catch (Exception ex) { ShowError($"Undo failed: {ex.Message}"); }
            });
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    [RelayCommand]
    private void PermanentDelete()
    {
        var items = ActiveTab?.SelectedItems;
        if (items is null || items.Count == 0) return;
        var label = items.Count == 1 ? $"'{items[0].Name}'" : $"{items.Count} items";
        if (!ZephyrMessageBox.Confirm($"Permanently delete {label}? This cannot be undone.", "Delete Forever", "Delete")) return;
        try
        {
            _fileOps.Delete(items.Select(i => i.FullPath), permanent: true);
            ActiveTab!.Reload();
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    [RelayCommand]
    private void OpenTerminal()
    {
        if (ActiveTab?.CurrentPath is { } path)
            TerminalService.OpenAt(path);
    }

    [RelayCommand]
    private void BatchRename()
    {
        var items = ActiveTab?.SelectedItems;
        if (items is null || items.Count < 2) return;
        var dlg = new BatchRenameDialog(items.Select(i => i.FullPath))
            { Owner = Application.Current.MainWindow };
        if (dlg.ShowDialog() != true) return;
        try
        {
            foreach (var (oldPath, newName) in dlg.Results)
                _fileOps.Rename(oldPath, newName);
            ActiveTab!.Reload();
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }
}
