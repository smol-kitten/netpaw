using System.Text;
using NetPaw.Ipc;
using Xunit;

namespace NetPaw.Tests;

/// <summary>The pipe wire format (docs/PLAN-v0.14.md, "IPC"). The ACL itself can only be tested on Windows
/// and is checked by the verify-windows CI job.</summary>
public class ServiceProtocolTests
{
    static MemoryStream Bytes(string s) => new(Encoding.UTF8.GetBytes(s));

    [Fact]
    public async Task Request_round_trips_as_one_line()
    {
        var wire = ServiceProtocol.Encode(new ServiceRequest(ServiceProtocol.Version, RequestKind.Status));
        Assert.Equal((byte)'\n', wire[^1]);
        Assert.Single(wire, (byte)'\n');
        var line = await ServiceProtocol.ReadLine(new MemoryStream(wire));
        var (req, err) = ServiceProtocol.ParseRequest(line!);
        Assert.Null(err);
        Assert.Equal(RequestKind.Status, req!.Kind);
        Assert.Contains("\"kind\":\"Status\"", Encoding.UTF8.GetString(wire));
    }

    [Fact]
    public async Task Reader_stops_at_the_size_limit()
    {
        var huge = new string('a', ServiceProtocol.MaxLine + 10) + "\n";
        await Assert.ThrowsAsync<InvalidDataException>(() => ServiceProtocol.ReadLine(Bytes(huge)));
    }

    [Fact]
    public async Task Reader_refuses_a_message_cut_off_by_the_client()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => ServiceProtocol.ReadLine(Bytes("{\"v\":1,\"kind\":\"Sta")));
        Assert.Null(await ServiceProtocol.ReadLine(Bytes("")));
    }

    [Fact]
    public void Encoder_refuses_an_oversized_message()
    {
        Assert.Throws<InvalidDataException>(() => ServiceProtocol.Encode(new { pad = new string('x', ServiceProtocol.MaxLine) }));
    }

    [Theory]
    [InlineData("not json", "invalid JSON")]
    [InlineData("null", "empty request")]
    [InlineData("{\"v\":2,\"kind\":\"Status\"}", "protocol version 2 is not supported")]
    [InlineData("{\"v\":1}", "unknown request kind")]
    [InlineData("{\"v\":1,\"kind\":\"None\"}", "unknown request kind")]
    [InlineData("{\"v\":1,\"kind\":\"RunCommand\"}", "invalid JSON")]
    [InlineData("{\"v\":1,\"kind\":99}", "unknown request kind")]
    public void Bad_requests_are_refused_with_a_reason(string body, string reason)
    {
        var (req, err) = ServiceProtocol.ParseRequest(Encoding.UTF8.GetBytes(body));
        Assert.Null(req);
        Assert.Contains(reason, err);
    }

    [Fact]
    public void Response_carries_the_caller_the_service_saw()
    {
        var wire = ServiceProtocol.Encode(new ServiceResponse(1, true, Status: new("0.14.0", 1, @"PC\anna", 2)));
        var text = Encoding.UTF8.GetString(wire);
        Assert.Contains("\"caller\":\"PC\\\\anna\"", text);
        Assert.DoesNotContain("\"error\"", text);
    }
}
