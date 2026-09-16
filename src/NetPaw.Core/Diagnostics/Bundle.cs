using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using NetPaw.Adapters;
using NetPaw.Model;
using NetPaw.Planning;
using NetPaw.Store;

namespace NetPaw.Diagnostics;

/// <summary>
/// The zip helpdesk asks for: ipconfig /all, the route table, the ARP cache, netsh config, NetPaw's own view of
/// the adapters, its log, the incident log and the settings. Addresses stay in (the admin sent it on purpose);
/// anything that could be a credential (export headers, tokens) is redacted.
/// </summary>
public static class Bundle
{
    public static string DefaultName(string machine, DateTimeOffset now) => $"netpaw-diag-{machine}-{now:yyyyMMdd-HHmm}.zip";

    /// <summary>Writes the zip and returns the entry names in order.</summary>
    public static List<string> Write(string zipPath, IStepRunner runner, JsonStore store, IReadOnlyList<AdapterInfo> adapters, Settings settings, string version, string machine, DateTimeOffset now)
    {
        var entries = new List<string>();
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        void Text(string name, string content) { var e = zip.CreateEntry(name); using var w = new StreamWriter(e.Open()); w.Write(content); entries.Add(name); }
        void Command(string name, string file, string args) { var r = runner.Run(new Step(name, file, args, Critical: false, ExpectFailure: true)); Text(name, $"> {file} {args}\r\n(exit {r.ExitCode})\r\n\r\n{r.Output}"); }
        void FileIfExists(string path, string name) { if (File.Exists(path)) Text(name, File.ReadAllText(path)); }

        Text("README.txt", $"NetPaw diagnostics\r\nmachine: {machine}\r\nnetpaw: {version}\r\nwindows: {Environment.OSVersion}\r\ntaken: {now:O}\r\n\r\nContains addresses and adapter names by design. Credentials are redacted.\r\n");
        Command("ipconfig-all.txt", "ipconfig", "/all");
        Command("routes.txt", "netsh", "interface ipv4 show route");
        Command("interfaces.txt", "netsh", "interface ipv4 show interfaces");
        Command("netsh-config.txt", "netsh", "interface ipv4 show config");
        Command("arp.txt", "arp", "-a");
        Command("dns-cache.txt", "ipconfig", "/displaydns");
        Text("adapters.json", JsonSerializer.Serialize(adapters, JsonStore.Json));
        Text("settings.json", Redact(File.Exists(store.SettingsFile) ? File.ReadAllText(store.SettingsFile) : JsonSerializer.Serialize(settings, JsonStore.Json)));
        FileIfExists(store.ProfilesFile, "profiles.json");
        FileIfExists(store.StateFile, "state.json");
        FileIfExists(store.LogFile, "netpaw.log");
        FileIfExists(store.IncidentsFile, "incidents.jsonl");
        return entries;
    }

    static readonly string[] SecretKeys = ["otlpHeaders", "OtlpHeaders", "token", "Token", "password", "Password", "apiKey", "ApiKey"];

    /// <summary>Settings JSON with credential-shaped values replaced; repository URLs and public keys stay (they are public).</summary>
    public static string Redact(string settingsJson)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(settingsJson); } catch (JsonException) { return "(settings.json unreadable)"; }
        Walk(root);
        return root?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "";

        static void Walk(JsonNode? n)
        {
            switch (n)
            {
                case JsonObject o:
                    foreach (var key in o.Select(kv => kv.Key).ToList())
                    {
                        if (SecretKeys.Any(s => key.Contains(s, StringComparison.OrdinalIgnoreCase))) o[key] = "[redacted]";
                        else Walk(o[key]);
                    }
                    break;
                case JsonArray a: foreach (var item in a) Walk(item); break;
            }
        }
    }
}
