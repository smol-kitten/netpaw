using NetPaw.Adapters;

namespace NetPaw.Connectivity;

public enum VpnKind { WireGuard, OpenVpn, WindowsNative, Tailscale, ZeroTier, Other }

/// <summary>A VPN adapter as seen from the OS: what it is, whether it is up, and whether it owns the default route.</summary>
public sealed record VpnInfo(string Adapter, VpnKind Kind, bool Up, bool HasAddress, bool FullTunnel)
{
    public string Mode => !Up ? "down" : FullTunnel ? "full tunnel (default route)" : "split tunnel";
    public override string ToString() => $"{Kind} '{Adapter}': {Mode}";
}

/// <summary>
/// Recognises VPN adapters by driver description/name — no vendor APIs, nothing sent. "Full tunnel"
/// means the VPN adapter carries a default gateway (0.0.0.0/0), which is how Windows expresses
/// "everything goes through the tunnel"; split tunnels only add specific routes.
/// </summary>
public static class VpnDetector
{
    static readonly (string Needle, VpnKind Kind)[] Signatures =
    [
        ("wireguard", VpnKind.WireGuard), ("wintun", VpnKind.WireGuard),
        ("tap-windows", VpnKind.OpenVpn), ("openvpn", VpnKind.OpenVpn), ("ovpn", VpnKind.OpenVpn), ("data channel offload", VpnKind.OpenVpn),
        ("tailscale", VpnKind.Tailscale), ("zerotier", VpnKind.ZeroTier),
        ("wan miniport (ikev2)", VpnKind.WindowsNative), ("wan miniport (l2tp)", VpnKind.WindowsNative), ("wan miniport (sstp)", VpnKind.WindowsNative), ("wan miniport (pptp)", VpnKind.WindowsNative),
        ("cisco anyconnect", VpnKind.Other), ("fortinet", VpnKind.Other), ("fortissl", VpnKind.Other), ("globalprotect", VpnKind.Other), ("pangp", VpnKind.Other), ("juniper", VpnKind.Other), ("pulse secure", VpnKind.Other), ("sonicwall", VpnKind.Other), ("checkpoint", VpnKind.Other),
    ];

    public static VpnKind? Classify(string name, string description)
    {
        var hay = (name + " " + description).ToLowerInvariant();
        foreach (var (needle, kind) in Signatures) if (hay.Contains(needle)) return kind;
        return null;
    }

    /// <summary>All VPN adapters including down ones (a WireGuard tunnel that is configured but not connected is still worth showing).</summary>
    public static List<VpnInfo> Detect(IEnumerable<AdapterInfo> adapters)
    {
        var list = new List<VpnInfo>();
        foreach (var a in adapters)
        {
            if (Classify(a.Name, a.Description) is not { } kind) continue;
            // Windows "WAN Miniport" adapters exist even without a VPN configured; only report them when up.
            if (kind == VpnKind.WindowsNative && !a.Up) continue;
            list.Add(new VpnInfo(a.Name, kind, a.Up && a.HasRealAddress, a.HasRealAddress, a.Up && a.HasGateway));
        }
        return list.OrderByDescending(v => v.Up).ThenBy(v => v.Adapter).ToList();
    }

    /// <summary>What changed between two detections, in one sentence each; empty when nothing changed.</summary>
    public static List<string> Changes(IReadOnlyList<VpnInfo> before, IReadOnlyList<VpnInfo> after)
    {
        var msgs = new List<string>();
        foreach (var v in after)
        {
            var old = before.FirstOrDefault(b => b.Adapter == v.Adapter);
            if (old is null && v.Up) msgs.Add($"VPN up: {v.Kind} '{v.Adapter}' — {v.Mode}.");
            else if (old is not null && old.Up != v.Up) msgs.Add(v.Up ? $"VPN up: {v.Kind} '{v.Adapter}' — {v.Mode}." : $"VPN down: {v.Kind} '{v.Adapter}'.");
            else if (old is not null && v.Up && old.FullTunnel != v.FullTunnel) msgs.Add($"VPN '{v.Adapter}' is now a {v.Mode}.");
        }
        foreach (var b in before.Where(b => b.Up && after.All(a => a.Adapter != b.Adapter))) msgs.Add($"VPN down: {b.Kind} '{b.Adapter}' (adapter gone).");
        return msgs;
    }
}
