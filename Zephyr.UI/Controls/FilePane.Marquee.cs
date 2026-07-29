using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using Zephyr.Core.Models;

namespace Zephyr.UI.Controls;

// Rubber-band (marquee) selection. A left-press on empty ground — the gutters either
// side of the list, or the blank area past the last row — starts a selection box that
// highlights every file it sweeps. Pressing on a row keeps the old behaviour (select,
// or start a file drag) so this never competes with drag-and-drop.
//
// Only realized containers can be hit-tested, so swept items are remembered in
// _marqueeHits: an item that scrolls out of view during an auto-scroll stays selected,
// while shrinking the band back over visible rows de-selects them again.
public partial class FilePane
{
    private const double AutoScrollEdge = 16;   // px from the viewport edge that starts auto-scroll

    private bool             _marqueeActive;
    private bool             _marqueePastThreshold;
    private Point            _marqueeAnchor;    // ListArea coordinates
    private Point            _marqueeCurrent;
    private ListBox?         _marqueeList;
    private Panel?           _marqueePanel;     // items host — the realized containers
    private ScrollViewer?    _marqueeScroll;
    private DispatcherTimer? _marqueeTimer;
    private readonly HashSet<FileItem> _marqueeBase = [];   // selection carried in from before the drag
    private readonly HashSet<FileItem> _marqueeHits = [];   // items the band has swept

    // ── Start ──────────────────────────────────────────────────────────────
    private void ListArea_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_marqueeActive) return;
        if (!IsEmptyGround(e.OriginalSource as DependencyObject)) return;
        if (VisibleList() is not { } list) return;

        BeginMarquee(list, e.GetPosition(ListArea),
                     additive: Keyboard.Modifiers is ModifierKeys.Control or ModifierKeys.Shift);
        e.Handled = true;
    }

    // True when the press landed on nothing interactive — not a row, scrollbar,
    // column header or button.
    private static bool IsEmptyGround(DependencyObject? source)
    {
        for (var el = source; el != null; el = ParentOf(el))
        {
            if (el is ListBoxItem or ListViewItem or ScrollBar or Thumb
                     or GridViewColumnHeader or ButtonBase) return false;
            if (el is FilePane) break;
        }
        return true;
    }

    private ListBox? VisibleList()
    {
        if (FileList.Visibility          == Visibility.Visible) return FileList;
        if (SearchResultList.Visibility  == Visibility.Visible) return SearchResultList;
        if (IconView.Visibility          == Visibility.Visible) return IconView;
        return null;
    }

    private void BeginMarquee(ListBox list, Point origin, bool additive)
    {
        _marqueeList          = list;
        _marqueePanel         = FindItemsHost(list);
        // Walk up from the items host: searching downwards from the list would find the
        // GridView column header's scroll presenter instead of the rows'.
        _marqueeScroll        = _marqueePanel is null ? null : FindAncestor<ScrollViewer>(_marqueePanel);
        _marqueeAnchor        = origin;
        _marqueeCurrent       = origin;
        _marqueeActive        = true;
        _marqueePastThreshold = false;
        _marqueeBase.Clear();
        _marqueeHits.Clear();

        if (additive)
        {
            foreach (var item in list.SelectedItems.OfType<FileItem>())
                _marqueeBase.Add(item);
        }
        else
        {
            list.UnselectAll();
        }

        list.Focus();
        ListArea.MouseMove         += Marquee_MouseMove;
        ListArea.MouseLeftButtonUp += Marquee_MouseUp;
        ListArea.LostMouseCapture  += Marquee_LostCapture;
        ListArea.KeyDown           += Marquee_KeyDown;
        ListArea.CaptureMouse();

        _marqueeTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(60),
                                              DispatcherPriority.Input, MarqueeAutoScrollTick, Dispatcher);
        _marqueeTimer.Start();
    }

    // ── Drag ───────────────────────────────────────────────────────────────
    private void Marquee_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_marqueeActive) return;
        if (e.LeftButton != MouseButtonState.Pressed) { EndMarquee(cancel: false); return; }
        UpdateMarquee(e.GetPosition(ListArea));
    }

    private void UpdateMarquee(Point pos)
    {
        _marqueeCurrent = pos;

        if (!_marqueePastThreshold)
        {
            if (Math.Abs(pos.X - _marqueeAnchor.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(pos.Y - _marqueeAnchor.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            _marqueePastThreshold = true;
            MarqueeBox.Visibility = Visibility.Visible;
        }

        var band = new Rect(_marqueeAnchor, pos);
        Canvas.SetLeft(MarqueeBox, band.X);
        Canvas.SetTop (MarqueeBox, band.Y);
        MarqueeBox.Width  = band.Width;
        MarqueeBox.Height = band.Height;

        SweepSelection(band);
    }

    private void SweepSelection(Rect band)
    {
        if (_marqueeList is not { } list || _marqueePanel is not { } panel) return;

        var viewport = ViewportRect();
        var hits     = new HashSet<FileItem>();
        var onScreen = new List<FileItem>();

        foreach (var child in panel.Children.OfType<FrameworkElement>())
        {
            if (child.DataContext is not FileItem item || !child.IsVisible) continue;

            Rect bounds;
            try   { bounds = child.TransformToAncestor(ListArea).TransformBounds(new Rect(child.RenderSize)); }
            catch { continue; }   // container detached mid-layout

            // Rows half-scrolled under the column header only count for the part on screen.
            if (viewport is { } clip) bounds.Intersect(clip);
            if (bounds.IsEmpty || bounds.Height <= 0) continue;

            onScreen.Add(item);
            if (bounds.IntersectsWith(band)) hits.Add(item);
        }

        foreach (var item in onScreen)
            if (!hits.Contains(item)) _marqueeHits.Remove(item);
        _marqueeHits.UnionWith(hits);

        var desired = new HashSet<FileItem>(_marqueeBase);
        desired.UnionWith(_marqueeHits);
        ApplySelection(list, desired);
    }

    // Diff rather than clear-and-refill: each move only touches a handful of items,
    // and every change raises SelectionChanged.
    private static void ApplySelection(ListBox list, HashSet<FileItem> desired)
    {
        var current = list.SelectedItems.OfType<FileItem>().ToHashSet();
        foreach (var item in current)
            if (!desired.Contains(item)) list.SelectedItems.Remove(item);
        foreach (var item in desired)
            if (!current.Contains(item)) list.SelectedItems.Add(item);
    }

    // ── Auto-scroll past the top/bottom edge ───────────────────────────────
    private void MarqueeAutoScrollTick(object? sender, EventArgs e)
    {
        if (!_marqueeActive || !_marqueePastThreshold) return;
        if (_marqueeScroll is not { } sv) return;
        if (ViewportRect() is not { } viewport) return;

        if      (_marqueeCurrent.Y < viewport.Top    + AutoScrollEdge) sv.LineUp();
        else if (_marqueeCurrent.Y > viewport.Bottom - AutoScrollEdge) sv.LineDown();
        else return;

        // Realize the containers the scroll just brought in, then re-sweep.
        ListArea.UpdateLayout();
        UpdateMarquee(_marqueeCurrent);
    }

    // ── Finish ─────────────────────────────────────────────────────────────
    private void Marquee_MouseUp(object sender, MouseButtonEventArgs e)
    {
        var wasDrag = _marqueePastThreshold;
        EndMarquee(cancel: false);
        if (wasDrag) e.Handled = true;
    }

    private void Marquee_LostCapture(object sender, MouseEventArgs e) => EndMarquee(cancel: false);

    private void Marquee_KeyDown(object sender, KeyEventArgs e)
    {
        if (!_marqueeActive || e.Key != Key.Escape) return;
        EndMarquee(cancel: true);
        e.Handled = true;
    }

    private void EndMarquee(bool cancel)
    {
        if (!_marqueeActive) return;
        _marqueeActive        = false;
        _marqueePastThreshold = false;
        _marqueeTimer?.Stop();
        MarqueeBox.Visibility = Visibility.Collapsed;

        ListArea.MouseMove         -= Marquee_MouseMove;
        ListArea.MouseLeftButtonUp -= Marquee_MouseUp;
        ListArea.LostMouseCapture  -= Marquee_LostCapture;
        ListArea.KeyDown           -= Marquee_KeyDown;
        if (ListArea.IsMouseCaptured) ListArea.ReleaseMouseCapture();

        if (cancel && _marqueeList is { } list) ApplySelection(list, _marqueeBase);

        _marqueeList   = null;
        _marqueePanel  = null;
        _marqueeScroll = null;
        _marqueeHits.Clear();
        _marqueeBase.Clear();
    }

    // ── Visual-tree helpers ────────────────────────────────────────────────
    private static DependencyObject? ParentOf(DependencyObject el)
        => el is Visual or Visual3D ? VisualTreeHelper.GetParent(el) : LogicalTreeHelper.GetParent(el);

    // The scrollable row area of the list, in ListArea coordinates. Taken from the
    // presenter above the items host so the column header is correctly excluded.
    private Rect? ViewportRect()
    {
        if (_marqueePanel is not { } panel) return null;
        if (FindAncestor<ScrollContentPresenter>(panel) is not { } presenter) return null;
        try   { return presenter.TransformToAncestor(ListArea).TransformBounds(new Rect(presenter.RenderSize)); }
        catch { return null; }
    }

    private static Panel? FindItemsHost(DependencyObject root)
    {
        if (root is Panel { IsItemsHost: true } host) return host;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindItemsHost(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        return null;
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) return match;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindDescendant<T>(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        return null;
    }

    private static T? FindAncestor<T>(DependencyObject start) where T : DependencyObject
    {
        for (var el = start; el != null; el = VisualTreeHelper.GetParent(el))
            if (el is T match) return match;
        return null;
    }
}
