using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using KochanSentinel.Core.Models;

namespace KochanSentinel.App.Controls;

/// <summary>
/// Interactive WinDirStat/SpaceSniffer-style squarified treemap visualizing folder &amp; file space distribution.
/// </summary>
public sealed class TreemapControl : FrameworkElement
{
    public static readonly DependencyProperty RootNodeProperty = DependencyProperty.Register(
        nameof(RootNode), typeof(DiskNode), typeof(TreemapControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnRootChanged));

    public static readonly DependencyProperty SelectedNodeProperty = DependencyProperty.Register(
        nameof(SelectedNode), typeof(DiskNode), typeof(TreemapControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault | FrameworkPropertyMetadataOptions.AffectsRender));

    public event Action<DiskNode>? NodeDoubleClicked;

    private sealed class TreemapBlock
    {
        public DiskNode Node { get; init; } = null!;
        public Rect Bounds { get; init; }
        public System.Windows.Media.Color FillColor { get; init; }
    }

    private readonly List<TreemapBlock> _blocks = new();
    private DiskNode? _hoveredNode;
    private readonly System.Windows.Controls.ToolTip _tip = new();

    public DiskNode? RootNode
    {
        get => (DiskNode?)GetValue(RootNodeProperty);
        set => SetValue(RootNodeProperty, value);
    }

    public DiskNode? SelectedNode
    {
        get => (DiskNode?)GetValue(SelectedNodeProperty);
        set => SetValue(SelectedNodeProperty, value);
    }

    public TreemapControl()
    {
        ClipToBounds = true;
        Cursor = System.Windows.Input.Cursors.Hand;
        ToolTip = _tip;
    }

    private static void OnRootChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TreemapControl ctrl)
        {
            ctrl._hoveredNode = null;
            ctrl.InvalidateVisual();
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth;
        double h = ActualHeight;
        _blocks.Clear();

        dc.DrawRoundedRectangle(
            new SolidColorBrush(System.Windows.Media.Color.FromRgb(9, 9, 11)),
            null,
            new Rect(0, 0, Math.Max(0, w), Math.Max(0, h)),
            6, 6);

        if (w < 40 || h < 40 || RootNode == null || RootNode.SizeBytes <= 0 || RootNode.Children.Count == 0)
        {
            var emptyFt = new FormattedText(
                "Treemap önizlemesi için sol üstten Diski Tara butonuna tıkla.",
                CultureInfo.CurrentCulture,
                System.Windows.FlowDirection.LeftToRight,
                new Typeface("Segoe UI"),
                12,
                new SolidColorBrush(System.Windows.Media.Color.FromRgb(113, 113, 122)),
                VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(emptyFt, new System.Windows.Point(Math.Max(12, (w - emptyFt.Width) / 2), Math.Max(12, (h - emptyFt.Height) / 2)));
            return;
        }

        // Layout top-level children squarified across full area
        var topItems = RootNode.Children.Where(c => c.SizeBytes > 0).Take(32).ToList();
        var topRects = ComputeSquarifiedLayout(topItems, new Rect(2, 2, w - 4, h - 4));

        var borderPen = new System.Windows.Media.Pen(new SolidColorBrush(System.Windows.Media.Color.FromRgb(9, 9, 11)), 1.5);
        borderPen.Freeze();

        var hoverPen = new System.Windows.Media.Pen(new SolidColorBrush(System.Windows.Media.Color.FromRgb(250, 250, 250)), 1.8);
        hoverPen.Freeze();

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = new Typeface(new System.Windows.Media.FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

        foreach (var (node, rect) in topRects)
        {
            if (rect.Width < 3 || rect.Height < 3) continue;

            // If large folder with children, subdivide 1 level deeper for authentic WinDirStat depth
            if (node.IsDirectory && node.Children.Count > 1 && rect.Width > 54 && rect.Height > 44)
            {
                var subRect = new Rect(rect.X + 2, rect.Y + 17, rect.Width - 4, rect.Height - 19);
                var folderFill = GetColorForNode(node, isHeader: true);

                dc.DrawRoundedRectangle(new SolidColorBrush(folderFill), borderPen, rect, 4, 4);
                DrawClippedLabel(dc, rect, $"{node.Name} ({node.SizeFormatted})", typeface, 10.5, dpi, topOnly: true);

                var subItems = node.Children.Where(c => c.SizeBytes > 0).Take(18).ToList();
                var subBlocks = ComputeSquarifiedLayout(subItems, subRect);
                foreach (var (subNode, sr) in subBlocks)
                {
                    if (sr.Width < 3 || sr.Height < 3) continue;
                    var col = GetColorForNode(subNode, false);
                    var b = new TreemapBlock { Node = subNode, Bounds = sr, FillColor = col };
                    _blocks.Add(b);
                    DrawBlock(dc, b, borderPen, hoverPen, typeface, dpi);
                }

                // Also register header strip hit target for the parent folder
                _blocks.Add(new TreemapBlock
                {
                    Node = node,
                    Bounds = new Rect(rect.X, rect.Y, rect.Width, 17),
                    FillColor = folderFill
                });
            }
            else
            {
                var col = GetColorForNode(node, false);
                var b = new TreemapBlock { Node = node, Bounds = rect, FillColor = col };
                _blocks.Add(b);
                DrawBlock(dc, b, borderPen, hoverPen, typeface, dpi);
            }
        }
    }

    private void DrawBlock(DrawingContext dc, TreemapBlock b, System.Windows.Media.Pen borderPen, System.Windows.Media.Pen hoverPen, Typeface tf, double dpi)
    {
        bool isSelected = SelectedNode != null && ReferenceEquals(SelectedNode, b.Node);
        bool isHovered = _hoveredNode != null && ReferenceEquals(_hoveredNode, b.Node);

        var col = b.FillColor;
        if (isHovered || isSelected)
        {
            col = System.Windows.Media.Color.FromRgb(
                (byte)Math.Min(255, col.R + 28),
                (byte)Math.Min(255, col.G + 28),
                (byte)Math.Min(255, col.B + 28));
        }

        dc.DrawRoundedRectangle(
            new SolidColorBrush(col),
            (isSelected || isHovered) ? hoverPen : borderPen,
            b.Bounds,
            3, 3);

        if (b.Bounds.Width >= 56 && b.Bounds.Height >= 24)
            DrawClippedLabel(dc, b.Bounds, $"{b.Node.Name}\n{b.Node.SizeFormatted}", tf, 10.5, dpi, topOnly: false);
    }

    private static void DrawClippedLabel(DrawingContext dc, Rect rect, string text, Typeface tf, double fontSize, double dpi, bool topOnly)
    {
        var ft = new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            System.Windows.FlowDirection.LeftToRight,
            tf,
            fontSize,
            new SolidColorBrush(System.Windows.Media.Color.FromArgb(240, 250, 250, 250)),
            dpi)
        {
            MaxTextWidth = Math.Max(8, rect.Width - 10),
            MaxTextHeight = topOnly ? 16 : Math.Max(14, rect.Height - 6),
            Trimming = TextTrimming.CharacterEllipsis
        };

        dc.PushClip(new RectangleGeometry(rect));
        dc.DrawText(ft, new System.Windows.Point(rect.X + 5, rect.Y + 2));
        dc.Pop();
    }

    private static System.Windows.Media.Color GetColorForNode(DiskNode node, bool isHeader)
    {
        if (node.IsSummaryPlaceholder)
            return System.Windows.Media.Color.FromRgb(39, 39, 42);
        if (node.IsDirectory)
        {
            if (isHeader) return System.Windows.Media.Color.FromRgb(24, 24, 30);
            if (node.PercentOfParent >= 30) return System.Windows.Media.Color.FromRgb(153, 27, 27);   // Red-800
            if (node.PercentOfParent >= 15) return System.Windows.Media.Color.FromRgb(146, 64, 14);   // Amber-800
            return System.Windows.Media.Color.FromRgb(30, 58, 138);                                  // Blue-900
        }

        string ext = node.Extension.ToLowerInvariant();
        if (ext is ".mp4" or ".mkv" or ".avi" or ".mov" or ".webm")
            return System.Windows.Media.Color.FromRgb(107, 33, 168); // Purple-800
        if (ext is ".pak" or ".ucas" or ".utoc" or ".iso" or ".zip" or ".rar" or ".7z")
            return System.Windows.Media.Color.FromRgb(7, 89, 133);   // Sky-800
        if (ext is ".exe" or ".dll" or ".msi" or ".sys" or ".dmp")
            return System.Windows.Media.Color.FromRgb(161, 98, 7);   // Yellow-700
        return System.Windows.Media.Color.FromRgb(21, 94, 117);      // Cyan-800
    }

    private static List<(DiskNode Node, Rect Rect)> ComputeSquarifiedLayout(IList<DiskNode> items, Rect container)
    {
        var output = new List<(DiskNode, Rect)>(items.Count);
        if (items.Count == 0 || container.Width <= 1 || container.Height <= 1)
            return output;

        long totalBytes = items.Sum(x => x.SizeBytes);
        if (totalBytes <= 0) return output;

        double totalArea = container.Width * container.Height;
        var areas = items.Select(x => (Node: x, Area: x.SizeBytes * totalArea / totalBytes)).ToList();

        Squarify(areas, new List<(DiskNode, double)>(), container, output);
        return output;
    }

    private static void Squarify(
        List<(DiskNode Node, double Area)> remaining,
        List<(DiskNode Node, double Area)> row,
        Rect freeRect,
        List<(DiskNode, Rect)> output)
    {
        if (remaining.Count == 0)
        {
            LayoutRow(row, freeRect, output);
            return;
        }

        var next = remaining[0];
        double shortSide = Math.Min(freeRect.Width, freeRect.Height);

        if (row.Count == 0 || WorstAspectRatio(row, shortSide) >= WorstAspectRatioWith(row, next, shortSide))
        {
            row.Add(next);
            remaining.RemoveAt(0);
            Squarify(remaining, row, freeRect, output);
        }
        else
        {
            var newFree = LayoutRow(row, freeRect, output);
            Squarify(remaining, new List<(DiskNode, double)>(), newFree, output);
        }
    }

    private static double WorstAspectRatio(List<(DiskNode Node, double Area)> row, double side)
    {
        if (row.Count == 0 || side <= 0) return double.MaxValue;
        double sum = row.Sum(r => r.Area);
        double max = row.Max(r => r.Area);
        double min = row.Min(r => r.Area);
        if (min <= 0 || sum <= 0) return double.MaxValue;
        double s2 = side * side;
        double sum2 = sum * sum;
        return Math.Max((s2 * max) / sum2, sum2 / (s2 * min));
    }

    private static double WorstAspectRatioWith(List<(DiskNode Node, double Area)> row, (DiskNode Node, double Area) candidate, double side)
    {
        var temp = new List<(DiskNode, double)>(row) { candidate };
        return WorstAspectRatio(temp, side);
    }

    private static Rect LayoutRow(List<(DiskNode Node, double Area)> row, Rect rect, List<(DiskNode, Rect)> output)
    {
        if (row.Count == 0) return rect;
        double sumArea = row.Sum(r => r.Area);
        if (sumArea <= 0) return rect;

        if (rect.Width >= rect.Height)
        {
            double stripWidth = Math.Min(rect.Width, sumArea / Math.Max(1.0, rect.Height));
            double y = rect.Y;
            foreach (var (node, area) in row)
            {
                double h = stripWidth > 0 ? area / stripWidth : 0;
                output.Add((node, new Rect(rect.X, y, Math.Max(0, stripWidth), Math.Max(0, h))));
                y += h;
            }
            return new Rect(rect.X + stripWidth, rect.Y, Math.Max(0, rect.Width - stripWidth), rect.Height);
        }
        else
        {
            double stripHeight = Math.Min(rect.Height, sumArea / Math.Max(1.0, rect.Width));
            double x = rect.X;
            foreach (var (node, area) in row)
            {
                double w = stripHeight > 0 ? area / stripHeight : 0;
                output.Add((node, new Rect(x, rect.Y, Math.Max(0, w), Math.Max(0, stripHeight))));
                x += w;
            }
            return new Rect(rect.X, rect.Y + stripHeight, rect.Width, Math.Max(0, rect.Height - stripHeight));
        }
    }

    private TreemapBlock? HitTestBlock(System.Windows.Point pt)
    {
        for (int i = _blocks.Count - 1; i >= 0; i--)
        {
            if (_blocks[i].Bounds.Contains(pt))
                return _blocks[i];
        }
        return null;
    }

    protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var hit = HitTestBlock(e.GetPosition(this));
        if (!ReferenceEquals(hit?.Node, _hoveredNode))
        {
            _hoveredNode = hit?.Node;
            if (_hoveredNode != null)
            {
                _tip.Content = $"{_hoveredNode.Name}\nBoyut: {_hoveredNode.SizeFormatted} ({_hoveredNode.PercentText})\nYol: {_hoveredNode.FullPath}\n(Çift tıkla: Klasörün içine odaklan)";
                _tip.IsOpen = true;
            }
            else
            {
                _tip.IsOpen = false;
            }
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeave(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hoveredNode = null;
        _tip.IsOpen = false;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        var hit = HitTestBlock(e.GetPosition(this));
        if (hit == null) return;

        SelectedNode = hit.Node;
        InvalidateVisual();

        if (e.ClickCount == 2 && !hit.Node.IsSummaryPlaceholder)
            NodeDoubleClicked?.Invoke(hit.Node);
    }
}
