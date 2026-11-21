using System.Collections.ObjectModel;
using System.Globalization;
using System.Diagnostics;
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
using DoorNet.Core.Models;
namespace DoorNet.App.ViewModels;

/// <summary>
/// Drives the dashboard: one 1 Hz timer, one sampler, and two row collections that are
/// synchronised in place so the UI never flickers.
/// </summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    private const int HistoryCapacity = 90;
    private const int MaxConnectionRows = 400;

    private readonly IThroughputSource _throughputSource;
    private readonly IConnectionSource _connectionSource;
    private readonly IPerAppUsageSource _perAppSource;
    private readonly ThroughputSampler _sampler = new();
    private readonly RingBuffer<ThroughputPoint> _history = new(HistoryCapacity);
    private readonly UsageTotals _usageTotals = new();
    private readonly DispatcherTimer _timer;

    private bool _tickRunning;
    private bool _isCapturing = true;
    private bool _disposed;
    private bool _isNetworkIdle = true;
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
            CaptureStack.CreatePerAppUsageSource())
    {
    }

    public MainViewModel(
        IThroughputSource throughputSource,
        IConnectionSource connectionSource,
        IPerAppUsageSource perAppSource)
    {
        _throughputSource = throughputSource;
        _connectionSource = connectionSource;
        _perAppSource = perAppSource;

        ToggleCaptureCommand = new RelayCommand(ToggleCapture);
        ResetSessionCommand = new RelayCommand(ResetSession);
        RestartElevatedCommand = new RelayCommand(RestartElevated);

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
    }

    public ObservableCollection<ConnectionRow> Connections { get; } = [];
    public ObservableCollection<AppRow> Apps { get; } = [];

    public RelayCommand ToggleCaptureCommand { get; }
    public RelayCommand ResetSessionCommand { get; }
    public RelayCommand RestartElevatedCommand { get; }

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
    public string VersionText => "DoorNet 0.1 · skeleton";

    public int ConnectionCount { get => _connectionCount; private set => SetProperty(ref _connectionCount, value); }
    public int AppCount { get => _appCount; private set => SetProperty(ref _appCount, value); }

    public IReadOnlyList<ThroughputPoint> ChartPoints
    {
        get => _chartPoints;
        private set => SetProperty(ref _chartPoints, value);
    }

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
        var rows = connections
            .Where(c => c.RemotePort != 0 || c.IsListening)
            .OrderBy(c => StatePriority(c.State))
            .ThenBy(c => c.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.RemoteEndpoint, StringComparer.Ordinal)
            .Take(MaxConnectionRows)
            .Select(ToRow)
            .ToList();

        RowSync.Apply(Connections, rows, r => r.Key);
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

        // With byte usage available, "top" means data consumed this session (stable order,
        // no row dancing every second). Without it, the best honest signal is socket count.
        var ordered = showMeters
            ? aggregated
                .OrderByDescending(SessionBytes)
                .ThenByDescending(a => a.BytesReceived + a.BytesSent)
            : aggregated
                .OrderByDescending(a => a.ActiveConnectionCount)
                .ThenByDescending(a => a.ConnectionCount);

        var usage = ordered
            .ThenBy(a => a.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var rates = usage
            .Select(a => (Download: a.BytesReceived / _intervalSeconds, Upload: a.BytesSent / _intervalSeconds))
            .ToList();

        var maxDownload = rates.Count == 0 ? 0 : rates.Max(r => r.Download);
        var maxUpload = rates.Count == 0 ? 0 : rates.Max(r => r.Upload);

        var apps = new List<AppRow>(usage.Count);
        for (var i = 0; i < usage.Count; i++)
        {
            var (totalDownload, totalUpload) = _usageTotals.Get(usage[i].ProcessId, usage[i].ProcessName);

            apps.Add(ToAppRow(
                usage[i],
                rates[i].Download,
                rates[i].Upload,
                totalDownload,
                totalUpload,
                maxDownload,
                maxUpload,
                showMeters));
        }

        RowSync.Apply(Apps, apps, r => r.ProcessId.ToString(CultureInfo.InvariantCulture));
        AppCount = apps.Count;
    }

    private static ConnectionRow ToRow(ConnectionInfo connection) =>
        new(
            connection.Key,
            connection.ProcessName,
            connection.ProtocolLabel,
            connection.LocalEndpoint,
            connection.RemoteEndpoint,
            connection.State,
            BrushForState(connection.State),
            AppIcons.ForProcess(connection.ExecutablePath, connection.ProcessName),
            connection.ExecutablePath ?? $"PID {connection.ProcessId}");

    private static AppRow ToAppRow(
        AppUsage app,
        double downloadRate,
        double uploadRate,
        long totalDownload,
        long totalUpload,
        double maxDownload,
        double maxUpload,
        bool showMeters)
    {
        var active = app.ActiveConnectionCount;
        var socketWord = app.ConnectionCount == 1 ? "socket" : "sockets";
        var hostWord = app.RemoteHostCount == 1 ? "host" : "hosts";

        var detail = active > 0
            ? $"{active} active · {app.RemoteHostCount} {hostWord} · {app.ConnectionCount} {socketWord}"
            : app.ConnectionCount > 0
                ? "No active outbound sockets"
                : "Traffic only · sockets closed";

        var connectionsText = app.ConnectionCount switch
        {
            0 => "no sockets",
            1 => "1 socket",
            _ => $"{app.ConnectionCount} sockets"
        };

        var downloadIdle = downloadRate < 1;
        var uploadIdle = uploadRate < 1;

        return new AppRow(
            app.ProcessId,
            app.ProcessName,
            app.Initial,
            AccentPalette.For(app.ProcessName),
            AppIcons.ForProcess(app.ExecutablePath, app.ProcessName),
            connectionsText,
            detail,
            downloadIdle ? "0" : ByteFormatter.FormatRate(downloadRate),
            uploadIdle ? "0" : ByteFormatter.FormatRate(uploadRate),
            showMeters ? ByteFormatter.FormatBytes(totalDownload) : string.Empty,
            showMeters ? ByteFormatter.FormatBytes(totalUpload) : string.Empty,
            MeterMath.Scale(downloadRate, maxDownload),
            MeterMath.Scale(uploadRate, maxUpload),
            downloadIdle,
            uploadIdle,
            showMeters,
            !showMeters,
            active > 0,
            app.ExecutablePath ?? $"PID {app.ProcessId}");
    }

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

    private long SessionBytes(AppUsage app)
    {
        var (download, upload) = _usageTotals.Get(app.ProcessId, app.ProcessName);
        return download + upload;
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
    }
}
