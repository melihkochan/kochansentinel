using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace KochanSentinel.App.Controls;

/// <summary>
/// Lightweight hardware-accelerated OnRender sparkline chart with soft gradient area fill.
/// </summary>
public sealed class SparklineControl : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IEnumerable<double>), typeof(SparklineControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnValuesChanged));

    public static readonly DependencyProperty StrokeBrushProperty = DependencyProperty.Register(
        nameof(StrokeBrush), typeof(System.Windows.Media.Brush), typeof(SparklineControl),
        new FrameworkPropertyMetadata(System.Windows.Media.Brushes.DeepSkyBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaxValueProperty = DependencyProperty.Register(
        nameof(MaxValue), typeof(double), typeof(SparklineControl),
        new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AutoScaleProperty = DependencyProperty.Register(
        nameof(AutoScale), typeof(bool), typeof(SparklineControl),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
        nameof(StrokeThickness), typeof(double), typeof(SparklineControl),
        new FrameworkPropertyMetadata(1.75, FrameworkPropertyMetadataOptions.AffectsRender));

    private INotifyCollectionChanged? _subscribedCollection;

    public IEnumerable<double>? Values
    {
        get => (IEnumerable<double>?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public System.Windows.Media.Brush StrokeBrush
    {
        get => (System.Windows.Media.Brush)GetValue(StrokeBrushProperty);
        set => SetValue(StrokeBrushProperty, value);
    }

    public double MaxValue
    {
        get => (double)GetValue(MaxValueProperty);
        set => SetValue(MaxValueProperty, value);
    }

    public bool AutoScale
    {
        get => (bool)GetValue(AutoScaleProperty);
        set => SetValue(AutoScaleProperty, value);
    }

    public double StrokeThickness
    {
        get => (double)GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    private static void OnValuesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not SparklineControl ctrl) return;
        if (ctrl._subscribedCollection != null)
        {
            ctrl._subscribedCollection.CollectionChanged -= ctrl.OnCollectionChanged;
            ctrl._subscribedCollection = null;
        }
        if (e.NewValue is INotifyCollectionChanged ncc)
        {
            ctrl._subscribedCollection = ncc;
            ncc.CollectionChanged += ctrl.OnCollectionChanged;
        }
        ctrl.InvalidateVisual();
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth;
        double h = ActualHeight;
        if (w <= 4 || h <= 4 || Values == null) return;

        var pts = Values as IList<double> ?? Values.ToList();
        if (pts.Count < 2) return;

        double ceiling = MaxValue > 0 ? MaxValue : 100.0;
        if (AutoScale)
        {
            double peak = 0;
            for (int i = 0; i < pts.Count; i++)
                if (pts[i] > peak) peak = pts[i];
            ceiling = Math.Max(1.0, peak * 1.2);
        }

        double stepX = w / (pts.Count - 1);
        double padY = 2.0;
        double usableH = Math.Max(2.0, h - padY * 2);

        var lineGeo = new StreamGeometry();
        var fillGeo = new StreamGeometry();

        using (var lineCtx = lineGeo.Open())
        using (var fillCtx = fillGeo.Open())
        {
            for (int i = 0; i < pts.Count; i++)
            {
                double norm = Math.Clamp(pts[i] / ceiling, 0.0, 1.0);
                double x = i * stepX;
                double y = padY + (1.0 - norm) * usableH;

                var pt = new System.Windows.Point(x, y);
                if (i == 0)
                {
                    lineCtx.BeginFigure(pt, false, false);
                    fillCtx.BeginFigure(new System.Windows.Point(0, h), true, true);
                    fillCtx.LineTo(pt, true, true);
                }
                else
                {
                    lineCtx.LineTo(pt, true, true);
                    fillCtx.LineTo(pt, true, true);
                }
            }
            fillCtx.LineTo(new System.Windows.Point(w, h), true, true);
        }

        lineGeo.Freeze();
        fillGeo.Freeze();

        var baseColor = StrokeBrush is SolidColorBrush scb ? scb.Color : System.Windows.Media.Color.FromRgb(56, 189, 248);
        var grad = new LinearGradientBrush(
            System.Windows.Media.Color.FromArgb(72, baseColor.R, baseColor.G, baseColor.B),
            System.Windows.Media.Color.FromArgb(4, baseColor.R, baseColor.G, baseColor.B),
            90.0);
        grad.Freeze();

        dc.DrawGeometry(grad, null, fillGeo);

        var pen = new System.Windows.Media.Pen(StrokeBrush, StrokeThickness)
        {
            LineJoin = PenLineJoin.Round,
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };
        pen.Freeze();
        dc.DrawGeometry(null, pen, lineGeo);
    }
}
