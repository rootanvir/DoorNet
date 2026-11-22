using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using DoorNet.App.Platform;

namespace DoorNet.App.Controls;

/// <summary>
/// Equirectangular world map with geolocated markers: green for the local machine, red for
/// remote servers. Land outlines come from Natural Earth 110m (public domain), projected
/// once into a normalised 0..1 geometry and drawn with theme-aware brushes.
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
        new FrameworkPropertyMetadata(Frozen("#22303D"), FrameworkPropertyMetadataOptions.AffectsRender));

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

    private const double LatTop = 85;
    private const double LatBottom = -60;

    private static readonly Geometry Land = LoadLand();

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

        var mapWidth = Math.Min(width, height * (360.0 / (LatTop - LatBottom)));
        var mapHeight = mapWidth * ((LatTop - LatBottom) / 360.0);
        var left = (width - mapWidth) / 2;
        var top = (height - mapHeight) / 2;

        dc.PushTransform(new TranslateTransform(left, top));
        dc.PushTransform(new ScaleTransform(mapWidth, mapHeight));
        dc.DrawGeometry(LandBrush, null, Land);
        dc.Pop();
        dc.Pop();

        var markers = Markers;
        if (markers is not { Count: > 0 })
            return;

        foreach (var marker in markers)
        {
            var x = left + ((marker.Longitude + 180) / 360 * mapWidth);
            var y = top + ((LatTop - Math.Clamp(marker.Latitude, LatBottom, LatTop)) / (LatTop - LatBottom) * mapHeight);

            if (marker.IsLocal)
            {
                var halo = new SolidColorBrush(((SolidColorBrush)LocalBrush).Color) { Opacity = 0.22 };
                halo.Freeze();
                dc.DrawEllipse(halo, null, new Point(x, y), 9, 9);
                dc.DrawEllipse(LocalBrush, null, new Point(x, y), 4.2, 4.2);
            }
            else
            {
                dc.DrawEllipse(RemoteBrush, null, new Point(x, y), 2.8, 2.8);
            }
        }
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
            var point = new Point((lon + 180) / 360, (LatTop - lat) / (LatTop - LatBottom));

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

    private static Brush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
