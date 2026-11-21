using System.Collections.Concurrent;
using System.Net;
using System.Runtime.InteropServices;
using DoorNet.Core.Abstractions;
using DoorNet.Core.Models;
using DoorNet.Core.Networking;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace DoorNet.Capture;

/// <summary>
/// Per-process byte usage from the kernel network provider (the same source GlassWire
/// uses). Kernel ETW sessions require administrator rights and the "NT Kernel Logger"
/// session is a single system-wide resource, so this source never hijacks one: if another
/// tool owns it, we report that and stay dark instead of stopping their session.
/// </summary>
public sealed class EtwPerAppUsageSource : IPerAppUsageSource, IDisposable
{
    private const string KernelSessionName = "NT Kernel Logger";

    private readonly ConcurrentDictionary<int, Traffic> _totals = new();
    private readonly ProcessNameCache _processNames = new();

    private TraceEventSession? _session;
    private Thread? _pumpThread;
    private volatile bool _available;
    private volatile bool _disposed;

    public EtwPerAppUsageSource()
    {
        if (!Environment.IsPrivilegedProcess)
        {
            UnavailableReason = "Administrator rights are required for the kernel ETW session.";
            return;
        }

        try
        {
            if (TraceEventSession.GetActiveSessionNames()
                .Any(name => string.Equals(name, KernelSessionName, StringComparison.OrdinalIgnoreCase)))
            {
                UnavailableReason =
                    "The NT Kernel Logger session is already in use by another monitoring tool.";
                return;
            }

            // NoRestartOnCreate: fail rather than stop somebody else's kernel session.
            _session = new TraceEventSession(
                KernelTraceEventParser.KernelSessionName,
                TraceEventSessionOptions.Create | TraceEventSessionOptions.NoRestartOnCreate);

            // Note: for kernel sessions the bool return of EnableKernelProvider is only
            // true in the "restart existing session" path, so check the session state.
            _session.EnableKernelProvider(KernelTraceEventParser.Keywords.NetworkTCPIP);

            if (!_session.IsActive)
            {
                UnavailableReason = "The kernel network provider refused to start.";
                _session.Dispose();
                _session = null;
                return;
            }

            var kernel = _session.Source.Kernel;
            kernel.TcpIpSend += data => Add(data.ProcessID, data.size, sent: true, data.daddr);
            kernel.TcpIpRecv += data => Add(data.ProcessID, data.size, sent: false, data.saddr);
            kernel.TcpIpSendIPV6 += data => Add(data.ProcessID, data.size, sent: true, data.daddr);
            kernel.TcpIpRecvIPV6 += data => Add(data.ProcessID, data.size, sent: false, data.saddr);
            kernel.UdpIpSend += data => Add(data.ProcessID, data.size, sent: true, data.daddr);
            kernel.UdpIpRecv += data => Add(data.ProcessID, data.size, sent: false, data.saddr);

            _pumpThread = new Thread(Pump)
            {
                IsBackground = true,
                Name = "DoorNet kernel ETW"
            };
            _pumpThread.Start();

            _available = true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException
                                       or InvalidOperationException
                                       or COMException
                                       or IOException)
        {
            UnavailableReason = Summarise(ex);
            _session?.Dispose();
            _session = null;
        }
    }

    public bool IsAvailable => _available;

    public string? UnavailableReason { get; private set; }

    public IReadOnlyList<PerAppTraffic> Drain()
    {
        if (!_available || _disposed)
            return Array.Empty<PerAppTraffic>();

        var rows = new List<PerAppTraffic>(_totals.Count);

        foreach (var (processId, traffic) in _totals)
        {
            var sent = Interlocked.Exchange(ref traffic.Sent, 0);
            var received = Interlocked.Exchange(ref traffic.Received, 0);

            if (sent == 0 && received == 0)
                continue;

            var (name, path) = _processNames.Resolve(processId);
            rows.Add(new PerAppTraffic(processId, sent, received, name, path));
        }

        PruneIfLarge();
        return rows;
    }

    private void Pump()
    {
        try
        {
            _session?.Source.Process();
        }
        catch (Exception ex)
        {
            // The session was stopped underneath us (service shutdown, policy change).
            _available = false;
            UnavailableReason = Summarise(ex);
        }
    }

    private void Add(int processId, int bytes, bool sent, IPAddress? endpoint)
    {
        // PIDs 0 and 4 are the idle/system pseudo-processes; their traffic is kernel housekeeping.
        if (processId <= 4 || bytes <= 0)
            return;

        // Localhost transfers never touch a NIC, so they are not network usage.
        if (endpoint is not null && AddressScope.IsLocalOnly(endpoint))
            return;

        var traffic = _totals.GetOrAdd(processId, static _ => new Traffic());
        if (sent)
            Interlocked.Add(ref traffic.Sent, bytes);
        else
            Interlocked.Add(ref traffic.Received, bytes);

        Interlocked.Exchange(ref traffic.LastSeenTicks, Environment.TickCount64);
    }

    private void PruneIfLarge()
    {
        if (_totals.Count <= 512)
            return;

        var cutoff = Environment.TickCount64 - (int)TimeSpan.FromMinutes(5).TotalMilliseconds;
        foreach (var (processId, traffic) in _totals)
        {
            if (Interlocked.Read(ref traffic.LastSeenTicks) < cutoff
                && Interlocked.Read(ref traffic.Sent) == 0
                && Interlocked.Read(ref traffic.Received) == 0)
            {
                _totals.TryRemove(processId, out _);
            }
        }
    }

    private static string Summarise(Exception ex) => ex switch
    {
        UnauthorizedAccessException => "Access denied starting the kernel ETW session.",
        COMException { HResult: unchecked((int)0x800700B7) } =>
            "The NT Kernel Logger session is already running.",
        _ => ex.Message.Length > 140 ? ex.Message[..140] : ex.Message
    };

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _available = false;

        try
        {
            _session?.Dispose();
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException)
        {
        }

        _pumpThread?.Join(TimeSpan.FromSeconds(2));
        _session = null;
    }

    private sealed class Traffic
    {
        public long Sent;
        public long Received;
        public long LastSeenTicks = Environment.TickCount64;
    }
}
