using System.Net;
using System.Net.Sockets;
using NetPaw.Model;

namespace NetPaw.Net;

/// <summary>Sends magic packets. Injected so the map and CLI are tested without a socket.</summary>
public interface IWol
{
    Task Send(byte[] packet, IReadOnlyList<IPEndPoint> targets, CancellationToken ct);
}

/// <summary>
/// Wake-on-LAN: 6×FF followed by the MAC 16 times, UDP port 9, sent to the global broadcast and to the
/// subnet broadcast of the adapter the MAC was seen on. Routers do not forward it — same segment only.
/// </summary>
public static class WakeOnLan
{
    public const int Port = 9;

    public static bool TryParseMac(string text, out byte[] mac)
    {
        mac = [];
        var hex = new string(text.Where(Uri.IsHexDigit).ToArray());
        var t = text.Trim();
        if (hex.Length != 12 || !t.All(c => Uri.IsHexDigit(c) || c is ':' or '-' or '.') || t.Count(c => c is ':' or '-' or '.') is not (0 or 2 or 5)) return false;
        mac = Enumerable.Range(0, 6).Select(i => Convert.ToByte(hex.Substring(i * 2, 2), 16)).ToArray();
        return true;
    }

    public static byte[] MagicPacket(byte[] mac)
    {
        if (mac.Length != 6) throw new ArgumentException("MAC must be 6 bytes", nameof(mac));
        var p = new byte[6 + 16 * 6];
        Array.Fill(p, (byte)0xFF, 0, 6);
        for (var i = 0; i < 16; i++) mac.CopyTo(p, 6 + i * 6);
        return p;
    }

    /// <summary>Where to send: the global broadcast plus the directed broadcast of <paramref name="subnet"/> when given.</summary>
    public static List<IPEndPoint> Targets(IpAddr? subnet)
    {
        var list = new List<IPEndPoint> { new(IPAddress.Broadcast, Port) };
        if (subnet is not null && subnet.PrefixLength < 31)
        {
            var bcast = IpMath.FromUInt(IpMath.ToUInt(subnet.Address) | ~IpMath.PrefixToMaskBits(subnet.PrefixLength));
            list.Add(new(IPAddress.Parse(bcast), Port));
        }
        return list;
    }

    public static Task Wake(IWol wol, string mac, IpAddr? subnet, CancellationToken ct = default)
    {
        if (!TryParseMac(mac, out var bytes)) throw new FormatException($"'{mac}' is not a MAC address");
        return wol.Send(MagicPacket(bytes), Targets(subnet), ct);
    }
}

public sealed class UdpWol : IWol
{
    public async Task Send(byte[] packet, IReadOnlyList<IPEndPoint> targets, CancellationToken ct)
    {
        using var udp = new UdpClient { EnableBroadcast = true };
        foreach (var t in targets)
            for (var i = 0; i < 3; i++) { await udp.SendAsync(packet, t, ct); await Task.Delay(50, ct); }   // three times: sleeping NICs miss the first one
    }
}
