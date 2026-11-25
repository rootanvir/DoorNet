using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Data;
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
using DoorNet.Core.Scanning;
using DoorNet.Core.Threats;
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
    private readonly NetworkScanner _scanner = new();
    private readonly MacVendorLookup _macVendors = new();
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
    private readonly DispatcherTimer _trafficTimer;
    private bool _trafficSampling;
    private readonly Dictionary<string, UrlTrack> _urlTracks = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _dismissedAlerts = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _allowedHosts;
    private readonly VirusTotalClient _reputation = new();
    private Dictionary<string, RecentTraffic> _recentTraffic = new(StringComparer.Ordinal);
    private List<TrafficRow> _allTraffic = [];
    private string _selectedFilterKey = "all";
    private static readonly TimeSpan TrafficRetention = TimeSpan.FromSeconds(8);

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
    private bool _isScanning;
    private string _scannerStatus = "Not scanned yet — run a scan to list the devices on this network.";
    private string _scannerSubnet = "—";
    private string _scannerSummary = string.Empty;
    private int _analyzedUrlCount;
    private int _suspiciousUrlCount;
    private bool _rebuilding;
    private bool _rebuildQueued;
    private string _apiKeyInput = string.Empty;
    private string _urlScanSummary = "No browser page seen yet — open one and DoorNet scores it within a few seconds.";
    private string _trafficSummary = "Collecting traffic…";
    private string _icmpSummary = "no ICMP echo traffic seen yet";
    private LocalNetworkInfo? _localNetwork;

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
        ToggleDownloadCommand = new RelayCommand<object?>(row => ToggleDirection(row, FirewallDirection.In));
        ToggleUploadCommand = new RelayCommand<object?>(row => ToggleDirection(row, FirewallDirection.Out));
        ToggleExpandCommand = new RelayCommand<object?>(ToggleExpand);
        ToggleTabsCommand = new RelayCommand<object?>(ToggleTabsSection);
        PreviousTabsCommand = new RelayCommand<object?>(row => ChangeTabPage(row, -1));
        NextTabsCommand = new RelayCommand<object?>(row => ChangeTabPage(row, 1));
        ToggleFirewallCommand = new RelayCommand(ToggleFirewall);
        ScanNetworkCommand = new RelayCommand(() => _ = ScanNetworkAsync());
        DismissAlertCommand = new RelayCommand<object?>(DismissAlert);
        WhitelistAlertCommand = new RelayCommand<object?>(WhitelistAlert);
        CopyAlertCommand = new RelayCommand<object?>(CopyAlert);
        SaveApiKeyCommand = new RelayCommand(SaveApiKey);
        ClearApiKeyCommand = new RelayCommand(ClearApiKey);
        RemoveWhitelistedHostCommand = new RelayCommand<object?>(RemoveWhitelistedHost);
        SelectProtocolFilterCommand = new RelayCommand<object?>(SelectProtocolFilter);

        _allowedHosts = new HashSet<string>(SettingsStore.Load().WhitelistedHosts, StringComparer.OrdinalIgnoreCase);
        LoadWhitelistedHosts();

        foreach (var rule in UrlAnalyzer.Rules)
            DetectionRules.Add(new DetectionRuleRow(rule.Id, rule.Title, $"+{rule.Weight}", rule.Detail));

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

        // Sockets come and go in well under a second (curl, short API calls), so sample faster
        // than the dashboard tick and remember what disappeared for a few seconds.
        _trafficTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _trafficTimer.Tick += (_, _) => _ = SampleTrafficAsync();

        _timer.Start();
        _trafficTimer.Start();
        _ = TickAsync();
        _ = LocateSelfAsync();
        _ = LoadLocalNetworkAsync();
        _ = ProbeTabsLoopAsync(_tabProbeCts.Token);
    }

    public ObservableCollection<ConnectionGroupRow> Connections { get; } = [];
    public ObservableCollection<AppRow> Apps { get; } = [];
    public ObservableCollection<ScannerRow> ScanDevices { get; } = [];
    public ObservableCollection<UrlFindingRow> UrlFindings { get; } = [];
    public ObservableCollection<UrlAlertRow> UrlAlerts { get; } = [];
    public ObservableCollection<DetectionRuleRow> DetectionRules { get; } = [];
    public ObservableCollection<ReputationRow> Reputations { get; } = [];
    public ObservableCollection<TrafficRow> Traffic { get; } = [];
    public ObservableCollection<string> WhitelistedHosts { get; } = [];

    /// <summary>Protocol choices for the Traffic filter, rebuilt from what is on screen.</summary>
    public ObservableCollection<ProtocolFilterOption> ProtocolFilters { get; } = [];

    public RelayCommand ToggleCaptureCommand { get; }
    public RelayCommand ResetSessionCommand { get; }
    public RelayCommand RestartElevatedCommand { get; }
    public RelayCommand ToggleThemeCommand { get; }
    public RelayCommand<object?> ToggleDownloadCommand { get; }
    public RelayCommand<object?> ToggleUploadCommand { get; }
    public RelayCommand<object?> ToggleExpandCommand { get; }
    public RelayCommand<object?> ToggleTabsCommand { get; }
    public RelayCommand<object?> PreviousTabsCommand { get; }
    public RelayCommand<object?> NextTabsCommand { get; }
    public RelayCommand ToggleFirewallCommand { get; }
    public RelayCommand ScanNetworkCommand { get; }
    public RelayCommand<object?> DismissAlertCommand { get; }
    public RelayCommand<object?> WhitelistAlertCommand { get; }
    public RelayCommand<object?> CopyAlertCommand { get; }
    public RelayCommand SaveApiKeyCommand { get; }
    public RelayCommand ClearApiKeyCommand { get; }
    public RelayCommand<object?> RemoveWhitelistedHostCommand { get; }
    public RelayCommand<object?> SelectProtocolFilterCommand { get; }

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

    public bool IsScanning
    {
        get => _isScanning;
        private set
        {
            if (!SetProperty(ref _isScanning, value))
                return;

            OnPropertyChanged(nameof(ScanButtonText));
            OnPropertyChanged(nameof(ScanButtonEnabled));
        }
    }

    public string ScanButtonText => IsScanning ? "Scanning…" : "Scan network";
    public bool ScanButtonEnabled => !IsScanning;
    public string ScannerStatusText { get => _scannerStatus; private set => SetProperty(ref _scannerStatus, value); }
    public string ScannerSubnetText { get => _scannerSubnet; private set => SetProperty(ref _scannerSubnet, value); }
    public string ScannerSummaryText { get => _scannerSummary; private set => SetProperty(ref _scannerSummary, value); }
    public bool HasScanResults => ScanDevices.Count > 0;

    /// <summary>Header label carries the live count so alerts are visible from any tab.</summary>
    public string AlertsTabLabel => UrlAlerts.Count == 0 ? "Alerts" : $"Alerts ({UrlAlerts.Count})";

    public bool HasUrlAlerts => UrlAlerts.Count > 0;

    /// <summary>Summary line for the Traffic tab.</summary>
    public string TrafficSummary
    {
        get => _trafficSummary;
        private set => SetProperty(ref _trafficSummary, value);
    }

    public string IcmpSummary
    {
        get => _icmpSummary;
        private set => SetProperty(ref _icmpSummary, value);
    }

    public bool HasTraffic => _allTraffic.Count > 0;

    /// <summary>Text on the Traffic filter button, e.g. "Filter: All (348)".</summary>
    public string ProtocolFilterLabel
    {
        get
        {
            var option = ProtocolFilters.FirstOrDefault(item => item.Key == _selectedFilterKey)
                         ?? ProtocolFilters.FirstOrDefault();

            return option is null ? "Filter: All" : $"Filter: {option.Display}";
        }
    }

    public bool HasWhitelistedHosts => WhitelistedHosts.Count > 0;

    public string UrlScanSummary
    {
        get => _urlScanSummary;
        private set => SetProperty(ref _urlScanSummary, value);
    }

    public string UrlAlertsSummary => HasUrlAlerts
        ? $"{UrlAlerts.Count} suspicious address(es) need a decision"
        : "Nothing suspicious right now.";

    public int AnalyzedUrlCount { get => _analyzedUrlCount; private set => SetProperty(ref _analyzedUrlCount, value); }

    public int SuspiciousUrlCount { get => _suspiciousUrlCount; private set => SetProperty(ref _suspiciousUrlCount, value); }

    /// <summary>VirusTotal state: configured (with a masked key) or the reason it is not.</summary>
    public string ReputationStatusText => _reputation.StatusText;

    public bool IsReputationAvailable => _reputation.IsAvailable;

    /// <summary>Text the user types into the app's key field; cleared after saving.</summary>
    public string ApiKeyInput { get => _apiKeyInput; set => SetProperty(ref _apiKeyInput, value); }

    public string LocalNetworkName => _localNetwork?.NetworkName ?? "Detecting…";

    public string LocalNetworkDetail => _localNetwork is { } network
        ? string.Join(" · ", new[] { network.ConnectionType, network.WifiDetails.Length > 0 ? network.WifiDetails : network.LinkSpeed }
            .Where(part => !string.IsNullOrWhiteSpace(part)))
        : string.Empty;

    public string LocalRouterText => _localNetwork?.Gateway ?? "—";

    public string LocalRouterDetail => _localNetwork is { } network
        ? $"subnet {network.Subnet} · DNS {network.DnsServers}"
        : "—";

    public string LocalHostText => _localNetwork?.HostName ?? Environment.MachineName;

    public string LocalHostDetail => _localNetwork is { } network
        ? $"{network.OsVersion} · {network.LocalAddress} · {network.MacAddress}"
        : Environment.OSVersion.VersionString;

    public string LocalPublicIpText => _selfGeo?.Address ?? "—";

    public string LocalIspText
    {
        get
        {
            if (_selfGeo is not { } geo)
                return "Looking up…";

            var parts = new[] { geo.NetworkText, geo.LocationText }
                .Where(part => !string.IsNullOrWhiteSpace(part));

            var text = string.Join(" · ", parts);
            return text.Length > 0 ? text : "—";
        }
    }

    public string LocalDnsText => _localNetwork?.DnsServers ?? "—";
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

        // DoorNet is an application on this machine like any other: list it even while it has
        // no sockets of its own, instead of letting it appear only during its own requests.
        aggregated = IncludeSelf(aggregated);

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
        MergeTraffic(connections, rebuild: true);
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
                OnPropertyChanged(nameof(LocalPublicIpText));
                OnPropertyChanged(nameof(LocalIspText));
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
                    AnalyzeBrowserUrls(tabs);
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
            markers.Add(new GeoMarker(self.Latitude, self.Longitude, IsLocal: true, "Mine", self.City, self.Country));

        foreach (var (address, point) in _remoteGeo.Take(300))
            markers.Add(new GeoMarker(point.Latitude, point.Longitude, IsLocal: false, address, point.City, point.Country));

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

    /// <summary>Adds this process to the app list when the socket tables do not mention it.</summary>
    private static IReadOnlyList<AppUsage> IncludeSelf(IReadOnlyList<AppUsage> apps)    {
        if (apps.Any(app => app.ProcessId == Environment.ProcessId))
            return apps;

        var path = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(path))
            return apps;

        var rows = new List<AppUsage>(apps.Count + 1);
        rows.AddRange(apps);
        rows.Add(new AppUsage
        {
            ProcessId = Environment.ProcessId,
            ProcessName = Path.GetFileNameWithoutExtension(path),
            ExecutablePath = path
        });

        return rows;
    }


    private void BuildConnectionGroups(IReadOnlyList<ConnectionInfo> connections)    {
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

    private void ToggleDirection(object? parameter, FirewallDirection direction)
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

        var blocked = _firewall.IsBlocked(path, direction);
        var result = _firewall.SetBlocked(path, direction, !blocked);

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

    private async Task EnrichVendorsAsync()
    {
        // Only registered (non-randomised) prefixes that the offline table does not know.
        var targets = ScanDevices
            .Where(row => row.Vendor == "Unknown vendor" && row.Mac != "—")
            .Where(row => !MacAddress.IsLocallyAdministered(row.Mac))
            .GroupBy(row => MacAddress.Prefix(row.Mac))
            .Select(group => group.First())
            .Take(16)
            .ToList();

        foreach (var target in targets)
        {
            var vendor = await _macVendors.LookupAsync(target.Mac).ConfigureAwait(true);
            if (string.IsNullOrWhiteSpace(vendor))
                continue;

            var prefix = MacAddress.Prefix(target.Mac);
            for (var i = 0; i < ScanDevices.Count; i++)
            {
                var row = ScanDevices[i];
                if (row.Vendor != "Unknown vendor" || MacAddress.Prefix(row.Mac) != prefix)
                    continue;

                ScanDevices[i] = row with
                {
                    Vendor = vendor,
                    Tooltip = row.Tooltip.Replace(
                        "Vendor Unknown vendor",
                        $"Vendor {vendor}",
                        StringComparison.Ordinal)
                };
            }
        }
    }

    private async Task LoadLocalNetworkAsync()
    {
        try
        {
            var info = await Task.Run(LocalNetworkProbe.Read);
            if (info is null)
                return;

            _localNetwork = info;
            OnPropertyChanged(nameof(LocalNetworkName));
            OnPropertyChanged(nameof(LocalNetworkDetail));
            OnPropertyChanged(nameof(LocalRouterText));
            OnPropertyChanged(nameof(LocalRouterDetail));
            OnPropertyChanged(nameof(LocalHostText));
            OnPropertyChanged(nameof(LocalHostDetail));
            OnPropertyChanged(nameof(LocalDnsText));
        }
        catch (Exception)
        {
            // Best effort: the local card keeps its placeholders.
        }
    }

    private async Task ScanNetworkAsync()
    {
        if (IsScanning)
            return;

        IsScanning = true;
        ScanDevices.Clear();
        OnPropertyChanged(nameof(HasScanResults));
        ScannerSummaryText = string.Empty;
        ScannerStatusText = "Starting scan…";
        _ = LoadLocalNetworkAsync();

        try
        {
            var progress = new Progress<ScanProgress>(report => ScannerStatusText = report.Message);
            var result = await Task.Run(() => _scanner.ScanAsync(progress)).ConfigureAwait(true);

            ScannerSubnetText = result.Subnet;
            foreach (var device in result.Devices)
                ScanDevices.Add(ToScannerRow(device));

            ScannerSummaryText = $"{result.Devices.Count} devices · {result.Devices.Count(d => d.IsActive)} active";
            ScannerStatusText = $"Last scan {DateTime.Now:HH:mm:ss} · {result.Duration.TotalSeconds:0.0} s";
            _ = EnrichVendorsAsync();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Net.NetworkInformation.NetworkInformationException)
        {
            ScannerStatusText = ex.Message;
        }
        catch (Exception ex)
        {
            ScannerStatusText = $"Scan failed: {ex.Message}";
        }
        finally
        {
            IsScanning = false;
            OnPropertyChanged(nameof(HasScanResults));
        }
    }

    private static ScannerRow ToScannerRow(LanDevice device) => new(
        device.KindText,
        ScannerRow.GlyphFor(device.Kind),
        device.Name,
        device.Address,
        string.IsNullOrEmpty(device.Mac) ? "—" : device.Mac,
        device.System,
        device.VendorText,
        device.PortsText,
        device.LatencyText,
        device.IsActive ? "Active" : "Idle",
        device.IsActive ? ScannerRow.ActiveBrush : ScannerRow.IdleBrush,
        device.IsActive,
        $"{device.KindText} · {device.System}\nIP {device.Address}\nMAC {(string.IsNullOrEmpty(device.Mac) ? "unknown" : device.Mac)}\nVendor {device.VendorText}\nOpen ports: {device.PortsText}\n{(device.IsActive ? $"Responding · {device.LatencyText}" : "Not responding (MAC known)")}");

    /// <summary>Scores the address of every browser's active tab and refreshes the threat lists.</summary>
    private void AnalyzeBrowserUrls(IReadOnlyDictionary<string, BrowserTabInfo> tabs)
    {
        var now = DateTimeOffset.Now;

        foreach (var (process, info) in tabs)
        {
            if (string.IsNullOrWhiteSpace(info.Address))
                continue;

            var assessment = UrlAnalyzer.Assess(info.Address);
            if (string.IsNullOrEmpty(assessment.Host))
                continue;

            var key = process + "|" + assessment.Url;
            if (!_urlTracks.TryGetValue(key, out var track))
            {
                track = new UrlTrack
                {
                    Url = assessment.Url,
                    Host = assessment.Host,
                    Process = process,
                    FirstSeen = now
                };
                _urlTracks[key] = track;
            }

            // Keep the worst verdict for as long as the address stays open.
            if (assessment.Score >= (track.Assessment?.Score ?? -1))
                track.Assessment = assessment;

            track.LastSeen = now;
            track.Hits++;
        }

        RebuildThreatRows();
    }

    private void RebuildThreatRows()
    {
        // A cached reputation completes synchronously, so enrichment can call back into this
        // method while the outer pass is still building rows. Queue instead of nesting.
        if (_rebuilding)
        {
            _rebuildQueued = true;
            return;
        }

        _rebuilding = true;
        try
        {
            BuildThreatRows();
        }
        finally
        {
            _rebuilding = false;
        }

        RebuildTraffic();

        if (_rebuildQueued)
        {
            _rebuildQueued = false;
            RebuildThreatRows();
        }
    }

    private void BuildThreatRows()
    {
        var findings = new List<UrlFindingRow>(_urlTracks.Count);
        var alerts = new List<UrlAlertRow>();
        var danger = 0;
        var suspicious = 0;
        var watch = 0;

        foreach (var track in _urlTracks.Values
                     .OrderBy(track => track.Assessment is null
                         ? UrlCategories.Rank(UrlCategory.Safe)
                         : UrlCategories.Rank(UrlCategories.For(track.Assessment)))
                     .ThenByDescending(track => track.Assessment?.Score ?? 0)
                     .ThenByDescending(track => track.LastSeen))
        {
            if (track.Assessment is not { } assessment)
                continue;

            // The local score plus whatever reputation adds, capped at 100.
            var score = Math.Min(100, assessment.Score + (track.Reputation?.Weight ?? 0));
            var category = assessment.IsExempt ? UrlCategory.Safe : UrlCategories.For(score);
            var isSuspicious = category is UrlCategory.Danger or UrlCategory.Suspicious;

            switch (category)
            {
                case UrlCategory.Danger:
                    danger++;
                    break;
                case UrlCategory.Suspicious:
                    suspicious++;
                    break;
                case UrlCategory.Watch:
                    watch++;
                    break;
            }

            findings.Add(new UrlFindingRow(
                track.Process + "|" + track.Url,
                track.Url,
                track.Host,
                track.Process,
                score.ToString(CultureInfo.InvariantCulture),
                UrlCategories.Label(category),
                UrlCategories.Label(category),
                SeverityBrushes.For(category),
                assessment.Reasons,
                track.LastSeen.ToString("HH:mm:ss", CultureInfo.CurrentCulture),
                isSuspicious));

            var alertKey = AlertKeyFor(track.Process, track.Host);
            if (!isSuspicious
                || _dismissedAlerts.Contains(alertKey)
                || _allowedHosts.Contains(track.Host))
            {
                continue;
            }

            // Ask the reputation service once per host, in the background.
            if (_reputation.IsAvailable && track.Reputation is null && !track.ReputationRequested && IsPublicHost(track.Host))
            {
                track.ReputationRequested = true;
                _ = EnrichReputationAsync(track);
            }

            alerts.Add(new UrlAlertRow(
                alertKey,
                track.Host,
                track.Url,
                track.Process,
                score.ToString(CultureInfo.InvariantCulture),
                UrlCategories.Label(category),
                SeverityBrushes.For(category),
                assessment.Reasons,
                $"{track.FirstSeen:HH:mm} · {track.Hits}×",
                track.Reputation is null ? string.Empty : $"VT {track.Reputation.Malicious}/{track.Reputation.Total}",
                track.Reputation?.Detail ?? string.Empty));
        }

        RowSync.Apply(UrlFindings, findings, row => row.Key);
        RowSync.Apply(UrlAlerts, alerts, row => row.Key);

        AnalyzedUrlCount = findings.Count;
        SuspiciousUrlCount = findings.Count(row => row.IsSuspicious);

        UrlScanSummary = findings.Count == 0
            ? "No browser page seen yet — open one and DoorNet scores it within a few seconds."
            : $"{findings.Count} addresses · {danger} danger · {suspicious} suspicious · {watch} watch · {findings.Count - danger - suspicious - watch} safe";

        OnPropertyChanged(nameof(HasUrlAlerts));
        OnPropertyChanged(nameof(AlertsTabLabel));
        OnPropertyChanged(nameof(UrlAlertsSummary));
        OnPropertyChanged(nameof(UrlScanSummary));
    }

    /// <summary>Samples the socket tables on a short cadence so brief transfers still appear.</summary>
    private async Task SampleTrafficAsync()
    {
        if (_trafficSampling || _disposed || !_isCapturing)
            return;

        _trafficSampling = true;
        try
        {
            var snapshot = await Task.Run(_connectionSource.Snapshot).ConfigureAwait(true);
            MergeTraffic(snapshot, rebuild: false);
        }
        catch (Exception)
        {
            // Adapters come and go; the next sample retries.
        }
        finally
        {
            _trafficSampling = false;
        }
    }

    private void MergeTraffic(IReadOnlyList<ConnectionInfo> connections, bool rebuild)
    {
        var now = DateTimeOffset.Now;

        foreach (var connection in connections)
        {
            var key = connection.Key;
            _recentTraffic[key] = _recentTraffic.TryGetValue(key, out var previous)
                ? previous with { LastSeen = now }
                : new RecentTraffic(connection, now);
        }

        var cutoff = now - TrafficRetention;
        foreach (var key in _recentTraffic.Where(pair => pair.Value.LastSeen < cutoff).Select(pair => pair.Key).ToList())
            _recentTraffic.Remove(key);

        if (rebuild)
            RebuildTraffic();
    }

    /// <summary>One list for everything: scored pages, every live socket, and the ICMP counters.</summary>
    private void RebuildTraffic()
    {
        var rows = new List<TrafficRow>(_recentTraffic.Count + _urlTracks.Count + 1);
        var danger = 0;
        var suspicious = 0;
        var watch = 0;

        foreach (var track in _urlTracks.Values)
        {
            if (track.Assessment is not { } assessment)
                continue;

            var score = Math.Min(100, assessment.Score + (track.Reputation?.Weight ?? 0));
            var category = assessment.IsExempt ? UrlCategory.Safe : UrlCategories.For(score);

            switch (category)
            {
                case UrlCategory.Danger:
                    danger++;
                    break;
                case UrlCategory.Suspicious:
                    suspicious++;
                    break;
                case UrlCategory.Watch:
                    watch++;
                    break;
            }

            rows.Add(new TrafficRow(
                "page|" + track.Process + "|" + track.Url,
                UrlCategories.Label(category),
                SeverityBrushes.For(category),
                track.Process,
                "Out",
                track.Url.StartsWith("https", StringComparison.OrdinalIgnoreCase) ? "HTTPS" : "HTTP",
                track.Host,
                "page",
                track.Url,
                score.ToString(CultureInfo.InvariantCulture),
                track.LastSeen.ToString("HH:mm:ss", CultureInfo.CurrentCulture),
                category is UrlCategory.Danger or UrlCategory.Suspicious));
        }

        foreach (var entry in _recentTraffic.Values.OrderByDescending(item => item.LastSeen).Take(400))
        {
            var connection = entry.Connection;

            // The process cache prefers the friendly description ("The curl executable");
            // the traffic list wants the file name people actually type.
            var agent = Path.GetFileName(connection.ExecutablePath);
            if (string.IsNullOrWhiteSpace(agent))
                agent = connection.ProcessName;

            rows.Add(new TrafficRow(
                "sock|" + connection.Key,
                "Live",
                SeverityBrushes.Low,
                agent,
                DirectionOf(connection),
                connection.ProtocolLabel,
                connection.IsListening
                    ? (connection.LocalAddress is "0.0.0.0" or "::" or "[::]"
                        ? $"*:{connection.LocalPort}"
                        : $"{connection.LocalAddress}:{connection.LocalPort}")
                    : connection.RemoteEndpoint,
                connection.State,
                $"pid {connection.ProcessId}",
                string.Empty,
                string.Empty,
                false));
        }

        var icmp = LocalTrafficStats.ReadIcmp();
        rows.Add(new TrafficRow(
            "icmp",
            "Info",
            SeverityBrushes.Low,
            "icmp",
            "In/Out",
            "ICMP",
            "echo",
            "—",
            icmp.HasActivity ? icmp.Summary : "no echo traffic yet",
            string.Empty,
            string.Empty,
            false));

        // DoorNet itself, so the app is visible here like any other agent.
        var selfPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(selfPath))
        {
            rows.Add(new TrafficRow(
                "self",
                "App",
                SeverityBrushes.Medium,
                Path.GetFileName(selfPath),
                "—",
                "—",
                "this app",
                "running",
                selfPath,
                string.Empty,
                string.Empty,
                false));
        }

        var ordered = rows
            .OrderBy(row => RankOf(row.Tag))
            .ThenByDescending(row => ParseScore(row.ScoreText))
            .ToList();

        _allTraffic = ordered;
        RebuildProtocolFilters();
        ApplyTrafficFilter();

        OnPropertyChanged(nameof(HasTraffic));

        TrafficSummary = $"{_recentTraffic.Count} sockets · {_urlTracks.Count} pages · " +
                         $"{danger} danger · {suspicious} suspicious · {watch} watch";
        IcmpSummary = icmp.HasActivity ? icmp.Summary : "no ICMP echo traffic seen yet";
    }

    /// <summary>Offers All plus whichever protocols are actually present right now.</summary>
    private void RebuildProtocolFilters()
    {
        var options = new List<ProtocolFilterOption>
        {
            new("all", "All", _allTraffic.Count)
        };

        Add("TCP", "TCP");
        Add("UDP", "UDP");
        Add("ICMP", "ICMP");
        Add("web", "Web");

        if (options.All(option => option.Key != _selectedFilterKey))
            _selectedFilterKey = "all";

        ProtocolFilters.Clear();
        foreach (var option in options)
            ProtocolFilters.Add(option);

        OnPropertyChanged(nameof(ProtocolFilterLabel));
        return;

        void Add(string key, string label)
        {
            var count = _allTraffic.Count(row => key == "web"
                ? row.Protocol is "HTTP" or "HTTPS"
                : row.Protocol == key);

            if (count > 0)
                options.Add(new ProtocolFilterOption(key, label, count));
        }
    }

    private void SelectProtocolFilter(object? parameter)
    {
        if (parameter is not ProtocolFilterOption option)
            return;

        _selectedFilterKey = option.Key;
        OnPropertyChanged(nameof(ProtocolFilterLabel));
        ApplyTrafficFilter();
    }

    private void ApplyTrafficFilter()
    {
        var visible = _selectedFilterKey == "all"
            ? _allTraffic
            : _allTraffic.Where(row => _selectedFilterKey == "web"
                ? row.Protocol is "HTTP" or "HTTPS"
                : row.Protocol == _selectedFilterKey).ToList();

        RowSync.Apply(Traffic, visible, row => row.Key);
    }


    private static int RankOf(string tag) => tag switch
    {
        "Danger" => 0,
        "Suspicious" => 1,
        "Watch" => 2,
        "Safe" => 3,
        "Live" => 4,
        "App" => 5,
        _ => 6
    };

    private static int ParseScore(string text) => int.TryParse(text, out var score) ? score : -1;

    private static string DirectionOf(ConnectionInfo connection)
    {
        if (connection.IsListening)
            return "Listen";

        if (connection.Protocol == TransportProtocol.Udp)
            return connection.RemotePort == 0 ? "Listen" : "Out";

        return connection.LocalPort >= 32768 ? "Out" : "In";
    }

    private sealed record RecentTraffic(ConnectionInfo Connection, DateTimeOffset LastSeen);

    private void DismissAlert(object? parameter)    {
        if (parameter is not UrlAlertRow alert)
            return;

        _dismissedAlerts.Add(alert.Key);
        RebuildThreatRows();
    }

    private void WhitelistAlert(object? parameter)
    {
        if (parameter is not UrlAlertRow alert)
            return;

        _allowedHosts.Add(alert.Host);
        _dismissedAlerts.Add(alert.Key);
        SettingsStore.WhitelistHost(alert.Host);
        LoadWhitelistedHosts();
        RebuildThreatRows();
    }

    private void CopyAlert(object? parameter)
    {
        if (parameter is not UrlAlertRow alert)
            return;

        var text = string.Join(
            Environment.NewLine,
            $"{alert.SeverityText} ({alert.ScoreText})  {alert.Host}",
            $"URL: {alert.Url}",
            $"Process: {alert.Process}",
            $"Seen: {alert.SeenText}",
            $"Why: {alert.Reasons}");

        try
        {
            Clipboard.SetText(text);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            // Clipboard was locked by another process; the user can retry.
        }
    }

    private static string AlertKeyFor(string process, string host) => process + "|" + host;

    private void LoadWhitelistedHosts()
    {
        WhitelistedHosts.Clear();
        foreach (var host in _allowedHosts.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
            WhitelistedHosts.Add(host);

        OnPropertyChanged(nameof(HasWhitelistedHosts));
    }

    private void RemoveWhitelistedHost(object? parameter)
    {
        if (parameter is not string host)
            return;

        _allowedHosts.Remove(host);
        SettingsStore.RemoveWhitelistedHost(host);
        LoadWhitelistedHosts();
        RebuildThreatRows();
    }

    /// <summary>Only real, public targets are sent to a third party.</summary>
    private static bool IsPublicHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return false;

        if (!System.Net.IPAddress.TryParse(host.Trim('[', ']'), out var address))
            return !host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                   && !host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
                   && !host.EndsWith(".lan", StringComparison.OrdinalIgnoreCase)
                   && !host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase);

        if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || System.Net.IPAddress.IsLoopback(address))
            return false;

        var bytes = address.GetAddressBytes();
        return bytes.Length != 4
               || (bytes[0] != 10
                   && !(bytes[0] == 192 && bytes[1] == 168)
                   && !(bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                   && !(bytes[0] == 169 && bytes[1] == 254));
    }

    private void SaveApiKey()
    {
        var key = (ApiKeyInput ?? string.Empty).Trim();
        if (key.Length < 16)
        {
            ApiKeyInput = string.Empty;
            OnPropertyChanged(nameof(ReputationStatusText));
            return;
        }

        SettingsStore.SaveApiKey(key);
        _reputation.Configure(key);
        ApiKeyInput = string.Empty;

        // A key arriving mid-session should score what is already on screen.
        foreach (var track in _urlTracks.Values)
            track.ReputationRequested = false;

        OnPropertyChanged(nameof(ReputationStatusText));
        OnPropertyChanged(nameof(IsReputationAvailable));
        RebuildThreatRows();
    }

    private void ClearApiKey()
    {
        SettingsStore.SaveApiKey(null);
        _reputation.Configure(null);
        ApiKeyInput = string.Empty;

        OnPropertyChanged(nameof(ReputationStatusText));
        OnPropertyChanged(nameof(IsReputationAvailable));
    }

    private async Task EnrichReputationAsync(UrlTrack track)
    {
        try
        {
            var reputation = await _reputation.LookupAsync(track.Host).ConfigureAwait(true);

            if (reputation is null)
            {
                // Allow a later retry, and keep the UI honest when the key is rejected.
                track.ReputationRequested = false;
                OnPropertyChanged(nameof(ReputationStatusText));
                return;
            }

            // Cleared only on failure: a cached hit completes synchronously, and leaving the
            // flag set keeps the rebuild below from asking for the same host again.
            track.Reputation = reputation;

            Reputations.Insert(0, new ReputationRow(
                reputation.Target,
                reputation.Summary,
                reputation.Detail,
                DateTime.Now.ToString("HH:mm:ss", CultureInfo.CurrentCulture),
                SeverityBrushes.For(reputation.Severity)));

            while (Reputations.Count > 40)
                Reputations.RemoveAt(Reputations.Count - 1);

            OnPropertyChanged(nameof(ReputationStatusText));
            RebuildThreatRows();
        }
        catch (Exception)
        {
            track.ReputationRequested = false;
        }
    }

    /// <summary>One address as it was seen, keeping the worst verdict for the session.</summary>
    private sealed class UrlTrack
    {
        public required string Url { get; init; }
        public required string Host { get; init; }
        public required string Process { get; init; }
        public DateTimeOffset FirstSeen { get; init; }
        public DateTimeOffset LastSeen { get; set; }
        public int Hits { get; set; }
        public UrlAssessment? Assessment { get; set; }
        public ThreatReputation? Reputation { get; set; }
        public bool ReputationRequested { get; set; }
    }

    private void ResetSession()    {
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
        _trafficTimer.Stop();
        (_perAppSource as IDisposable)?.Dispose();
        _tabProbeCts.Cancel();
        _tabProbeCts.Dispose();
        _geo.Dispose();
        _macVendors.Dispose();
        _reputation.Dispose();
    }
}