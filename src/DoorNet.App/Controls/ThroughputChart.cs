using System.Globalization;
using System.Windows;
using System.Windows.Media;
using DoorNet.Core.Formatting;
using DoorNet.Core.Models;

namespace DoorNet.App.Controls;

/// <summary>
/// Dependency-free dual-series throughput chart. Draws a filled download area, an upload
/// line and a byte-scaled grid in a single <see cref="OnRender"/> pass, so a 1 Hz update
/// costs no visual tree churn.
/// </summary>
public sealed class ThroughputChart : FrameworkElement
{
    public static readonly DependencyProperty PointsProperty = DependencyProperty.Register(
        nameof(Points),
        typeof(IReadOnlyList<ThroughputPoint>),
        typeof(ThroughputChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CapacityProperty = DependencyProperty.Register(
        nameof(Capacity),
        typeof(int),
        typeof(ThroughputChart),
        new FrameworkPropertyMetadata(90, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty DownloadColorProperty = DependencyProperty.Register(
        nameof(DownloadColor),
        typeof(Color),
        typeof(ThroughputChart),
        new FrameworkPropertyMetadata(Color.FromRgb(0x35, 0xE0, 0x8E), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty UploadColorProperty = DependencyProperty.Register(
        nameof(UploadColor),
        typeof(Color),
        typeof(ThroughputChart),
        new FrameworkPropertyMetadata(Color.FromRgb(0x4C, 0x9B, 0xFF), FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Typeface LabelTypeface = new("Segoe UI");

    public IReadOnlyList<ThroughputPoint>? Points
    {
        get => (IReadOnlyList<ThroughputPoint>?)GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    public int Capacity
    {
        get => (int)GetValue(CapacityProperty);
        set => SetValue(CapacityProperty, value);
    }

    public Color DownloadColor
    {
        get => (Color)GetValue(DownloadColorProperty);
        set => SetValue(DownloadColorProperty, value);
    }

    public Color UploadColor
    {
        get => (Color)GetValue(UploadColorProperty);
        set => SetValue(UploadColorProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 8 || height <= 8)
            return;

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        const double gutter = 62;
        const double rightPad = 10;
        const double topPad = 10;
        const double bottomPad = 20;

        var plot = new Rect(
            gutter,
            topPad,
            Math.Max(1, width - gutter - rightPad),
            Math.Max(1, height - topPad - bottomPad));

        var points = Points;
        var peak = 0d;
        if (points is { Count: > 0 })
        {
            foreach (var point in points)
                peak = Math.Max(peak, Math.Max(point.DownloadBytesPerSecond, point.UploadBytesPerSecond));
        }

        var axisMax = ChartMath.NiceMax(peak);
        DrawGrid(dc, plot, axisMax, dpi);

        if (points is not { Count: > 1 })
            return;

        var capacity = Math.Max(2, Capacity);
        var step = plot.Width / (capacity - 1);

        double X(int index) => plot.Right - ((points.Count - 1 - index) * step);
        double Y(double value) => plot.Bottom - (Math.Clamp(value / axisMax, 0, 1) * plot.Height);

        dc.PushClip(new RectangleGeometry(plot));

        var downloadGeometry = new StreamGeometry();
        using (var ctx = downloadGeometry.Open())
        {
            ctx.BeginFigure(new Point(X(0), plot.Bottom), isFilled: true, isClosed: true);
            for (var i = 0; i < points.Count; i++)
                ctx.LineTo(new Point(X(i), Y(points[i].DownloadBytesPerSecond)), isStroked: true, isSmoothJoin: false);
            ctx.LineTo(new Point(X(points.Count - 1), plot.Bottom), isStroked: true, isSmoothJoin: false);
        }

        downloadGeometry.Freeze();

        var areaBrush = new LinearGradientBrush(
            Color.FromArgb(0x55, DownloadColor.R, DownloadColor.G, DownloadColor.B),
            Color.FromArgb(0x00, DownloadColor.R, DownloadColor.G, DownloadColor.B),
            new Point(0, 0),
            new Point(0, 1));
        areaBrush.Freeze();

        dc.DrawGeometry(areaBrush, null, downloadGeometry);
        dc.DrawGeometry(null, CreateLinePen(DownloadColor, 2.0), BuildLine(points, X, Y, p => p.DownloadBytesPerSecond));
        dc.DrawGeometry(null, CreateLinePen(UploadColor, 1.6), BuildLine(points, X, Y, p => p.UploadBytesPerSecond));

        DrawEndpoint(dc, new Point(X(points.Count - 1), Y(points[^1].DownloadBytesPerSecond)), DownloadColor);
        DrawEndpoint(dc, new Point(X(points.Count - 1), Y(points[^1].UploadBytesPerSecond)), UploadColor);

        dc.Pop();
    }

    private static void DrawGrid(DrawingContext dc, Rect plot, double axisMax, double dpi)
    {
        var gridPen = new Pen(new SolidColorBrush(Color.FromRgb(0x1B, 0x27, 0x33)), 1);
        gridPen.Freeze();

        var labelBrush = new SolidColorBrush(Color.FromRgb(0x5A, 0x6C, 0x7E));
        labelBrush.Freeze();

        for (var i = 0; i <= 4; i++)
        {
            var y = plot.Bottom - (i * plot.Height / 4);
            dc.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));

            var (value, unit) = ByteFormatter.SplitRate(axisMax * i / 4);
            var text = new FormattedText(
                $"{value} {unit}",
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                LabelTypeface,
                9.5,
                labelBrush,
                dpi);

            dc.DrawText(text, new Point(plot.Left - 8 - text.Width, y - (text.Height / 2)));
        }
    }

    private static StreamGeometry BuildLine(
        IReadOnlyList<ThroughputPoint> points,
        Func<int, double> x,
        Func<double, double> y,
        Func<ThroughputPoint, double> select)
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(x(0), y(select(points[0]))), isFilled: false, isClosed: false);
            for (var i = 1; i < points.Count; i++)
                ctx.LineTo(new Point(x(i), y(select(points[i]))), isStroked: true, isSmoothJoin: true);
        }

        geometry.Freeze();
        return geometry;
    }

    private static Pen CreateLinePen(Color color, double thickness)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();

        var pen = new Pen(brush, thickness)
        {
            LineJoin = PenLineJoin.Round,
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };
        pen.Freeze();
        return pen;
    }

    private static void DrawEndpoint(DrawingContext dc, Point center, Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        dc.DrawEllipse(brush, null, center, 3, 3);
    }
}
