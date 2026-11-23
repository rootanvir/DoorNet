using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using DoorNet.App.Platform;

namespace DoorNet.App.Controls;

/// <summary>
/// Equirectangular world map with geolocated markers: green for the local machine, red for
/// remote servers. Ocean, land and graticule are drawn with theme brushes; hovering a dot
/// shows a small callout with the place and address.
/// </summary>
public sealed class WorldMapView : FrameworkElement
{
    public static readonly DependencyProperty MarkersProperty = DependencyProperty.Register(
        nameof(Markers),
        typeof(IReadOnlyList<GeoMarker>),
        typeof(WorldMapView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LandBrushProperty = DependencyProperty.Register(
        nameof(LandBrush),
        typeof(Brush),
        typeof(WorldMapView),
        new FrameworkPropertyMetadata(Frozen("#25452F"), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty OceanBrushProperty = DependencyProperty.Register(
        nameof(OceanBrush),
        typeof(Brush),
        typeof(WorldMapView),
        new FrameworkPropertyMetadata(Frozen("#0C1A26"), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty RemoteBrushProperty = DependencyProperty.Register(
        nameof(RemoteBrush),
        typeof(Brush),
        typeof(WorldMapView),
        new FrameworkPropertyMetadata(Frozen("#F87171"), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LocalBrushProperty = DependencyProperty.Register(
        nameof(LocalBrush),
        typeof(Brush),
        typeof(WorldMapView),
        new FrameworkPropertyMetadata(Frozen("#35E08E"), FrameworkPropertyMetadataOptions.AffectsRender));

    // Land geometry is authored against this fixed band; the renderer stretches it onto
    // whatever latitude range the card's aspect ratio calls for.
    private const double FixedLatTop = 85;
    private const double FixedLatBottom = -60;

    private static readonly Geometry Land = LoadLand();
    private static readonly Pen CoastPen = CreateCoastPen();
    private static readonly Typeface TitleTypeface =
        new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Typeface BodyTypeface = new("Segoe UI");

    private readonly List<(Point Position, GeoMarker Marker)> _dots = new();
    private int _hoverIndex = -1;

    public IReadOnlyList<GeoMarker>? Markers
    {
        get => (IReadOnlyList<GeoMarker>?)GetValue(MarkersProperty);
        set => SetValue(MarkersProperty, value);
    }

    public Brush LandBrush
    {
        get => (Brush)GetValue(LandBrushProperty);
        set => SetValue(LandBrushProperty, value);
    }

    public Brush OceanBrush
    {
        get => (Brush)GetValue(OceanBrushProperty);
        set => SetValue(OceanBrushProperty, value);
    }

    public Brush RemoteBrush
    {
        get => (Brush)GetValue(RemoteBrushProperty);
        set => SetValue(RemoteBrushProperty, value);
    }

    public Brush LocalBrush
    {
        get => (Brush)GetValue(LocalBrushProperty);
        set => SetValue(LocalBrushProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 8 || height <= 8)
            return;

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        // Keep the world's 360:145 band intact and centre it, so the card carries the same
        // margins on the left and right that the rest of the dashboard uses.
        var mapWidth = Math.Min(width, height * (360.0 / (FixedLatTop - FixedLatBottom)));
        var mapHeight = mapWidth * ((FixedLatTop - FixedLatBottom) / 360.0);
        var left = (width - mapWidth) / 2;
        var top = (height - mapHeight) / 2;
        var mapRect = new Rect(left, top, mapWidth, mapHeight);

        dc.DrawRoundedRectangle(OceanBrush, null, mapRect, 8, 8);

        dc.PushClip(new RectangleGeometry(mapRect, 8, 8));
        DrawGraticule(dc, mapRect, FixedLatTop, FixedLatBottom);

        dc.PushTransform(new TranslateTransform(left, top));
        dc.PushTransform(new ScaleTransform(mapWidth, mapHeight));
        dc.DrawGeometry(LandBrush, CoastPen, Land);
        dc.Pop();
        dc.Pop();
        dc.Pop();

        _dots.Clear();
        var markers = Markers;
        if (markers is { Count: > 0 })
        {
            foreach (var marker in markers)
            {
                var x = left + ((marker.Longitude + 180) / 360 * mapWidth);
                var y = top + ((FixedLatTop - Math.Clamp(marker.Latitude, FixedLatBottom, FixedLatTop)) / (FixedLatTop - FixedLatBottom) * mapHeight);
                var position = new Point(x, y);

                if (marker.IsLocal)
                {
                    var haloColor = SolidColorOf(LocalBrush);
                    var halo = new SolidColorBrush(Color.FromArgb(56, haloColor.R, haloColor.G, haloColor.B));
                    halo.Freeze();
                    dc.DrawEllipse(halo, null, position, 9, 9);
                    dc.DrawEllipse(LocalBrush, null, position, 4.2, 4.2);
                }
                else
                {
                    dc.DrawEllipse(RemoteBrush, null, position, 2.8, 2.8);
                }

                _dots.Add((position, marker));
            }
        }

        if (_hoverIndex >= 0 && _hoverIndex < _dots.Count)
            DrawCallout(dc, _dots[_hoverIndex], width, height, dpi);
    }

    private static void DrawGraticule(DrawingContext dc, Rect mapRect, double latTop, double latBottom)
    {
        var grid = new SolidColorBrush(Color.FromArgb(0x38, 0x9F, 0xB6, 0xC9));
        grid.Freeze();
        var pen = new Pen(grid, 0.6);
        pen.Freeze();

        for (var lon = -150; lon <= 150; lon += 30)
        {
            var x = mapRect.Left + ((lon + 180) / 360.0 * mapRect.Width);
            dc.DrawLine(pen, new Point(x, mapRect.Top), new Point(x, mapRect.Bottom));
        }

        for (var lat = -60; lat <= 60; lat += 30)
        {
            if (lat < latBottom || lat > latTop)
                continue;

            var y = mapRect.Top + ((latTop - lat) / (latTop - latBottom) * mapRect.Height);
            dc.DrawLine(pen, new Point(mapRect.Left, y), new Point(mapRect.Right, y));
        }
    }

    private void DrawCallout(DrawingContext dc, (Point Position, GeoMarker Marker) dot, double width, double height, double dpi)
    {
        var (title, subtitle) = Describe(dot.Marker);

        var titleBrush = Frozen("#F2F7FB");
        var bodyBrush = Frozen("#AEBECD");
        var background = Frozen("#F2141E28");
        var border = new Pen(Frozen("#40FFFFFF"), 1);
        border.Freeze();

        var titleText = new FormattedText(title, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            TitleTypeface, 11, titleBrush, dpi);
        var bodyText = new FormattedText(subtitle, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            BodyTypeface, 10, bodyBrush, dpi);

        var padding = 9.0;
        var boxWidth = Math.Min(Math.Max(titleText.Width, bodyText.Width) + (padding * 2), width - 12);
        var boxHeight = titleText.Height + bodyText.Height + (padding * 1.5);

        titleText.MaxTextWidth = boxWidth - (padding * 2);
        titleText.MaxLineCount = 1;
        titleText.Trimming = TextTrimming.CharacterEllipsis;
        bodyText.MaxTextWidth = boxWidth - (padding * 2);
        bodyText.MaxLineCount = 1;
        bodyText.Trimming = TextTrimming.CharacterEllipsis;

        var x = Math.Clamp(dot.Position.X - (boxWidth / 2), 6, width - boxWidth - 6);
        var y = dot.Position.Y - boxHeight - 10;
        if (y < 6)
            y = dot.Position.Y + 12;

        var box = new Rect(x, y, boxWidth, boxHeight);
        dc.DrawRoundedRectangle(background, border, box, 6, 6);
        dc.DrawText(titleText, new Point(x + padding, y + (padding * 0.65)));
        dc.DrawText(bodyText, new Point(x + padding, y + (padding * 0.65) + titleText.Height));
    }

    private static (string Title, string Subtitle) Describe(GeoMarker marker)
    {
        if (marker.IsLocal)
            return ("This machine", "Your public IP location");

        var city = marker.City;
        var country = marker.Country;

        var title = !string.IsNullOrWhiteSpace(city)
            ? (string.IsNullOrWhiteSpace(country) ? city! : $"{city}, {country}")
            : !string.IsNullOrWhiteSpace(country) ? country! : "Remote server";

        return (title, marker.Label);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var position = e.GetPosition(this);
        var index = -1;

        for (var i = 0; i < _dots.Count; i++)
        {
            var dot = _dots[i].Position;
            if (Math.Abs(dot.X - position.X) <= 7 && Math.Abs(dot.Y - position.Y) <= 7)
            {
                index = i;
                break;
            }
        }

        if (index != _hoverIndex)
        {
            _hoverIndex = index;
            Cursor = index >= 0 ? Cursors.Hand : null;
            InvalidateVisual();
        }

        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        if (_hoverIndex != -1)
        {
            _hoverIndex = -1;
            Cursor = null;
            InvalidateVisual();
        }

        base.OnMouseLeave(e);
    }

    private static Geometry LoadLand()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "world-land.geojson");
        if (!File.Exists(path))
            return Geometry.Empty;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var geometry = new StreamGeometry { FillRule = FillRule.Nonzero };

            using (var context = geometry.Open())
            {
                foreach (var feature in document.RootElement.GetProperty("features").EnumerateArray())
                {
                    var shape = feature.GetProperty("geometry");
                    var type = shape.GetProperty("type").GetString();
                    var coordinates = shape.GetProperty("coordinates");

                    if (string.Equals(type, "Polygon", StringComparison.Ordinal))
                    {
                        AddRing(context, coordinates[0]);
                    }
                    else if (string.Equals(type, "MultiPolygon", StringComparison.Ordinal))
                    {
                        foreach (var polygon in coordinates.EnumerateArray())
                            AddRing(context, polygon[0]);
                    }
                }
            }

            geometry.Freeze();
            return geometry;
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return Geometry.Empty;
        }
    }

    private static void AddRing(StreamGeometryContext context, JsonElement ring)
    {
        var first = true;

        foreach (var position in ring.EnumerateArray())
        {
            var lon = position[0].GetDouble();
            var lat = position[1].GetDouble();
            var point = new Point((lon + 180) / 360, (FixedLatTop - lat) / (FixedLatTop - FixedLatBottom));

            if (first)
            {
                context.BeginFigure(point, isFilled: true, isClosed: true);
                first = false;
            }
            else
            {
                context.LineTo(point, isStroked: false, isSmoothJoin: false);
            }
        }
    }

    private static Pen CreateCoastPen()
    {
        var brush = new SolidColorBrush(Color.FromArgb(0x30, 0x0B, 0x22, 0x16));
        brush.Freeze();
        var pen = new Pen(brush, 0.6);
        pen.Freeze();
        return pen;
    }

    private static Color SolidColorOf(Brush brush) =>
        brush is SolidColorBrush solid ? solid.Color : Colors.Transparent;

    private static Brush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
