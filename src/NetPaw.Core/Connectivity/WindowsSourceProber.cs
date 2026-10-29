using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace NetPaw.Connectivity;

/// <summary>Source-bound probes on Windows: ICMP through iphlpapi's IcmpSendEcho2Ex (takes a source address, no
/// elevation, no text parsing), TCP through a bound TcpClient, ARP through the existing SendARP wrapper.</summary>
public sealed class WindowsSourceProber(Arp.IArpProvider arp) : ISourceProber
{
    [DllImport("iphlpapi.dll")] static extern nint IcmpCreateFile();
    [DllImport("iphlpapi.dll")] static extern bool IcmpCloseHandle(nint handle);
    [DllImport("iphlpapi.dll")] static extern uint IcmpSendEcho2Ex(nint handle, nint evt, nint apc, nint apcCtx, uint source, uint destination,
        byte[] data, ushort size, ref IpOptionInformation options, byte[] reply, uint replySize, uint timeoutMs);
    [StructLayout(LayoutKind.Sequential)] struct IpOptionInformation { public byte Ttl, Tos, Flags, OptionsSize; public nint OptionsData; }
    const uint IP_REQ_TIMED_OUT = 11010, IP_TTL_EXPIRED_TRANSIT = 11013, IP_DEST_NET_UNREACHABLE = 11002, IP_DEST_HOST_UNREACHABLE = 11003, IP_DEST_PROT_UNREACHABLE = 11004, IP_DEST_PORT_UNREACHABLE = 11005;

    static uint Packed(string ip) => BitConverter.ToUInt32(IPAddress.Parse(ip).GetAddressBytes(), 0);

    public Task<SourceProbe> Ping(string target, string source, int ttl, int timeoutMs, CancellationToken ct) => Task.Run(() =>
    {
        if (!OperatingSystem.IsWindows()) return new SourceProbe("error", null, null);
        var h = IcmpCreateFile(); if (h == -1 || h == 0) return new SourceProbe("error", null, null);
        try
        {
            var opts = new IpOptionInformation { Ttl = (byte)Math.Clamp(ttl, 1, 255) };
            var data = new byte[32]; var reply = new byte[256];
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var n = IcmpSendEcho2Ex(h, 0, 0, 0, Packed(source), Packed(target), data, (ushort)data.Length, ref opts, reply, (uint)reply.Length, (uint)timeoutMs);
            // ICMP_ECHO_REPLY: Address (4), Status (4), RoundTripTime (4), ... ; on no reply the status is in GetLastError.
            var status = n > 0 ? BitConverter.ToUInt32(reply, 4) : (uint)Marshal.GetLastWin32Error();
            var from = n > 0 ? new IPAddress(BitConverter.ToUInt32(reply, 0)).ToString() : null;
            var ms = n > 0 ? (int)BitConverter.ToUInt32(reply, 8) : (int)sw.ElapsedMilliseconds;
            return status switch
            {
                0 => new SourceProbe("reply", from, ms),
                IP_TTL_EXPIRED_TRANSIT => new SourceProbe("ttl-expired", from, ms),
                IP_DEST_NET_UNREACHABLE or IP_DEST_HOST_UNREACHABLE or IP_DEST_PROT_UNREACHABLE or IP_DEST_PORT_UNREACHABLE => new SourceProbe("unreachable", from, ms),
                IP_REQ_TIMED_OUT => new SourceProbe("timeout", null, null),
                _ => new SourceProbe($"error {status}", from, null),
            };
        }
        finally { IcmpCloseHandle(h); }
    }, ct);

    public async Task<SourceProbe> Connect(string target, int port, string source, int timeoutMs, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var c = new TcpClient(new IPEndPoint(IPAddress.Parse(source), 0));
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct); cts.CancelAfter(timeoutMs);
            await c.ConnectAsync(IPAddress.Parse(target), port, cts.Token);
            return new SourceProbe("open", target, (int)sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new SourceProbe("timeout", null, null); }
        catch (SocketException ex) { return new SourceProbe(ex.SocketErrorCode == SocketError.ConnectionRefused ? "refused" : ex.SocketErrorCode is SocketError.HostUnreachable or SocketError.NetworkUnreachable ? "unreachable" : "error " + ex.SocketErrorCode, null, null); }
    }

    public async Task<string?> Arp(string ip, string source, CancellationToken ct) => (await arp.Probe(ip, source, ct)).Mac;
}
