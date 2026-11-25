using System.Runtime.InteropServices;

namespace DoorNet.Capture;

/// <summary>Machine-wide ICMP counters, for the traffic list's ping line.</summary>
public readonly record struct IcmpSnapshot(
    uint EchoRequestsIn,
    uint EchoRepliesIn,
    uint EchoRequestsOut,
    uint EchoRepliesOut)
{
    public bool HasActivity => EchoRequestsIn + EchoRepliesIn + EchoRequestsOut + EchoRepliesOut > 0;

    public string Summary =>
        $"echo in {EchoRequestsIn} · replies in {EchoRepliesIn} · echo out {EchoRequestsOut} · replies out {EchoRepliesOut}";
}

/// <summary>
/// Reads the OS ICMP counters. The connection tables do not list ICMP at all, so this is how
/// pings can be reported without a packet driver: totals only, not the peer address.
/// </summary>
public static class LocalTrafficStats
{
    public static IcmpSnapshot ReadIcmp()
    {
        try
        {
            if (GetIcmpStatistics(out var info) != 0)
                return default;

            return new IcmpSnapshot(
                info.InStats.Echo,
                info.InStats.EchoReplies,
                info.OutStats.Echo,
                info.OutStats.EchoReplies);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return default;
        }
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern int GetIcmpStatistics(out MibIcmp info);

    [StructLayout(LayoutKind.Sequential)]
    private struct MibIcmp
    {
        public IcmpStats InStats;
        public IcmpStats OutStats;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IcmpStats
    {
        public uint Messages;
        public uint Errors;
        public uint DestinationUnreachable;
        public uint TimeExceeded;
        public uint ParameterProblems;
        public uint SourceQuench;
        public uint Redirects;
        public uint Echo;
        public uint EchoReplies;
        public uint Timestamps;
        public uint TimestampReplies;
        public uint AddressMasks;
        public uint AddressMaskReplies;
    }
}
