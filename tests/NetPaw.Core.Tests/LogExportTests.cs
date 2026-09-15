using System.Text.Json;
using NetPaw.Telemetry;
using Xunit;

namespace NetPaw.Tests;

public class LogExportTests
{
    static LogRecord Rec() => new(DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_123), "warn", "Internet lost — intranet still reachable",
        new Dictionary<string, object?> { ["adapter"] = "Ethernet 2", ["from"] = "Online", ["to"] = "IntranetOnly", ["attempt"] = 2, ["note"] = "has \"quotes\" and ]brackets" });

    [Fact]
    public void OtlpPayloadFollowsTheLogsProtoShape()
    {
        using var ex = new LogExporter(new LogExportSettings { OtlpEndpoint = "http://c:4318/v1/logs" }, "0.6.0", "PC1");
        var json = JsonSerializer.SerializeToNode(ex.OtlpPayload(Rec()))!;
        var rec = json["resourceLogs"]![0]!["scopeLogs"]![0]!["logRecords"]![0]!;
        Assert.Equal("1700000000123000000", rec["timeUnixNano"]!.GetValue<string>());
        Assert.Equal(13, rec["severityNumber"]!.GetValue<int>()); Assert.Equal("WARN", rec["severityText"]!.GetValue<string>());
        Assert.Equal("Internet lost — intranet still reachable", rec["body"]!["stringValue"]!.GetValue<string>());
        var attrs = rec["attributes"]!.AsArray();
        Assert.Contains(attrs, a => a!["key"]!.GetValue<string>() == "attempt" && a["value"]!["intValue"]!.GetValue<string>() == "2");
        var res = json["resourceLogs"]![0]!["resource"]!["attributes"]!.AsArray();
        Assert.Contains(res, a => a!["key"]!.GetValue<string>() == "service.name" && a["value"]!["stringValue"]!.GetValue<string>() == "netpaw");
    }

    [Fact]
    public void SyslogLineIsRfc5424WithEscapedStructuredData()
    {
        using var ex = new LogExporter(new LogExportSettings { SyslogServer = "127.0.0.1:5514", SyslogFacility = 1 }, "0.6.0", "PC1");
        var line = ex.SyslogLine(Rec());
        Assert.StartsWith("<12>1 2023-11-14T22:13:20.123Z PC1 netpaw - - [netpaw@0 ", line);   // facility 1 * 8 + warning 4
        Assert.Contains("adapter=\"Ethernet 2\"", line);
        Assert.Contains("note=\"has \\\"quotes\\\" and \\]brackets\"", line);
        Assert.EndsWith("] Internet lost — intranet still reachable", line);
    }

    [Fact]
    public void EndpointParsingAndHeaders()
    {
        Assert.Equal(5514, LogExporter.ParseEndpoint("127.0.0.1:5514", 514)!.Port);
        Assert.Equal(514, LogExporter.ParseEndpoint("127.0.0.1", 514)!.Port);
        Assert.Null(LogExporter.ParseEndpoint("", 514));
        Assert.Null(LogExporter.ParseEndpoint("bad host name", 514)); // spaces are never a host; no DNS involved
        var s = new LogExportSettings(); Assert.False(s.Enabled);
        s.SyslogServer = "x"; Assert.True(s.Enabled);
    }
}
