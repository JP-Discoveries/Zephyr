using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Zephyr.Core.FileSystem;
using Zephyr.Core.Models;
using Zephyr.UI.Dialogs;

namespace Zephyr.UI.ViewModels;

// Dual-pane compare / mirror: tints each pane's items by how they differ from the other
// pane (live, as either side changes) and copies new/changed items across on demand.
public partial class MainViewModel
{
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MirrorLeftToRightCommand))]
    [NotifyCanExecuteChangedFor(nameof(MirrorRightToLeftCommand))]
    private bool _isCompareMode;

    // The tabs whose Items collections we're currently observing for live re-comparison.
    private TabViewModel? _cmpLeftTab;
    private TabViewModel? _cmpRightTab;

    [RelayCommand]
    private void ToggleCompare() => IsCompareMode = !IsCompareMode;

    partial void OnIsCompareModeChanged(bool value)
    {
        if (value && !IsSplitView) IsSplitView = true;

        // Always detach first, then (re)attach if turning on.
        LeftPane.PropertyChanged  -= ComparePanePropertyChanged;
        RightPane.PropertyChanged -= ComparePanePropertyChanged;
        HookCompareTab(ref _cmpLeftTab,  null);
        HookCompareTab(ref _cmpRightTab, null);

        if (value)
        {
            LeftPane.PropertyChanged  += ComparePanePropertyChanged;
            RightPane.PropertyChanged += ComparePanePropertyChanged;
            HookCompareTab(ref _cmpLeftTab,  LeftPane.ActiveTab);
            HookCompareTab(ref _cmpRightTab, RightPane.ActiveTab);
            RecomputeCompare();
        }
    }

    private void ComparePanePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PaneViewModel.ActiveTab)) return;
        HookCompareTab(ref _cmpLeftTab,  LeftPane.ActiveTab);
        HookCompareTab(ref _cmpRightTab, RightPane.ActiveTab);
        RecomputeCompare();
    }

    private void HookCompareTab(ref TabViewModel? slot, TabViewModel? tab)
    {
        if (ReferenceEquals(slot, tab)) return;
        if (slot != null)
        {
            slot.Items.CollectionChanged -= CompareItemsChanged;
            PaneComparer.Clear(slot.Items);  // drop stale tints from the tab we're leaving
        }
        slot = tab;
        if (slot != null) slot.Items.CollectionChanged += CompareItemsChanged;
    }

    private void CompareItemsChanged(object? sender, NotifyCollectionChangedEventArgs e) => RecomputeCompare();

    private void RecomputeCompare()
    {
        if (!IsCompareMode) return;
        if (LeftPane.ActiveTab is not { } left || RightPane.ActiveTab is not { } right) return;
        PaneComparer.Compare(left.Items, right.Items);
    }

    private bool CanMirror() =>
        IsCompareMode && IsSplitView &&
        LeftPane.ActiveTab is { CurrentPath: var lp } && Directory.Exists(lp) &&
        RightPane.ActiveTab is { CurrentPath: var rp } && Directory.Exists(rp);

    [RelayCommand(CanExecute = nameof(CanMirror))]
    private Task MirrorLeftToRight() => MirrorAsync(LeftPane.ActiveTab!, RightPane.ActiveTab!);

    [RelayCommand(CanExecute = nameof(CanMirror))]
    private Task MirrorRightToLeft() => MirrorAsync(RightPane.ActiveTab!, LeftPane.ActiveTab!);

    // Copies everything missing or changed on the destination side, overwriting differing files.
    private async Task MirrorAsync(TabViewModel source, TabViewModel dest)
    {
        var toCopy = source.Items
            .Where(i => i.CompareStatus is CompareStatus.Unique or CompareStatus.Newer or CompareStatus.Different)
            .Select(i => i.FullPath)
            .ToList();

        if (toCopy.Count == 0)
        {
            ZephyrMessageBox.Show("Nothing to mirror — the destination already matches this folder.", "Mirror");
            return;
        }

        if (!ZephyrMessageBox.Confirm(
                $"Copy {toCopy.Count} new or changed item{(toCopy.Count == 1 ? "" : "s")} to:\n{dest.CurrentPath}\n\n" +
                "Existing files with the same name will be overwritten.",
                "Mirror", "Copy"))
            return;

        try
        {
            await Transfers.EnqueueAsync(TransferOperation.Copy, toCopy,
                dest.CurrentPath, FileOperationsService.ConflictResolution.Replace);
            dest.Reload();
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }
}
