using System.IO.Compression;
using NetPaw.Diagnostics;
using NetPaw.Model;
using NetPaw.Store;
using Xunit;

namespace NetPaw.Tests;

public class BundleTests
{
    [Fact]
    public void BundleRedactsSettingsAndListsFiles()
    {
        var dir = Directory.CreateTempSubdirectory("netpaw-diag");
        try
        {
            var store = new JsonStore(dir.FullName);
            File.WriteAllText(store.SettingsFile, """{"WorkAdapter":"Ethernet","RepoUrls":["https://x/index.json|ab:PUBKEY"],"LogExport":{"OtlpEndpoint":"https://otel","OtlpHeaders":["Authorization: Bearer s3cr3t"]},"Nested":{"ApiKey":"k"}}""");
            File.WriteAllText(store.LogFile, "log line");
            File.WriteAllText(store.IncidentsFile, "{\"at\":1}");
            var zipPath = Path.Combine(dir.FullName, "out.zip");
            var entries = Bundle.Write(zipPath, new FakeRunner(), store, [Fx.Adapter()], new Settings(), "0.9.0", "PC1", new DateTimeOffset(2026, 9, 16, 12, 0, 0, TimeSpan.Zero));
            Assert.Equal(["README.txt", "ipconfig-all.txt", "routes.txt", "interfaces.txt", "netsh-config.txt", "arp.txt", "dns-cache.txt", "adapters.json", "settings.json", "netpaw.log", "incidents.jsonl"], entries);   // no profiles.json/state.json: they do not exist here
            using var zip = ZipFile.OpenRead(zipPath);
            string Read(string n) { using var r = new StreamReader(zip.GetEntry(n)!.Open()); return r.ReadToEnd(); }
            var settings = Read("settings.json");
            Assert.DoesNotContain("s3cr3t", settings); Assert.Contains("[redacted]", settings); Assert.Contains("PUBKEY", settings); Assert.Contains("https://otel", settings);
            Assert.DoesNotContain("\"k\"", settings);
            Assert.StartsWith("> ipconfig /all", Read("ipconfig-all.txt")); Assert.Contains("Ok.", Read("arp.txt"));
            Assert.Contains("192.168.1.10", Read("adapters.json")); Assert.Contains("netpaw: 0.9.0", Read("README.txt")); Assert.Equal("log line", Read("netpaw.log"));
            Assert.Equal("netpaw-diag-PC1-20260916-1200.zip", Bundle.DefaultName("PC1", new DateTimeOffset(2026, 9, 16, 12, 0, 0, TimeSpan.Zero)));
            Assert.Equal("(settings.json unreadable)", Bundle.Redact("{not json"));
        }
        finally { dir.Delete(true); }
    }
}
