using System.Globalization;
using System.Windows;
using System.Windows.Media;
using DoorNet.Core.Formatting;
using DoorNet.Core.Models;

namespace DoorNet.App.Controls;

/// <summary>
/// Dependency-free dual-series throughput chart. Draws a filled download area, an upload
/// line and a byte-scaled grid in a single <see cref="OnRender"/> pass, so a 1 Hz update
/// costs no visual tree churn. Brushes are dependency properties so the theme can switch
/// without rebuilding the control.
/// </summary>
public sealed class ThroughputChart : FrameworkElement
{
    private static readonly Brush DefaultDownload = Frozen("#35E08E");
    private static readonly Brush DefaultUpload = Frozen("#4C9BFF");
    private static readonly Brush DefaultGrid = Frozen("#1B2733");
    private static readonly Brush DefaultLabel = Frozen("#5A6C7E");

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

    public static readonly DependencyProperty DownloadBrushProperty = DependencyProperty.Register(
        nameof(DownloadBrush),
        typeof(Brush),
        typeof(ThroughputChart),
        new FrameworkPropertyMetadata(DefaultDownload, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty UploadBrushProperty = DependencyProperty.Register(
        nameof(UploadBrush),
        typeof(Brush),
        typeof(ThroughputChart),
        new FrameworkPropertyMetadata(DefaultUpload, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty GridBrushProperty = DependencyProperty.Register(
        nameof(GridBrush),
        typeof(Brush),
        typeof(ThroughputChart),
        new FrameworkPropertyMetadata(DefaultGrid, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LabelBrushProperty = DependencyProperty.Register(
        nameof(LabelBrush),
        typeof(Brush),
        typeof(ThroughputChart),
        new FrameworkPropertyMetadata(DefaultLabel, FrameworkPropertyMetadataOptions.AffectsRender));

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

    public Brush DownloadBrush
    {
        get => (Brush)GetValue(DownloadBrushProperty);
        set => SetValue(DownloadBrushProperty, value);
    }

    public Brush UploadBrush
    {
        get => (Brush)GetValue(UploadBrushProperty);
        set => SetValue(UploadBrushProperty, value);
    }

    public Brush GridBrush
    {
        get => (Brush)GetValue(GridBrushProperty);
        set => SetValue(GridBrushProperty, value);
    }

    public Brush LabelBrush
    {
        get => (Brush)GetValue(LabelBrushProperty);
        set => SetValue(LabelBrushProperty, value);
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

        var downloadPoints = BuildSeries(points, X, Y, p => p.DownloadBytesPerSecond);
        var uploadPoints = BuildSeries(points, X, Y, p => p.UploadBytesPerSecond);

        var downloadGeometry = new StreamGeometry();
        using (var ctx = downloadGeometry.Open())
        {
            ctx.BeginFigure(new Point(downloadPoints[0].X, plot.Bottom), isFilled: true, isClosed: true);
            ctx.LineTo(downloadPoints[0], isStroked: true, isSmoothJoin: false);
            AppendSmoothPath(ctx, downloadPoints);
            ctx.LineTo(new Point(downloadPoints[^1].X, plot.Bottom), isStroked: true, isSmoothJoin: false);
        }

        downloadGeometry.Freeze();

        var downloadColor = SolidColorOf(DownloadBrush, "#35E08E");
        var areaBrush = new LinearGradientBrush(
            Color.FromArgb(0x55, downloadColor.R, downloadColor.G, downloadColor.B),
            Color.FromArgb(0x00, downloadColor.R, downloadColor.G, downloadColor.B),
            new Point(0, 0),
            new Point(0, 1));
        areaBrush.Freeze();

        dc.DrawGeometry(areaBrush, null, downloadGeometry);
        dc.DrawGeometry(null, CreateLinePen(DownloadBrush, 2.0), BuildSmoothLine(downloadPoints));
        dc.DrawGeometry(null, CreateLinePen(UploadBrush, 1.6), BuildSmoothLine(uploadPoints));

        DrawEndpoint(dc, new Point(X(points.Count - 1), Y(points[^1].DownloadBytesPerSecond)), DownloadBrush);
        DrawEndpoint(dc, new Point(X(points.Count - 1), Y(points[^1].UploadBytesPerSecond)), UploadBrush);

        dc.Pop();
    }

    private void DrawGrid(DrawingContext dc, Rect plot, double axisMax, double dpi)
    {
        var gridPen = new Pen(GridBrush, 1);
        gridPen.Freeze();

        var labelBrush = LabelBrush;

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

    private static List<Point> BuildSeries(
        IReadOnlyList<ThroughputPoint> points,
        Func<int, double> x,
        Func<double, double> y,
        Func<ThroughputPoint, double> select)
    {
        var series = new List<Point>(points.Count);

        for (var i = 0; i < points.Count; i++)
            series.Add(new Point(x(i), y(select(points[i]))));

        return series;
    }

    /// <summary>
    /// Catmull-Rom spline through the samples, emitted as cubic Beziers so a 1 s spike
    /// eases in and out instead of drawing a hard triangle.
    /// </summary>
    private static void AppendSmoothPath(StreamGeometryContext ctx, IReadOnlyList<Point> path)
    {
        for (var i = 0; i < path.Count - 1; i++)
        {
            var p0 = path[Math.Max(0, i - 1)];
            var p1 = path[i];
            var p2 = path[i + 1];
            var p3 = path[Math.Min(path.Count - 1, i + 2)];

            var c1 = new Point(p1.X + ((p2.X - p0.X) / 6.0), p1.Y + ((p2.Y - p0.Y) / 6.0));
            var c2 = new Point(p2.X - ((p3.X - p1.X) / 6.0), p2.Y - ((p3.Y - p1.Y) / 6.0));

            ctx.BezierTo(c1, c2, p2, isStroked: true, isSmoothJoin: true);
        }
    }

    private static StreamGeometry BuildSmoothLine(IReadOnlyList<Point> path)
    {
        var geometry = new StreamGeometry();

        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(path[0], isFilled: false, isClosed: false);
            AppendSmoothPath(ctx, path);
        }

        geometry.Freeze();
        return geometry;
    }

    private static Pen CreateLinePen(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness)
        {
            LineJoin = PenLineJoin.Round,
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };
        pen.Freeze();
        return pen;
    }

    private static void DrawEndpoint(DrawingContext dc, Point center, Brush brush) =>
        dc.DrawEllipse(brush, null, center, 3, 3);

    private static Color SolidColorOf(Brush brush, string fallback)
    {
        if (brush is SolidColorBrush solid)
            return solid.Color;

        return (Color)ColorConverter.ConvertFromString(fallback);
    }

    private static Brush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
