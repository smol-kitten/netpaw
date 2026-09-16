using NetPaw.Model;
using NetPaw.Net;

namespace NetPaw.Planning;

/// <summary>One line of the IPv4 route table as netsh prints it.</summary>
public sealed record RouteEntry(string Prefix, int InterfaceIndex, string? Gateway, int Metric);

/// <summary>
/// Reads what Windows actually holds after an apply: the route table and the interface metric.
/// .NET exposes neither, so this parses <c>netsh interface ipv4 show route</c> / <c>show interfaces</c>.
/// </summary>
public static class RouteTable
{
    /// <summary>Parses "Publish  Type  Met  Prefix  Idx  Gateway/Interface Name" rows. Any locale: only the column shape matters.</summary>
    public static List<RouteEntry> Parse(string showRouteText)
    {
        var list = new List<RouteEntry>();
        foreach (var raw in showRouteText.Split('\n'))
        {
            var t = raw.Trim().Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (t.Length < 6 || !t[3].Contains('/') || !int.TryParse(t[2], out var met) || !int.TryParse(t[4], out var idx)) continue;
            var rest = string.Join(' ', t.Skip(5));
            var gw = System.Net.IPAddress.TryParse(rest, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? rest : null;
            var slash = t[3].IndexOf('/');
            if (!int.TryParse(t[3][(slash + 1)..], out var len)) continue;
            list.Add(new RouteEntry($"{IpMath.NetworkOf(t[3][..slash], len)}/{len}", idx, gw, met));
        }
        return list;
    }

    /// <summary>Parses "Idx  Met  MTU  State  Name" rows into index → interface metric.</summary>
    public static Dictionary<int, int> ParseInterfaceMetrics(string showInterfacesText)
    {
        var d = new Dictionary<int, int>();
        foreach (var raw in showInterfacesText.Split('\n'))
        {
            var t = raw.Trim().Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (t.Length >= 5 && int.TryParse(t[0], out var idx) && int.TryParse(t[1], out var met)) d[idx] = met;
        }
        return d;
    }

    /// <summary>Routes on one interface plus its metric, read through the step runner (so the same elevation/path rules apply as for an apply).</summary>
    public static (IReadOnlyList<RouteEntry> Routes, int? InterfaceMetric) Read(IStepRunner runner, int interfaceIndex)
    {
        var routes = runner.Run(Step.Netsh("Read route table", "interface ipv4 show route", critical: false));
        var ifs = runner.Run(Step.Netsh("Read interface metrics", "interface ipv4 show interfaces", critical: false));
        var metrics = ParseInterfaceMetrics(ifs.Output);
        return (Parse(routes.Output).Where(r => r.InterfaceIndex == interfaceIndex).ToList(), metrics.TryGetValue(interfaceIndex, out var m) ? m : null);
    }
}
