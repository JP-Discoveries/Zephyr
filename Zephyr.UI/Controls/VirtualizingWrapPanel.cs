using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using WpfGen = System.Windows.Controls.ItemContainerGenerator;

namespace Zephyr.UI.Controls;

/// <summary>
/// Virtualizing wrap panel — only creates containers for visible items.
/// Requires ScrollViewer.CanContentScroll="True" on the parent ItemsControl.
/// </summary>
public class VirtualizingWrapPanel : VirtualizingPanel, IScrollInfo
{
    // ── Item size ─────────────────────────────────────────────────────────────
    public static readonly DependencyProperty ItemWidthProperty =
        DependencyProperty.Register(nameof(ItemWidth), typeof(double), typeof(VirtualizingWrapPanel),
            new FrameworkPropertyMetadata(120.0, FrameworkPropertyMetadataOptions.AffectsMeasure,
                (d, _) => ((VirtualizingWrapPanel)d).ClearAllContainers()));

    public static readonly DependencyProperty ItemHeightProperty =
        DependencyProperty.Register(nameof(ItemHeight), typeof(double), typeof(VirtualizingWrapPanel),
            new FrameworkPropertyMetadata(150.0, FrameworkPropertyMetadataOptions.AffectsMeasure,
                (d, _) => ((VirtualizingWrapPanel)d).ClearAllContainers()));

    public double ItemWidth  { get => (double)GetValue(ItemWidthProperty); set => SetValue(ItemWidthProperty, value); }
    public double ItemHeight { get => (double)GetValue(ItemHeightProperty); set => SetValue(ItemHeightProperty, value); }

    // ── Generator accessors ───────────────────────────────────────────────────
    // Interface: StartAt, GenerateNext, Remove, GeneratorPositionFromIndex
    private IItemContainerGenerator IGen => ItemContainerGenerator;
    // Concrete class: IndexFromContainer, PrepareItemContainer
    private WpfGen CGen => (WpfGen)ItemContainerGenerator;

    // ── Cached layout state (kept so Arrange agrees with Measure) ────────────
    private int    _lastCols      = 1;
    private double _lastActualIw  = 120;

    // ── IScrollInfo ───────────────────────────────────────────────────────────
    private ScrollViewer? _owner;
    private double        _vertOffset;
    private Size          _extent;
    private Size          _viewport;

    public ScrollViewer? ScrollOwner   { get => _owner; set => _owner = value; }
    public bool CanHorizontallyScroll  { get; set; }
    public bool CanVerticallyScroll    { get; set; } = true;
    public double ExtentWidth          => _extent.Width;
    public double ExtentHeight         => _extent.Height;
    public double ViewportWidth        => _viewport.Width;
    public double ViewportHeight       => _viewport.Height;
    public double HorizontalOffset     => 0;
    public double VerticalOffset       => _vertOffset;

    public void LineUp()          => ScrollTo(_vertOffset - ItemHeight * 0.25);
    public void LineDown()        => ScrollTo(_vertOffset + ItemHeight * 0.25);
    public void PageUp()          => ScrollTo(_vertOffset - _viewport.Height);
    public void PageDown()        => ScrollTo(_vertOffset + _viewport.Height);
    public void MouseWheelUp()    => ScrollTo(_vertOffset - SystemParameters.WheelScrollLines * ItemHeight * 0.25);
    public void MouseWheelDown()  => ScrollTo(_vertOffset + SystemParameters.WheelScrollLines * ItemHeight * 0.25);
    public void LineLeft()  {} public void LineRight()  {}
    public void PageLeft()  {} public void PageRight()  {}
    public void MouseWheelLeft() {} public void MouseWheelRight() {}
    public void SetHorizontalOffset(double o) {}
    public void SetVerticalOffset(double o)  => ScrollTo(o);
    public Rect MakeVisible(Visual v, Rect r) => r;

    private void ScrollTo(double offset)
    {
        var max     = Math.Max(0, _extent.Height - _viewport.Height);
        var clamped = Math.Max(0, Math.Min(offset, max));
        if (Math.Abs(_vertOffset - clamped) < 0.5) return;
        _vertOffset = clamped;
        _owner?.InvalidateScrollInfo();
        InvalidateMeasure();
    }

    // ── Layout ────────────────────────────────────────────────────────────────
    protected override Size MeasureOverride(Size available)
    {
        if (double.IsInfinity(available.Width))  available.Width  = 800;
        if (double.IsInfinity(available.Height)) available.Height = 600;

        var iw       = Math.Max(1, ItemWidth);
        var ih       = Math.Max(1, ItemHeight);
        var cols     = Math.Max(1, (int)(available.Width / iw));
        var actualIw = available.Width / cols; // spread items to fill the row
        _lastCols    = cols;
        _lastActualIw = actualIw;

        var count  = GetItemCount();
        var rows   = count == 0 ? 0 : (int)Math.Ceiling((double)count / cols);
        var totalH = rows * ih;

        _viewport = available;
        _extent   = new Size(available.Width, totalH);
        _owner?.InvalidateScrollInfo();

        _vertOffset = Math.Max(0, Math.Min(_vertOffset, Math.Max(0, totalH - available.Height)));

        int first = 0, last = -1;
        if (count > 0)
        {
            var firstRow = (int)Math.Floor(_vertOffset / ih);
            var lastRow  = (int)Math.Ceiling((_vertOffset + available.Height) / ih);
            first = Math.Max(0, firstRow * cols);
            last  = Math.Min(count - 1, (lastRow + 1) * cols - 1);
        }

        // Remove off-screen containers (iterate backwards to keep indices stable)
        for (int i = InternalChildren.Count - 1; i >= 0; i--)
        {
            var idx = CGen.IndexFromContainer(InternalChildren[i]);
            if (idx < 0 || idx < first || idx > last)
            {
                if (idx >= 0)
                {
                    var gp = IGen.GeneratorPositionFromIndex(idx);
                    if (gp.Index >= 0) IGen.Remove(gp, 1);
                }
                RemoveInternalChildRange(i, 1);
            }
        }

        // Realize visible containers
        if (count > 0 && first <= last)
        {
            var startPos = IGen.GeneratorPositionFromIndex(first);
            int childIdx = startPos.Offset == 0 ? startPos.Index : startPos.Index + 1;
            if (childIdx < 0) childIdx = 0;

            using (IGen.StartAt(startPos, GeneratorDirection.Forward, true))
            {
                for (int i = first; i <= last; i++, childIdx++)
                {
                    var child = IGen.GenerateNext(out bool isNew) as UIElement;
                    if (child == null) break;
                    if (isNew)
                    {
                        if (childIdx >= InternalChildren.Count) AddInternalChild(child);
                        else                                     InsertInternalChild(childIdx, child);
                        IGen.PrepareItemContainer(child);
                    }
                    child.Measure(new Size(actualIw, ih));
                }
            }
        }

        // Return the viewport size — IScrollInfo already owns extent reporting.
        return available;
    }

    protected override Size ArrangeOverride(Size final)
    {
        var ih       = Math.Max(1, ItemHeight);
        var cols     = _lastCols;
        var actualIw = _lastActualIw > 0 ? _lastActualIw : final.Width / cols;

        foreach (UIElement child in InternalChildren)
        {
            var idx = CGen.IndexFromContainer(child);
            if (idx < 0) continue;
            child.Arrange(new Rect(
                (idx % cols) * actualIw,
                (idx / cols) * ih - _vertOffset,
                actualIw,
                ih));
        }
        return final;
    }

    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        base.OnItemsChanged(sender, args);
        if (args.Action is NotifyCollectionChangedAction.Reset
                        or NotifyCollectionChangedAction.Remove
                        or NotifyCollectionChangedAction.Replace)
            ClearAllContainers();
        InvalidateMeasure();
    }

    private void ClearAllContainers()
    {
        int count = InternalChildren.Count;
        if (count == 0) return;
        // RemoveInternalChildRange does NOT notify the generator — must do it explicitly
        // so that GenerateNext returns isNew=true on the next measure pass.
        IGen.Remove(new GeneratorPosition(0, 0), count);
        RemoveInternalChildRange(0, count);
    }

    private int GetItemCount() => ItemsControl.GetItemsOwner(this)?.Items.Count ?? 0;
}
