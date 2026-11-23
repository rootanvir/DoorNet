using System.Collections.ObjectModel;
using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using DoorNet.App.Collections;
using DoorNet.App.Platform;
using DoorNet.Capture;
using DoorNet.Core.Abstractions;
using DoorNet.Core.Aggregation;
using DoorNet.Core.Capture;
using DoorNet.Core.Collections;
using DoorNet.Core.Formatting;
using DoorNet.Core.Firewall;
using DoorNet.Core.Models;
namespace DoorNet.App.ViewModels;

/// <summary>
/// Drives the dashboard: one 1 Hz timer, one sampler, and two row collections that are
/// synchronised in place so the UI never flickers.
/// </summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    private const int HistoryCapacity = 90;
    private const int TabPageSize = 10;
    private const int MaxConnectionRows = 400;

    private readonly IThroughputSource _throughputSource;
    private readonly IConnectionSource _connectionSource;
    private readonly IPerAppUsageSource _perAppSource;
    private readonly IFirewallController _firewall;
    private readonly ThroughputSampler _sampler = new();
    private readonly RingBuffer<ThroughputPoint> _history = new(HistoryCapacity);
    private readonly UsageTotals _usageTotals = new();
    private readonly HashSet<string> _expanded = new(StringComparer.Ordinal);
    private readonly GeoLocator _geo = new();
    private readonly Dictionary<string, GeoPoint> _remoteGeo = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _geoPending = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _geoFailed = new(StringComparer.OrdinalIgnoreCase);
    private GeoPoint? _selfGeo;
    private IReadOnlyList<GeoMarker> _mapMarkers = Array.Empty<GeoMarker>();
    private IReadOnlyDictionary<string, BrowserTabInfo> _browserTabs =
        new Dictionary<string, BrowserTabInfo>(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _tabsExpanded = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _tabPages = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _tabProbeCts = new();
    private readonly DispatcherTimer _timer;

    private bool _tickRunning;
    private bool _isCapturing = true;
    private bool _disposed;
    private bool _isNetworkIdle = true;
    private AppTheme _theme = AppTheme.Dark;
    private double _peakDown;
    private double _peakUp;
    private double _intervalSeconds = 1;
    private DateTimeOffset _lastTickAt = DateTimeOffset.Now;

    private string _downValue = "0";
    private string _downUnit = "B/s";
    private string _upValue = "0";
    private string _upUnit = "B/s";
    private string _totalValue = "0";
    private string _totalUnit = "B";
    private string _downPeakText = "peak 0 B/s";
    private string _upPeakText = "peak 0 B/s";
    private string _sessionSplitText = "↓ 0 B   ↑ 0 B";
    private string _adapterSummary = "Detecting adapters…";
    private string _statusText = "Live";
    private string _captureButtonLabel = "Pause";
    private string? _lastError;
    private int _connectionCount;
    private int _appCount;
    private IReadOnlyList<ThroughputPoint> _chartPoints = Array.Empty<ThroughputPoint>();

    public MainViewModel()
        : this(
            CaptureStack.CreateThroughputSource(),
            CaptureStack.CreateConnectionSource(),
            CaptureStack.CreatePerAppUsageSource(),
            CaptureStack.CreateFirewallController())
    {
    }

    public MainViewModel(
        IThroughputSource throughputSource,
        IConnectionSource connectionSource,
        IPerAppUsageSource perAppSource,
        IFirewallController firewall)
    {
        _throughputSource = throughputSource;
        _connectionSource = connectionSource;
        _perAppSource = perAppSource;
        _firewall = firewall;

        ToggleCaptureCommand = new RelayCommand(ToggleCapture);
        ResetSessionCommand = new RelayCommand(ResetSession);
        RestartElevatedCommand = new RelayCommand(RestartElevated);
        _theme = ThemeManager.Current;
        ToggleThemeCommand = new RelayCommand(ToggleTheme);
        AllowDownloadCommand = new RelayCommand<object?>(row => SetDirection(row, FirewallDirection.In, block: false));
        BlockDownloadCommand = new RelayCommand<object?>(row => SetDirection(row, FirewallDirection.In, block: true));
        AllowUploadCommand = new RelayCommand<object?>(row => SetDirection(row, FirewallDirection.Out, block: false));
        BlockUploadCommand = new RelayCommand<object?>(row => SetDirection(row, FirewallDirection.Out, block: true));
        ToggleExpandCommand = new RelayCommand<object?>(ToggleExpand);
        ToggleTabsCommand = new RelayCommand<object?>(ToggleTabsSection);
        PreviousTabsCommand = new RelayCommand<object?>(row => ChangeTabPage(row, -1));
        NextTabsCommand = new RelayCommand<object?>(row => ChangeTabPage(row, 1));
        ToggleFirewallCommand = new RelayCommand(ToggleFirewall);

        CanOfferElevation = !Environment.IsPrivilegedProcess;

        ElevationNote = Environment.IsPrivilegedProcess
            ? "Elevated session · kernel ETW per-app bytes"
            : "Standard user · aggregate counters only";

        PerAppNote = _perAppSource.IsAvailable
            ? "Per-app rates and totals come from the kernel ETW session; totals reset with the session."
            : $"Per-app bytes unavailable: {_perAppSource.UnavailableReason ?? "unknown reason."}";

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _timer.Tick += (_, _) => _ = TickAsync();

        _timer.Start();
        _ = TickAsync();
        _ = LocateSelfAsync();
        _ = ProbeTabsLoopAsync(_tabProbeCts.Token);
    }

    public ObservableCollection<ConnectionGroupRow> Connections { get; } = [];
    public ObservableCollection<AppRow> Apps { get; } = [];

    public RelayCommand ToggleCaptureCommand { get; }
    public RelayCommand ResetSessionCommand { get; }
    public RelayCommand RestartElevatedCommand { get; }
    public RelayCommand ToggleThemeCommand { get; }
    public RelayCommand<object?> AllowDownloadCommand { get; }
    public RelayCommand<object?> BlockDownloadCommand { get; }
    public RelayCommand<object?> AllowUploadCommand { get; }
    public RelayCommand<object?> BlockUploadCommand { get; }
    public RelayCommand<object?> ToggleExpandCommand { get; }
    public RelayCommand<object?> ToggleTabsCommand { get; }
    public RelayCommand<object?> PreviousTabsCommand { get; }
    public RelayCommand<object?> NextTabsCommand { get; }
    public RelayCommand ToggleFirewallCommand { get; }

    /// <summary>Master switch for every DoorNet firewall rule.</summary>
    public bool IsFirewallEnabled => _firewall.IsEnabled;

    public string FirewallStateText => IsFirewallEnabled ? "Firewall ON" : "Firewall OFF";

    public string FirewallTooltip => IsFirewallEnabled
        ? "DoorNet firewall rules are active. Click to suspend all of them."
        : "DoorNet firewall rules are suspended. Click to enable them.";

    /// <summary>Raised after the palette changes so the window can restyle its native chrome.</summary>
    public event EventHandler? ThemeChanged;

    public string ThemeGlyph => _theme == AppTheme.Dark ? "\uE706" : "\uE708";

    public string ThemeTooltip => _theme == AppTheme.Dark ? "Switch to light theme" : "Switch to dark theme";

    /// <summary>True when the process is not elevated and can offer a UAC relaunch.</summary>
    public bool CanOfferElevation { get; }

    public string DownValue { get => _downValue; private set => SetProperty(ref _downValue, value); }
    public string DownUnit { get => _downUnit; private set => SetProperty(ref _downUnit, value); }
    public string UpValue { get => _upValue; private set => SetProperty(ref _upValue, value); }
    public string UpUnit { get => _upUnit; private set => SetProperty(ref _upUnit, value); }
    public string TotalValue { get => _totalValue; private set => SetProperty(ref _totalValue, value); }
    public string TotalUnit { get => _totalUnit; private set => SetProperty(ref _totalUnit, value); }
    public string DownPeakText { get => _downPeakText; private set => SetProperty(ref _downPeakText, value); }
    public string UpPeakText { get => _upPeakText; private set => SetProperty(ref _upPeakText, value); }
    public string SessionSplitText { get => _sessionSplitText; private set => SetProperty(ref _sessionSplitText, value); }
    public string AdapterSummary { get => _adapterSummary; private set => SetProperty(ref _adapterSummary, value); }
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    public string CaptureButtonLabel { get => _captureButtonLabel; private set => SetProperty(ref _captureButtonLabel, value); }
    public string ElevationNote { get; }
    public string PerAppNote { get; }

    /// <summary>True when the app is unelevated, so per-app meters can be enabled by UAC.</summary>
    public bool ShowEnableMeters => !_perAppSource.IsAvailable && !Environment.IsPrivilegedProcess;
    public string VersionText => "DoorNet 0.1 · skeleton";

    public int ConnectionCount { get => _connectionCount; private set => SetProperty(ref _connectionCount, value); }
    public int AppCount { get => _appCount; private set => SetProperty(ref _appCount, value); }

    public IReadOnlyList<ThroughputPoint> ChartPoints
    {
        get => _chartPoints;
        private set => SetProperty(ref _chartPoints, value);
    }

    public IReadOnlyList<GeoMarker> MapMarkers
    {
        get => _mapMarkers;
        private set => SetProperty(ref _mapMarkers, value);
    }

    public string RemoteHostCountText => $"{_remoteGeo.Count} located";

    public bool IsCapturing
    {
        get => _isCapturing;
        private set
        {
            if (SetProperty(ref _isCapturing, value))
                OnPropertyChanged(nameof(CaptureButtonLabel));
        }
    }

    /// <summary>True when both directions are at zero, so the tiles can show the idle tone.</summary>
    public bool IsNetworkIdle
    {
        get => _isNetworkIdle;
        private set => SetProperty(ref _isNetworkIdle, value);
    }

    public string? LastError
    {
        get => _lastError;
        private set
        {
            if (SetProperty(ref _lastError, value))
                OnPropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => !string.IsNullOrEmpty(_lastError);

    private async Task TickAsync()
    {
        if (_tickRunning || !_isCapturing || _disposed)
            return;

        _tickRunning = true;
        try
        {
            var now = DateTimeOffset.Now;
            _intervalSeconds = Math.Clamp((now - _lastTickAt).TotalSeconds, 0.25, 5);
            _lastTickAt = now;

            // Both calls touch the OS and can take tens of milliseconds on a busy machine;
            // keep them off the UI thread.
            var counters = await Task.Run(_throughputSource.ReadCounters);
            var connections = await Task.Run(_connectionSource.Snapshot);

            if (_disposed || !_isCapturing)
                return;

            var reading = _sampler.Sample(counters, now);
            ApplyReading(reading, now);
            ApplyConnections(connections);
            LastError = null;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            StatusText = "Error";
        }
        finally
        {
            _tickRunning = false;
        }
    }

    private void ApplyReading(ThroughputReading reading, DateTimeOffset now)
    {
        var (downValue, downUnit) = ByteFormatter.SplitRate(reading.DownloadBytesPerSecond);
        DownValue = downValue;
        DownUnit = downUnit;

        var (upValue, upUnit) = ByteFormatter.SplitRate(reading.UploadBytesPerSecond);
        UpValue = upValue;
        UpUnit = upUnit;

        IsNetworkIdle = reading.DownloadBytesPerSecond < 1 && reading.UploadBytesPerSecond < 1;

        var sessionBytes = reading.SessionBytesReceived + reading.SessionBytesSent;
        var (totalValue, totalUnit) = ByteFormatter.SplitBytes(sessionBytes);
        TotalValue = totalValue;
        TotalUnit = totalUnit;

        _peakDown = Math.Max(_peakDown, reading.DownloadBytesPerSecond);
        _peakUp = Math.Max(_peakUp, reading.UploadBytesPerSecond);
        DownPeakText = $"peak {ByteFormatter.FormatRate(_peakDown)}";
        UpPeakText = $"peak {ByteFormatter.FormatRate(_peakUp)}";

        SessionSplitText =
            $"↓ {ByteFormatter.FormatBytes(reading.SessionBytesReceived)}   ↑ {ByteFormatter.FormatBytes(reading.SessionBytesSent)}";

        AdapterSummary = reading.AdapterSummary;

        _history.Add(new ThroughputPoint(now, reading.DownloadBytesPerSecond, reading.UploadBytesPerSecond));
        ChartPoints = _history.ToArray();
    }

    private void ApplyConnections(IReadOnlyList<ConnectionInfo> connections)
    {
        ConnectionCount = connections.Count;

        var traffic = _perAppSource.Drain();
        var aggregated = AppUsageAggregator.Aggregate(connections, traffic);
        var showMeters = _perAppSource.IsAvailable;

        if (showMeters)
        {
            var now = DateTime.UtcNow;
            foreach (var app in aggregated)
                _usageTotals.Add(app, now);

            _usageTotals.Prune(now, TimeSpan.FromMinutes(10));
        }

        BuildConnectionGroups(connections);
        BuildAppGroups(aggregated, showMeters);
        QueueGeolocation(connections);
    }

    private void QueueGeolocation(IReadOnlyList<ConnectionInfo> connections)
    {
        if (_geoPending.Count >= 3)
            return;

        foreach (var connection in connections)
        {
            if (connection.RemotePort == 0)
                continue;

            var address = connection.RemoteAddress;
            if (string.IsNullOrEmpty(address)
                || _remoteGeo.ContainsKey(address)
                || _geoPending.Contains(address)
                || _geoFailed.Contains(address)
                || !GeoLocator.IsPublicAddress(address))
            {
                continue;
            }

            _geoPending.Add(address);
            _ = LocateRemoteAsync(address);

            if (_geoPending.Count >= 3)
                break;
        }
    }

    private async Task LocateRemoteAsync(string address)
    {
        try
        {
            var point = await _geo.LocateAsync(address).ConfigureAwait(false);

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (point is { } geo)
                    _remoteGeo[address] = geo;
                else
                    _geoFailed.Add(address);

                RebuildMarkers();
            });
        }
        catch (Exception)
        {
            _geoFailed.Add(address);
        }
        finally
        {
            _geoPending.Remove(address);
        }
    }

    private async Task LocateSelfAsync()
    {
        try
        {
            var point = await _geo.LocateSelfAsync().ConfigureAwait(false);
            if (point is not { } geo)
                return;

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                _selfGeo = geo;
                RebuildMarkers();
            });
        }
        catch (Exception)
        {
            // Geolocation is best-effort; the map simply shows no "mine" dot.
        }
    }

    private async Task ProbeTabsLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var tabs = await Task.Run(BrowserTabsProbe.Probe, cancellationToken).ConfigureAwait(false);

                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    _browserTabs = tabs;
                });
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception)
            {
                // UI Automation can fail for a browser window mid-navigation; try again later.
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(4), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void RebuildMarkers()
    {
        var markers = new List<GeoMarker>(_remoteGeo.Count + 1);

        if (_selfGeo is { } self)
            markers.Add(new GeoMarker(self.Latitude, self.Longitude, IsLocal: true, "Mine"));

        foreach (var (address, point) in _remoteGeo.Take(300))
            markers.Add(new GeoMarker(point.Latitude, point.Longitude, IsLocal: false, address));

        MapMarkers = markers;
        OnPropertyChanged(nameof(RemoteHostCountText));
    }

    private void BuildAppGroups(IReadOnlyList<AppUsage> aggregated, bool showMeters)
    {
        var summaries = aggregated
            .GroupBy(AppKey, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var apps = group.ToList();
                var (totalDown, totalUp) = GroupTotals(apps);

                return new AppSummary(
                    group.Key,
                    MostCommonName(apps),
                    apps.Select(a => a.ExecutablePath).FirstOrDefault(p => !string.IsNullOrEmpty(p)),
                    apps,
                    apps.Sum(a => a.BytesReceived) / _intervalSeconds,
                    apps.Sum(a => a.BytesSent) / _intervalSeconds,
                    totalDown,
                    totalUp,
                    apps.Sum(a => a.ConnectionCount),
                    apps.Sum(a => a.ActiveConnectionCount),
                    apps.Sum(a => a.RemoteHostCount));
            })
            .ToList();

        var maxDownload = summaries.Count == 0 ? 0 : summaries.Max(s => s.Download);
        var maxUpload = summaries.Count == 0 ? 0 : summaries.Max(s => s.Upload);

        var ordered = showMeters
            ? summaries
                .OrderByDescending(s => s.TotalDown + s.TotalUp)
                .ThenByDescending(s => s.Download + s.Upload)
            : summaries
                .OrderByDescending(s => s.Active)
                .ThenByDescending(s => s.Sockets);

        var apps = ordered
            .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .Select(s => ToAppRow(
                s,
                maxDownload,
                maxUpload,
                showMeters,
                _expanded.Contains("app|" + s.Key)))
            .ToList();

        RowSync.Apply(Apps, apps, r => r.GroupKey);
        AppCount = apps.Count;
    }

    private void BuildConnectionGroups(IReadOnlyList<ConnectionInfo> connections)
    {
        var groups = connections
            .Where(c => c.RemotePort != 0 || c.IsListening)
            .GroupBy(AppKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => new { Key = group.Key, Items = group.ToList() })
            .OrderByDescending(g => g.Items.Count)
            .ThenBy(g => g.Items[0].ProcessName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var rows = new List<ConnectionGroupRow>(groups.Count);

        foreach (var group in groups)
        {
            var first = group.Items[0];
            var established = group.Items.Count(c => c.State is "Established" or "Open");
            var protocols = group.Items.Select(c => c.ProtocolLabel).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var localCount = group.Items.Select(c => c.LocalEndpoint).Distinct(StringComparer.Ordinal).Count();
            var remoteCount = group.Items.Where(c => c.RemotePort != 0)
                .Select(c => c.RemoteEndpoint)
                .Distinct(StringComparer.Ordinal)
                .Count();

            rows.Add(new ConnectionGroupRow(
                group.Key,
                first.ProcessName,
                InitialOf(first.ProcessName),
                AccentPalette.For(first.ProcessName),
                AppIcons.ForProcess(first.ExecutablePath, first.ProcessName),
                $"{group.Items.Count} {(group.Items.Count == 1 ? "conn" : "conns")}",
                _expanded.Contains("conn|" + group.Key),
                group.Items.Take(MaxConnectionRows).Select(ToRow).ToList(),
                first.ExecutablePath ?? $"PID {first.ProcessId}",
                protocols.Count switch
                {
                    0 => "\u2014",
                    1 => protocols[0],
                    _ => string.Join("/", protocols)
                },
                $"{localCount} {(localCount == 1 ? "endpoint" : "endpoints")}",
                remoteCount == 0 ? "\u2014" : $"{remoteCount} {(remoteCount == 1 ? "host" : "hosts")}",
                $"{established} active",
                established > 0 ? ConnectionRow.LiveBrush : ConnectionRow.IdleBrush));
        }

        RowSync.Apply(Connections, rows, r => r.GroupKey);
    }

    private (long Down, long Up) GroupTotals(IReadOnlyList<AppUsage> apps)
    {
        long down = 0;
        long up = 0;

        foreach (var app in apps)
        {
            var (d, u) = _usageTotals.Get(app.ProcessId, app.ProcessName);
            down += d;
            up += u;
        }

        return (down, up);
    }

    private static string AppKey(AppUsage app) =>
        string.IsNullOrEmpty(app.ExecutablePath)
            ? "name:" + app.ProcessName.ToLowerInvariant()
            : "path:" + app.ExecutablePath.ToLowerInvariant();

    private static string AppKey(ConnectionInfo connection) =>
        string.IsNullOrEmpty(connection.ExecutablePath)
            ? "name:" + connection.ProcessName.ToLowerInvariant()
            : "path:" + connection.ExecutablePath.ToLowerInvariant();

    private static string MostCommonName(IReadOnlyList<AppUsage> apps) =>
        apps.GroupBy(a => a.ProcessName)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .FirstOrDefault() ?? "Unknown";

    private static string InitialOf(string name) =>
        string.IsNullOrWhiteSpace(name) ? "?" : char.ToUpperInvariant(name[0]).ToString();

    private ConnectionRow ToRow(ConnectionInfo connection) =>
        new(
            connection.Key,
            connection.ProcessName,
            connection.ProtocolLabel,
            connection.LocalEndpoint,
            connection.RemoteEndpoint,
            connection.State,
            BrushForState(connection.State),
            AppIcons.ForProcess(connection.ExecutablePath, connection.ProcessName),
            connection.ExecutablePath,
            TooltipFor(connection.ExecutablePath, connection.ProcessId));

    private static string TooltipFor(string? executablePath, int processId) =>
        executablePath ?? $"PID {processId}";

    private AppRow ToAppRow(
        AppSummary summary,
        double maxDownload,
        double maxUpload,
        bool showMeters,
        bool expanded)
    {
        var downloadIdle = showMeters && summary.Download < 1;
        var uploadIdle = showMeters && summary.Upload < 1;

        var downloadText = !showMeters
            ? "\u2014"
            : downloadIdle ? "0" : ByteFormatter.FormatRate(summary.Download);

        var uploadText = !showMeters
            ? "\u2014"
            : uploadIdle ? "0" : ByteFormatter.FormatRate(summary.Upload);

        var socketWord = summary.Sockets == 1 ? "socket" : "sockets";
        var hostWord = summary.Hosts == 1 ? "host" : "hosts";

        var detail = summary.Active > 0
            ? $"{summary.Active} active \u00B7 {summary.Hosts} {hostWord} \u00B7 {summary.Sockets} {socketWord}"
            : summary.Sockets > 0
                ? "No active outbound sockets"
                : "Traffic only \u00B7 sockets closed";

        BrowserTabInfo? tabs = null;

        if (!string.IsNullOrEmpty(summary.Path))
        {
            var exe = Path.GetFileNameWithoutExtension(summary.Path);
            _browserTabs.TryGetValue(exe, out tabs);
        }

        if (tabs is { Count: > 0 })
            detail += $" \u00B7 {tabs.Count} tabs";

        var allTabs = tabs?.Titles ?? Array.Empty<string>();
        var tabsKey = "tabs|" + summary.Key;
        var tabsExpanded = _tabsExpanded.Contains(tabsKey);
        var page = Pager.ClampPage(
            _tabPages.TryGetValue(tabsKey, out var storedPage) ? storedPage : 0,
            allTabs.Count,
            TabPageSize);
        var visibleTabs = Pager.Slice(allTabs, page, TabPageSize);
        var tabsPageText = Pager.RangeText(page, allTabs.Count, TabPageSize);
        var pageCount = Pager.PageCount(allTabs.Count, TabPageSize);

        var processes = summary.Apps
            .OrderByDescending(a => a.BytesReceived + a.BytesSent)
            .Select(a =>
            {
                var (d, u) = _usageTotals.Get(a.ProcessId, a.ProcessName);
                return new AppProcessRow(
                    a.ProcessId,
                    a.ProcessName,
                    $"PID {a.ProcessId}",
                    $"{a.ConnectionCount} {(a.ConnectionCount == 1 ? "socket" : "sockets")} \u00B7 {a.ActiveConnectionCount} active",
                    showMeters ? ByteFormatter.FormatRate(a.BytesReceived / _intervalSeconds) : "\u2014",
                    showMeters ? ByteFormatter.FormatRate(a.BytesSent / _intervalSeconds) : "\u2014",
                    showMeters ? ByteFormatter.FormatBytes(d + u) : "\u2014",
                    AppIcons.ForProcess(a.ExecutablePath, a.ProcessName));
            })
            .ToList();

        return new AppRow(
            summary.Key,
            summary.Name,
            InitialOf(summary.Name),
            AccentPalette.For(summary.Name),
            AppIcons.ForProcess(summary.Path, summary.Name),
            detail,
            downloadText,
            uploadText,
            showMeters ? ByteFormatter.FormatBytes(summary.TotalDown) : "\u2014",
            showMeters ? ByteFormatter.FormatBytes(summary.TotalUp) : "\u2014",
            MeterMath.Scale(summary.Download, maxDownload),
            MeterMath.Scale(summary.Upload, maxUpload),
            downloadIdle,
            uploadIdle,
            showMeters,
            summary.Path,
            _firewall.IsBlocked(summary.Path, FirewallDirection.In),
            _firewall.IsBlocked(summary.Path, FirewallDirection.Out),
            expanded,
            processes,
            allTabs,
            tabsExpanded,
            visibleTabs,
            tabsPageText,
            pageCount > 1,
            page > 0,
            page < pageCount - 1,
            summary.Path ?? $"PID {summary.Apps[0].ProcessId}");
    }

    private sealed record AppSummary(
        string Key,
        string Name,
        string? Path,
        IReadOnlyList<AppUsage> Apps,
        double Download,
        double Upload,
        long TotalDown,
        long TotalUp,
        int Sockets,
        int Active,
        int Hosts);

    private static int StatePriority(string state) => state switch
    {
        "Established" => 0,
        "Open" => 1,
        "SynSent" or "SynReceived" => 2,
        "Listen" => 3,
        "TimeWait" or "CloseWait" or "FinWait1" or "FinWait2" or "LastAck" or "Closing" => 4,
        _ => 5
    };

    private static System.Windows.Media.Brush BrushForState(string state) => state switch
    {
        "Established" or "Open" => ConnectionRow.LiveBrush,
        "Listen" => ConnectionRow.ListenBrush,
        "SynSent" or "SynReceived" => ConnectionRow.ClosingBrush,
        _ => ConnectionRow.IdleBrush
    };

    private void ToggleCapture()
    {
        IsCapturing = !IsCapturing;
        StatusText = IsCapturing ? "Live" : "Paused";

        if (IsCapturing)
            _ = TickAsync();
    }

    private void ToggleExpand(object? parameter)
    {
        var key = parameter switch
        {
            AppRow app => "app|" + app.GroupKey,
            ConnectionGroupRow connection => "conn|" + connection.GroupKey,
            _ => null
        };

        if (key is null)
            return;

        if (!_expanded.Add(key))
            _expanded.Remove(key);

        _ = TickAsync();
    }

    private void ToggleTabsSection(object? parameter)
    {
        if (parameter is not AppRow row)
            return;

        var key = "tabs|" + row.GroupKey;

        if (!_tabsExpanded.Add(key))
            _tabsExpanded.Remove(key);

        _ = TickAsync();
    }

    private void ChangeTabPage(object? parameter, int delta)
    {
        if (parameter is not AppRow row || row.Tabs.Count == 0)
            return;

        var key = "tabs|" + row.GroupKey;
        var page = Pager.ClampPage(
            (_tabPages.TryGetValue(key, out var stored) ? stored : 0) + delta,
            row.Tabs.Count,
            TabPageSize);

        _tabPages[key] = page;
        _ = TickAsync();
    }

    private void SetDirection(object? parameter, FirewallDirection direction, bool block)
    {
        var path = parameter switch
        {
            AppRow app => app.ExecutablePath,
            ConnectionRow connection => connection.ExecutablePath,
            _ => null
        };

        if (path is null)
        {
            MessageBox.Show(
                "DoorNet cannot read this process's executable path, so it cannot create a firewall rule for it.",
                "DoorNet firewall",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (!_firewall.IsAvailable)
        {
            var elevate = MessageBox.Show(
                $"{_firewall.UnavailableReason}\n\nRestart DoorNet with administrator rights now?",
                "DoorNet firewall",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (elevate == MessageBoxResult.Yes)
                RestartElevated();

            return;
        }

        if (!_firewall.IsEnabled)
        {
            MessageBox.Show(
                "The DoorNet firewall is off. Turn it on from the header, then change this application.",
                "DoorNet firewall",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var result = _firewall.SetBlocked(path, direction, block);

        if (!result.Succeeded)
        {
            MessageBox.Show(result.Message, "DoorNet firewall", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _ = TickAsync();
    }


    private void ToggleFirewall()
    {
        if (!_firewall.IsAvailable)
        {
            var elevate = MessageBox.Show(
                $"{_firewall.UnavailableReason}\n\nRestart DoorNet with administrator rights now?",
                "DoorNet firewall",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (elevate == MessageBoxResult.Yes)
                RestartElevated();

            return;
        }

        var result = _firewall.SetEnabled(!_firewall.IsEnabled);

        if (!result.Succeeded)
        {
            MessageBox.Show(result.Message, "DoorNet firewall", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        OnPropertyChanged(nameof(IsFirewallEnabled));
        OnPropertyChanged(nameof(FirewallStateText));
        OnPropertyChanged(nameof(FirewallTooltip));

        _ = TickAsync();
    }

    private void ToggleTheme()
    {
        _theme = _theme == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark;
        ThemeManager.Apply(_theme);

        var settings = SettingsStore.Load();
        settings.Theme = _theme.ToString();
        SettingsStore.Save(settings);

        OnPropertyChanged(nameof(ThemeGlyph));
        OnPropertyChanged(nameof(ThemeTooltip));
        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RestartElevated()
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrEmpty(executable) || _disposed)
            return;

        try
        {
            Process.Start(new ProcessStartInfo(executable)
            {
                UseShellExecute = true,
                Verb = "runas"
            });

            Application.Current.Shutdown();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // UAC was dismissed or there is no interactive shell: stay running unelevated.
        }
    }

    private void ResetSession()
    {
        _sampler.Reset();
        _history.Clear();
        _usageTotals.Clear();
        _peakDown = 0;
        _peakUp = 0;

        ChartPoints = Array.Empty<ThroughputPoint>();
        DownValue = "0";
        DownUnit = "B/s";
        UpValue = "0";
        UpUnit = "B/s";
        TotalValue = "0";
        TotalUnit = "B";
        DownPeakText = "peak 0 B/s";
        UpPeakText = "peak 0 B/s";
        SessionSplitText = "↓ 0 B   ↑ 0 B";
        IsNetworkIdle = true;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _timer.Stop();
        (_perAppSource as IDisposable)?.Dispose();
        _tabProbeCts.Cancel();
        _tabProbeCts.Dispose();
        _geo.Dispose();
    }
}
