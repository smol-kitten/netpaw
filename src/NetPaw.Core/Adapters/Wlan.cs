using NetPaw.Planning;

namespace NetPaw.Adapters;

/// <summary>SSID + BSSID of a connected Wi-Fi adapter. .NET has no API for it; <c>netsh wlan show interfaces</c> does.</summary>
public sealed record WlanInfo(string Name, string? Ssid, string? Bssid, int? Signal = null, int? Channel = null, string? Radio = null, int? ReceiveMbps = null, int? TransmitMbps = null)
{
    /// <summary>"Corp · ch 36 · 72 % · 866 Mbit/s" — the info card row.</summary>
    public string Summary => string.Join(" · ", new[] { Ssid, Channel is { } c ? $"ch {c}" : null, Signal is { } s ? $"{s} %" : null, ReceiveMbps is { } r ? $"{r} Mbit/s" : null, Radio }.Where(x => x is not null));
    public bool Weak => Signal is < 30;
}

public static class Wlan
{
    /// <summary>Parses the "Name : Wi-Fi / SSID : x / BSSID : aa:bb:…" blocks, one per interface, any locale (label text is ignored: the key is the token before the colon).</summary>
    public static List<WlanInfo> Parse(string text)
    {
        var list = new List<WlanInfo>();
        string? name = null, ssid = null, bssid = null, radio = null; int? signal = null, channel = null, rx = null, tx = null;
        void Flush() { if (name is not null) list.Add(new WlanInfo(name, ssid, bssid, signal, channel, radio, rx, tx)); name = ssid = bssid = radio = null; signal = channel = rx = tx = null; }
        static int? Num(string v) { var digits = new string(v.TakeWhile(c => char.IsAsciiDigit(c)).ToArray()); return int.TryParse(digits, out var n) ? n : null; }
        static bool Is(string key, params string[] names) => names.Any(n => key.StartsWith(n, StringComparison.OrdinalIgnoreCase));
        foreach (var raw in text.Split('\n'))
        {
            var colon = raw.IndexOf(':'); if (colon < 0) continue;
            var key = raw[..colon].Trim(); var val = raw[(colon + 1)..].Trim();
            if (key.Equals("Name", StringComparison.OrdinalIgnoreCase)) { Flush(); name = val; }
            else if (key.Equals("SSID", StringComparison.OrdinalIgnoreCase) && !key.Contains("AP", StringComparison.OrdinalIgnoreCase)) ssid = val.Length == 0 ? null : val;
            else if (key.Equals("BSSID", StringComparison.OrdinalIgnoreCase) || key.Equals("AP BSSID", StringComparison.OrdinalIgnoreCase)) bssid = val.Length == 0 ? null : val.Replace('-', ':').ToLowerInvariant();
            else if (Is(key, "Signal")) signal = Num(val);
            else if (Is(key, "Channel", "Kanal", "Canal")) channel = Num(val);
            else if (Is(key, "Radio type", "Funktyp", "Type de radio")) radio = val.Length == 0 ? null : val;
            else if (Is(key, "Receive rate", "Empfangsrate", "Débit de réception")) rx = Num(val);
            else if (Is(key, "Transmit rate", "Übertragungsrate", "Débit de transmission")) tx = Num(val);
        }
        Flush();
        return list;
    }

    /// <summary>Info for one adapter, or null when it is not a connected Wi-Fi interface.</summary>
    public static WlanInfo? Read(IStepRunner runner, string adapterName)
    {
        var r = runner.Run(Step.Netsh("Read Wi-Fi association", "wlan show interfaces", critical: false));
        return Parse(r.Output).FirstOrDefault(w => string.Equals(w.Name, adapterName, StringComparison.OrdinalIgnoreCase) && w.Bssid is not null);
    }
}
