using NetPaw.Planning;

namespace NetPaw.Adapters;

/// <summary>Wired 802.1X state of one adapter, from <c>netsh lan show interfaces</c> (needs the Wired AutoConfig service).</summary>
public sealed record Dot1xInfo(string Name, string State)
{
    static bool Has(string s, params string[] words) => words.Any(w => s.Contains(w, StringComparison.OrdinalIgnoreCase));
    /// <summary>The switch rejected the credentials/certificate: the port stays closed, DHCP never answers.</summary>
    public bool Failed => Has(State, "fail", "fehlgeschlagen", "échou");
    public bool InProgress => !Failed && Has(State, "authenticating", "wird authentifiziert", "en cours");
    /// <summary>Port does not do 802.1X at all — the normal case on unmanaged and most office ports.</summary>
    public bool NotSupported => Has(State, "does not support", "unterstützt keine", "ne prend pas");
}

public static class Dot1x
{
    /// <summary>Parses the "Name : X … State : Y" blocks; label language does not matter (Name/State keys are the same in the common locales, "Status" for German).</summary>
    public static List<Dot1xInfo> Parse(string text)
    {
        var list = new List<Dot1xInfo>();
        string? name = null, state = null;
        void Flush() { if (name is not null) list.Add(new Dot1xInfo(name, state ?? "")); name = state = null; }
        foreach (var raw in text.Split('\n'))
        {
            var colon = raw.IndexOf(':'); if (colon < 0) continue;
            var key = raw[..colon].Trim(); var val = raw[(colon + 1)..].Trim();
            if (key.Equals("Name", StringComparison.OrdinalIgnoreCase)) { Flush(); name = val; }
            else if (key.Equals("State", StringComparison.OrdinalIgnoreCase) || key.Equals("Status", StringComparison.OrdinalIgnoreCase) || key.Equals("État", StringComparison.OrdinalIgnoreCase)) state = val;
        }
        Flush();
        return list;
    }

    /// <summary>State for one adapter, or null when the service is off or the adapter is not listed.</summary>
    public static Dot1xInfo? Read(IStepRunner runner, string adapterName)
    {
        var r = runner.Run(Step.Netsh("Read 802.1X state", "lan show interfaces", critical: false));
        if (r.ExitCode != 0) return null;
        return Parse(r.Output).FirstOrDefault(d => string.Equals(d.Name, adapterName, StringComparison.OrdinalIgnoreCase));
    }
}
