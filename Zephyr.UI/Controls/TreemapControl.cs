using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Zephyr.Core.FileSystem;
using Zephyr.UI.Services;

namespace Zephyr.UI.Controls;

/// <summary>
/// A squarified treemap: every file is a rectangle whose area is proportional to its size,
/// coloured by file type. Folders nest as bordered containers. Rendered directly in
/// <see cref="OnRender"/> (no per-rectangle visual) so it scales to thousands of files.
/// </summary>
public sealed class TreemapControl : FrameworkElement
{
    private const int    MaxDepth = 9;
    private const double MinDir   = 14;  // below this a folder is drawn as a single block
    private const double MinDraw  = 1;   // rects smaller than this are skipped

    private static readonly Pen   CellPen   = FrozenPen(Color.FromArgb(0x40, 0x00, 0x00, 0x00), 0.4);
    private static readonly Pen   DirPen    = FrozenPen(Color.FromArgb(0x99, 0x00, 0x00, 0x00), 1);
    private static readonly Pen   HoverPen  = FrozenPen(Colors.White, 1.6);
    private static readonly Brush LabelBrush = Frozen(Colors.White);
    private static readonly Brush LabelBack  = Frozen(Color.FromArgb(0xB0, 0x00, 0x00, 0x00));
    private static readonly Typeface LabelFace = new("Segoe UI");

    // A shared "cushion" overlay: a soft radial bump that maps to each cell's bounds, giving
    // the glossy raised-tile look (à la WizTree) without per-cell brush allocation.
    private static readonly Brush Cushion = BuildCushion();

    private readonly List<(UsageNode node, Rect rect, Brush brush)> _cells = [];  // files + collapsed dirs
    private readonly List<(UsageNode node, Rect rect)> _dirs  = [];               // folder containers

    private UsageNode? _root;
    private Size _builtFor;
    private UsageNode? _hovered;

    public event Action<UsageNode?>? HoverChanged;
    public event Action<UsageNode>?  DrillRequested;

    public UsageNode? HoveredNode => _hovered;

    public void SetRoot(UsageNode? root)
    {
        _root = root;
        _builtFor = Size.Empty;
        _hovered = null;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = RenderSize;
        // Opaque backdrop so hit-testing covers the whole surface.
        dc.DrawRectangle(Frozen(Color.FromRgb(0x1E, 0x1E, 0x1E)), null, new Rect(size));

        if (_root is null || size.Width < 2 || size.Height < 2) return;

        if (size != _builtFor) { Rebuild(size); _builtFor = size; }

        foreach (var (_, rect, brush) in _cells)
        {
            dc.DrawRectangle(brush, CellPen, rect);
            dc.DrawRectangle(Cushion, null, rect);  // glossy bump on top of the flat colour
        }
        foreach (var (_, rect) in _dirs)
            dc.DrawRectangle(null, DirPen, rect);

        // Folder name + size labels (WizTree-style) on containers big enough to read.
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        foreach (var (node, rect) in _dirs)
        {
            if (rect.Width < 54 || rect.Height < 18) continue;
            var text = $"{node.Name}  ({node.SizeDisplay})";
            var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                LabelFace, 11, LabelBrush, dpi)
            {
                MaxTextWidth  = Math.Max(1, rect.Width - 6),
                MaxLineCount  = 1,
                Trimming      = TextTrimming.CharacterEllipsis,
            };
            double backW = Math.Min(rect.Width, ft.WidthIncludingTrailingWhitespace + 6);
            dc.DrawRectangle(LabelBack, null, new Rect(rect.X, rect.Y, backW, ft.Height + 3));
            dc.DrawText(ft, new Point(rect.X + 3, rect.Y + 1));
        }

        if (_hovered is not null && TryFindRect(_hovered, out var hr))
            dc.DrawRectangle(null, HoverPen, hr);
    }

    private void Rebuild(Size size)
    {
        _cells.Clear();
        _dirs.Clear();
        if (_root is not null)
            Layout(_root, new Rect(0, 0, size.Width, size.Height), 0);
    }

    private void Layout(UsageNode node, Rect rect, int depth)
    {
        if (rect.Width < MinDraw || rect.Height < MinDraw) return;

        if (!node.HasChildren) { _cells.Add((node, rect, BrushFor(node))); return; }

        var placed = new List<(UsageNode, Rect)>();
        Squarify(node.Children, rect, placed);

        foreach (var (child, r) in placed)
        {
            if (r.Width < MinDraw || r.Height < MinDraw) continue;

            if (child.IsDirectory && child.HasChildren &&
                depth < MaxDepth && r.Width > MinDir && r.Height > MinDir)
            {
                _dirs.Add((child, r));
                Layout(child, Deflate(r, 1.5), depth + 1);
            }
            else
            {
                _cells.Add((child, r, BrushFor(child)));  // a file, or a folder too small/deep to expand
            }
        }
    }

    // Colour for a cell: a file by its extension; a folder by the extension of its single
    // largest leaf (follow the biggest child down), so collapsed folders are never grey.
    private static Brush BrushFor(UsageNode node) =>
        FileTypeColorService.GetBrush(node.IsDirectory ? RepresentativeExtension(node) : node.Extension);

    private static string RepresentativeExtension(UsageNode node)
    {
        var cur = node;
        while (cur.IsDirectory && cur.HasChildren)
        {
            UsageNode? biggest = null;
            foreach (var c in cur.Children)
                if (biggest is null || c.Bytes > biggest.Bytes) biggest = c;
            if (biggest is null) break;
            cur = biggest;
        }
        return cur.Extension;
    }

    // Squarified treemap (Bruls, Huizing & van Wijk, 1999): lay children out in rows that
    // keep rectangles as close to square as possible.
    private static void Squarify(IReadOnlyList<UsageNode> nodes, Rect bounds, List<(UsageNode, Rect)> output)
    {
        var items = nodes.Where(n => n.Bytes > 0).OrderByDescending(n => n.Bytes).ToList();
        if (items.Count == 0) return;

        double total = 0;
        foreach (var n in items) total += n.Bytes;
        double area = bounds.Width * bounds.Height;
        if (area <= 0 || total <= 0) return;

        var areas = new double[items.Count];
        for (int k = 0; k < items.Count; k++) areas[k] = items[k].Bytes / total * area;

        var free = bounds;
        int index = 0;
        while (index < items.Count)
        {
            double shortSide = Math.Min(free.Width, free.Height);
            if (shortSide <= 0) break;

            int rowCount = 1;
            double rowArea = areas[index];
            double worst = Worst(areas, index, index, shortSide, rowArea);
            while (index + rowCount < items.Count)
            {
                double nextArea = rowArea + areas[index + rowCount];
                double nextWorst = Worst(areas, index, index + rowCount, shortSide, nextArea);
                if (nextWorst > worst) break;
                worst = nextWorst;
                rowArea = nextArea;
                rowCount++;
            }

            double thickness = rowArea / shortSide;
            double offset = 0;
            if (free.Width >= free.Height)
            {
                for (int k = index; k < index + rowCount; k++)
                {
                    double h = areas[k] / thickness;
                    output.Add((items[k], new Rect(free.X, free.Y + offset, thickness, h)));
                    offset += h;
                }
                free = new Rect(free.X + thickness, free.Y, Math.Max(0, free.Width - thickness), free.Height);
            }
            else
            {
                for (int k = index; k < index + rowCount; k++)
                {
                    double w = areas[k] / thickness;
                    output.Add((items[k], new Rect(free.X + offset, free.Y, w, thickness)));
                    offset += w;
                }
                free = new Rect(free.X, free.Y + thickness, free.Width, Math.Max(0, free.Height - thickness));
            }
            index += rowCount;
        }
    }

    private static double Worst(double[] areas, int start, int end, double shortSide, double rowArea)
    {
        double max = double.MinValue, min = double.MaxValue;
        for (int k = start; k <= end; k++)
        {
            if (areas[k] > max) max = areas[k];
            if (areas[k] < min) min = areas[k];
        }
        double s2 = shortSide * shortSide;
        double a2 = rowArea * rowArea;
        return Math.Max(s2 * max / a2, a2 / (s2 * min));
    }

    // ── Interaction ─────────────────────────────────────────────────────────

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var node = HitTest(e.GetPosition(this));
        if (!ReferenceEquals(node, _hovered))
        {
            _hovered = node;
            HoverChanged?.Invoke(node);
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        if (_hovered is not null)
        {
            _hovered = null;
            HoverChanged?.Invoke(null);
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2) return;
        var dir = HitTestDir(e.GetPosition(this));
        if (dir is not null) DrillRequested?.Invoke(dir);
    }

    // Most specific block (files win over the folders that contain them).
    private UsageNode? HitTest(Point p)
    {
        for (int i = _cells.Count - 1; i >= 0; i--)
            if (_cells[i].rect.Contains(p)) return _cells[i].node;
        for (int i = _dirs.Count - 1; i >= 0; i--)
            if (_dirs[i].rect.Contains(p)) return _dirs[i].node;
        return null;
    }

    // Smallest (deepest) folder under the point — what a double-click drills into.
    private UsageNode? HitTestDir(Point p)
    {
        UsageNode? best = null;
        double bestArea = double.MaxValue;
        foreach (var (node, rect) in _dirs)
            if (rect.Contains(p) && rect.Width * rect.Height < bestArea)
            {
                best = node;
                bestArea = rect.Width * rect.Height;
            }
        if (best is not null) return best;

        // A folder collapsed into a single block is still drillable.
        for (int i = _cells.Count - 1; i >= 0; i--)
            if (_cells[i].rect.Contains(p) && _cells[i].node is { IsDirectory: true, HasChildren: true })
                return _cells[i].node;
        return null;
    }

    private bool TryFindRect(UsageNode node, out Rect rect)
    {
        foreach (var (n, r, _) in _cells) if (ReferenceEquals(n, node)) { rect = r; return true; }
        foreach (var (n, r) in _dirs)  if (ReferenceEquals(n, node)) { rect = r; return true; }
        rect = default;
        return false;
    }

    private static Rect Deflate(Rect r, double by)
    {
        double w = Math.Max(0, r.Width  - 2 * by);
        double h = Math.Max(0, r.Height - 2 * by);
        return new Rect(r.X + by, r.Y + by, w, h);
    }

    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
    private static Pen FrozenPen(Color c, double t) { var p = new Pen(Frozen(c), t); p.Freeze(); return p; }

    // Radial highlight (top-left) fading to a dark edge — a cheap cushion/bump shade reused
    // for every cell. Mapped relative to each rectangle's bounds.
    private static Brush BuildCushion()
    {
        var brush = new RadialGradientBrush
        {
            GradientOrigin = new Point(0.32, 0.28),
            Center         = new Point(0.5, 0.5),
            RadiusX = 0.85, RadiusY = 0.85,
            MappingMode = BrushMappingMode.RelativeToBoundingBox,
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF), 0.0),
                new GradientStop(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF), 0.35),
                new GradientStop(Color.FromArgb(0x00, 0x00, 0x00, 0x00), 0.55),
                new GradientStop(Color.FromArgb(0x55, 0x00, 0x00, 0x00), 1.0),
            },
        };
        brush.Freeze();
        return brush;
    }
}
