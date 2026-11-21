using System.Net;
using System.Runtime.InteropServices;
using DoorNet.Core.Abstractions;
using DoorNet.Core.Models;

namespace DoorNet.Capture;

/// <summary>
/// Reads the IPv4 and IPv6 TCP/UDP tables through <c>iphlpapi</c>. Requires no elevation,
/// and is the source of truth for "what is this machine talking to right now".
/// </summary>
public sealed class ConnectionTableSource : IConnectionSource
{
    private const int AfInet = 2;
    private const int AfInet6 = 23;
    private const uint ErrorInsufficientBuffer = 122;

    private readonly ProcessNameCache _processNames = new();

    public IReadOnlyList<ConnectionInfo> Snapshot()
    {
        var rows = new List<ConnectionInfo>(128);

        ReadTcp(rows, AfInet, isV6: false);
        ReadTcp(rows, AfInet6, isV6: true);
        ReadUdp(rows, AfInet, isV6: false);
        ReadUdp(rows, AfInet6, isV6: true);

        return rows;
    }

    private void ReadTcp(List<ConnectionInfo> rows, int family, bool isV6)
    {
        var buffer = QueryTable(
            (IntPtr table, ref int size) => GetExtendedTcpTable(table, ref size, false, family, TcpTableClass.OwnerPidAll, 0));

        if (buffer == IntPtr.Zero)
            return;

        try
        {
            var count = Marshal.ReadInt32(buffer);
            var rowPtr = IntPtr.Add(buffer, sizeof(int));
            var stride = isV6 ? Marshal.SizeOf<MibTcp6RowOwnerPid>() : Marshal.SizeOf<MibTcpRowOwnerPid>();

            for (var i = 0; i < count; i++)
            {
                var address = IntPtr.Add(rowPtr, i * stride);

                string localAddress, remoteAddress;
                int localPort, remotePort, state, processId;

                if (isV6)
                {
                    var row = Marshal.PtrToStructure<MibTcp6RowOwnerPid>(address);
                    localAddress = FormatV6(row.LocalAddr, row.LocalScopeId);
                    remoteAddress = FormatV6(row.RemoteAddr, row.RemoteScopeId);
                    localPort = SwapPort(row.LocalPort);
                    remotePort = SwapPort(row.RemotePort);
                    state = (int)row.State;
                    processId = (int)row.OwningPid;
                }
                else
                {
                    var row = Marshal.PtrToStructure<MibTcpRowOwnerPid>(address);
                    localAddress = new IPAddress(row.LocalAddr).ToString();
                    remoteAddress = new IPAddress(row.RemoteAddr).ToString();
                    localPort = SwapPort(row.LocalPort);
                    remotePort = SwapPort(row.RemotePort);
                    state = (int)row.State;
                    processId = (int)row.OwningPid;
                }

                if (processId <= 0)
                    continue;

                var (name, path) = _processNames.Resolve(processId);
                rows.Add(new ConnectionInfo
                {
                    Protocol = TransportProtocol.Tcp,
                    ProcessId = processId,
                    ProcessName = name,
                    ExecutablePath = path,
                    LocalAddress = localAddress,
                    LocalPort = localPort,
                    RemoteAddress = remoteAddress,
                    RemotePort = remotePort,
                    State = TcpStateName(state)
                });
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private void ReadUdp(List<ConnectionInfo> rows, int family, bool isV6)
    {
        var buffer = QueryTable(
            (IntPtr table, ref int size) => GetExtendedUdpTable(table, ref size, false, family, UdpTableClass.OwnerPid, 0));

        if (buffer == IntPtr.Zero)
            return;

        try
        {
            var count = Marshal.ReadInt32(buffer);
            var rowPtr = IntPtr.Add(buffer, sizeof(int));
            var stride = isV6 ? Marshal.SizeOf<MibUdp6RowOwnerPid>() : Marshal.SizeOf<MibUdpRowOwnerPid>();

            for (var i = 0; i < count; i++)
            {
                var address = IntPtr.Add(rowPtr, i * stride);

                string localAddress;
                int localPort, processId;

                if (isV6)
                {
                    var row = Marshal.PtrToStructure<MibUdp6RowOwnerPid>(address);
                    localAddress = FormatV6(row.LocalAddr, row.LocalScopeId);
                    localPort = SwapPort(row.LocalPort);
                    processId = (int)row.OwningPid;
                }
                else
                {
                    var row = Marshal.PtrToStructure<MibUdpRowOwnerPid>(address);
                    localAddress = new IPAddress(row.LocalAddr).ToString();
                    localPort = SwapPort(row.LocalPort);
                    processId = (int)row.OwningPid;
                }

                if (processId <= 0)
                    continue;

                var (name, path) = _processNames.Resolve(processId);
                rows.Add(new ConnectionInfo
                {
                    Protocol = TransportProtocol.Udp,
                    ProcessId = processId,
                    ProcessName = name,
                    ExecutablePath = path,
                    LocalAddress = localAddress,
                    LocalPort = localPort,
                    RemoteAddress = string.Empty,
                    RemotePort = 0,
                    State = "Open"
                });
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private delegate uint TableQuery(IntPtr table, ref int size);

    /// <summary>Allocates the exact buffer the OS asks for, retrying if the table grows.</summary>
    private static IntPtr QueryTable(TableQuery query)
    {
        var size = 0;
        var result = query(IntPtr.Zero, ref size);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (size <= 0)
                return IntPtr.Zero;

            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                result = query(buffer, ref size);
                if (result == 0)
                    return buffer;
            }
            catch
            {
                Marshal.FreeHGlobal(buffer);
                throw;
            }

            Marshal.FreeHGlobal(buffer);

            if (result != ErrorInsufficientBuffer)
                return IntPtr.Zero;
        }

        return IntPtr.Zero;
    }

    private static string FormatV6(byte[] address, uint scopeId) =>
        new IPAddress(address, scopeId).ToString();

    private static int SwapPort(uint value) =>
        (((int)(value & 0xFF)) << 8) | (int)((value >> 8) & 0xFF);

    private static string TcpStateName(int state) => state switch
    {
        1 => "Closed",
        2 => "Listen",
        3 => "SynSent",
        4 => "SynReceived",
        5 => "Established",
        6 => "FinWait1",
        7 => "FinWait2",
        8 => "CloseWait",
        9 => "Closing",
        10 => "LastAck",
        11 => "TimeWait",
        12 => "DeleteTCB",
        _ => "Unknown"
    };

    private enum TcpTableClass
    {
        OwnerPidAll = 5
    }

    private enum UdpTableClass
    {
        OwnerPid = 1
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;
        public uint RemoteAddr;
        public uint RemotePort;
        public uint OwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcp6RowOwnerPid
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] LocalAddr;
        public uint LocalScopeId;
        public uint LocalPort;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] RemoteAddr;
        public uint RemoteScopeId;
        public uint RemotePort;
        public uint State;
        public uint OwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibUdpRowOwnerPid
    {
        public uint LocalAddr;
        public uint LocalPort;
        public uint OwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibUdp6RowOwnerPid
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] LocalAddr;
        public uint LocalScopeId;
        public uint LocalPort;
        public uint OwningPid;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr pTcpTable,
        ref int pdwSize,
        [MarshalAs(UnmanagedType.Bool)] bool bOrder,
        int ulAf,
        TcpTableClass tableClass,
        uint reserved);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedUdpTable(
        IntPtr pUdpTable,
        ref int pdwSize,
        [MarshalAs(UnmanagedType.Bool)] bool bOrder,
        int ulAf,
        UdpTableClass tableClass,
        uint reserved);
}
