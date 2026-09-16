using System.Net;
using System.Net.Sockets;

namespace NetPaw.Connectivity;

/// <summary>
/// The smallest DNS client that answers "does this server respond?": one A query over UDP, one reply.
/// Any well-formed reply (including NXDOMAIN or REFUSED) means the server is alive; that is the question
/// the advisor asks. .NET's resolver cannot target one server, hence the packet code.
/// </summary>
public static class DnsQuery
{
    public static byte[] BuildQuery(ushort id, string name)
    {
        var labels = name.Trim().TrimEnd('.').Split('.', StringSplitOptions.RemoveEmptyEntries);
        var q = new List<byte> { (byte)(id >> 8), (byte)id, 0x01, 0x00, 0, 1, 0, 0, 0, 0, 0, 0 };   // RD=1, QDCOUNT=1
        foreach (var l in labels) { var b = System.Text.Encoding.ASCII.GetBytes(l); q.Add((byte)b.Length); q.AddRange(b); }
        q.AddRange([0, 0, 1, 0, 1]);                                                                    // root, QTYPE A, QCLASS IN
        return [.. q];
    }

    /// <summary>Parses a reply: matching id, QR bit, rcode, and the first A record if any. Returns false for garbage.</summary>
    public static bool TryParse(byte[] r, ushort id, out int rcode, out string? firstA)
    {
        rcode = -1; firstA = null;
        if (r.Length < 12 || (ushort)((r[0] << 8) | r[1]) != id || (r[2] & 0x80) == 0) return false;
        rcode = r[3] & 0x0F;
        int qd = (r[4] << 8) | r[5], an = (r[6] << 8) | r[7], pos = 12;
        try
        {
            for (var i = 0; i < qd; i++) { pos = SkipName(r, pos) + 4; }
            for (var i = 0; i < an; i++)
            {
                pos = SkipName(r, pos);
                int type = (r[pos] << 8) | r[pos + 1], len = (r[pos + 8] << 8) | r[pos + 9]; pos += 10;
                if (type == 1 && len == 4) { firstA = $"{r[pos]}.{r[pos + 1]}.{r[pos + 2]}.{r[pos + 3]}"; break; }
                pos += len;
            }
        }
        catch (IndexOutOfRangeException) { return rcode >= 0; }
        return true;
    }

    static int SkipName(byte[] r, int pos)
    {
        while (true)
        {
            var len = r[pos];
            if (len == 0) return pos + 1;
            if ((len & 0xC0) == 0xC0) return pos + 2;      // compression pointer ends the name
            pos += len + 1;
        }
    }

    public static string RcodeName(int rcode) => rcode switch { 0 => "NOERROR", 1 => "FORMERR", 2 => "SERVFAIL", 3 => "NXDOMAIN", 4 => "NOTIMP", 5 => "REFUSED", _ => $"RCODE{rcode}" };

    /// <summary>One query to one server. Ok = a reply came back at all.</summary>
    public static async Task<ProbeResult> Ask(string server, string name, int timeoutMs, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        if (!IPAddress.TryParse(server, out var ip)) return new ProbeResult(server, false, 0, "not an address");
        var id = (ushort)Random.Shared.Next(1, 65535);
        try
        {
            using var udp = new UdpClient(ip.AddressFamily);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct); cts.CancelAfter(timeoutMs);
            await udp.SendAsync(BuildQuery(id, name), new IPEndPoint(ip, 53), cts.Token);
            while (true)
            {
                var res = await udp.ReceiveAsync(cts.Token);
                if (!TryParse(res.Buffer, id, out var rcode, out var a)) continue;      // a stray datagram for another id
                return new ProbeResult(server, true, (int)sw.ElapsedMilliseconds, a ?? RcodeName(rcode));
            }
        }
        catch (OperationCanceledException) { return new ProbeResult(server, false, (int)sw.ElapsedMilliseconds, "no answer"); }
        catch (SocketException ex) { return new ProbeResult(server, false, (int)sw.ElapsedMilliseconds, ex.SocketErrorCode.ToString()); }
    }
}
