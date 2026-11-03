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
    public void A_v1_status_request_is_still_answered_and_a_v2_request_carries_the_identity()
    {
        // G5 review decision 1: a 0.16 tray or CLI may still ask Status (so it can print "upgrade"); every v1 change intent is refused.
        var (v1, err) = ServiceProtocol.ParseRequest(Encoding.UTF8.GetBytes("{\"v\":1,\"kind\":\"Status\"}"));
        Assert.Null(err); Assert.Equal(RequestKind.Status, v1!.Kind); Assert.Equal(1, v1.V);
        Assert.Equal(2, ServiceProtocol.Version);
        var req = new ServiceRequest(ServiceProtocol.Version, RequestKind.Dhcp) { Adapter = "Ethernet 2", AdapterId = "{E1}", AdapterMac = "AA:BB:CC:DD:EE:FF" };
        var (back, err2) = ServiceProtocol.ParseRequest(ServiceProtocol.Encode(req)[..^1]);
        Assert.Null(err2); Assert.Equal("{E1}", back!.AdapterId); Assert.Equal("AA:BB:CC:DD:EE:FF", back.AdapterMac); Assert.Equal("Ethernet 2", back.Adapter);
    }

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
    [InlineData("{\"v\":3,\"kind\":\"Status\"}", "protocol version 3 is not supported")]
    [InlineData("{\"kind\":\"Status\"}", "protocol version 0 is not supported")]          // v missing
    [InlineData("{\"v\":0,\"kind\":\"Dhcp\"}", "protocol version 0 is not supported")]
    [InlineData("{\"v\":-1,\"kind\":\"Status\"}", "protocol version -1 is not supported")]
    [InlineData("{\"v\":1,\"kind\":\"ApplyProfile\"}", "protocol v1 is no longer accepted for changes: upgrade the client")]
    [InlineData("{\"v\":1,\"kind\":\"Dhcp\",\"adapter\":\"Ethernet\"}", "upgrade the client")]
    [InlineData("{\"v\":1,\"kind\":\"RevertLastApply\"}", "upgrade the client")]
    [InlineData("{\"v\":1}", "unknown request kind")]
    [InlineData("{\"v\":1,\"kind\":\"None\"}", "unknown request kind")]
    [InlineData("{\"v\":1,\"kind\":\"RunCommand\"}", "invalid JSON")]
    [InlineData("{\"v\":1,\"kind\":99}", "invalid JSON")]
    [InlineData("{\"v\":1,\"kind\":1}", "invalid JSON")]
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

    [Fact]
    public async Task Status_response_round_trips_through_the_client_parser()
    {
        var wire = ServiceProtocol.Encode(new ServiceResponse(ServiceProtocol.Version, true, Status: new("0.14.0", 1, @"PC\anna", 2)));
        var line = await ServiceProtocol.ReadLine(new MemoryStream(wire));
        var back = System.Text.Json.JsonSerializer.Deserialize<ServiceResponse>(line!, ServiceProtocol.Json)!;
        Assert.True(back.Ok);
        Assert.NotNull(back.Status);
        Assert.Equal(@"PC\anna", back.Status!.Caller);
        Assert.Equal(2, back.Status.CallerSession);
        Assert.Null(back.Status.Tier);   // a 0.14/0.15 service answers without a tier
    }

    [Fact]
    public void Status_carries_the_effective_tier()
    {
        var wire = ServiceProtocol.Encode(new ServiceResponse(ServiceProtocol.Version, true, Status: new("0.16.0", 1, @"PC\anna", 2, "NetworkConfigOperators")));
        var back = System.Text.Json.JsonSerializer.Deserialize<ServiceResponse>(wire, ServiceProtocol.Json)!;
        Assert.Equal("NetworkConfigOperators", back.Status!.Tier);
        Assert.Contains("\"tier\":\"NetworkConfigOperators\"", System.Text.Encoding.UTF8.GetString(wire));
    }

    [Fact]
    public void A_request_built_on_a_live_adapter_carries_its_id_and_mac()
    {
        var a = Fx.Adapter("Ethernet 2");
        foreach (var r in new[] { ServiceChange.Dhcp(a), ServiceChange.Renew(a), ServiceChange.Release(a), ServiceChange.ResetAdapter(a), ServiceChange.Prefer(a),
                                  ServiceChange.SetMtu(1400, a), ServiceChange.Reach("10.0.0.1", a, true), ServiceChange.DeleteRoute("10.0.0.0/8", null, a),
                                  ServiceChange.Profile(Fx.Static("lab"), a), ServiceChange.Managed("corp", a) })
        {
            Assert.Equal("Ethernet 2", r.Adapter); Assert.Equal(Fx.Id("Ethernet 2"), r.AdapterId); Assert.Equal("AA:BB:CC:DD:EE:FF", r.AdapterMac);
        }
        Assert.Null(ServiceChange.Dhcp("Ethernet 2").AdapterId);   // name-only callers still work, the service logs them as pre-0.17
    }

    [Fact]
    public void Confirm_requests_name_the_adapter_the_same_way_and_a_window_is_attached_only_when_asked()
    {
        var a = Fx.Adapter("Ethernet 2");
        var c = ServiceChange.Confirm(a);
        Assert.Equal(RequestKind.ConfirmApply, c.Kind);
        Assert.Equal(("Ethernet 2", Fx.Id("Ethernet 2"), "AA:BB:CC:DD:EE:FF"), (c.Adapter, c.AdapterId, c.AdapterMac));
        Assert.Null(ServiceChange.Confirm().Adapter);   // "the only armed window"
        Assert.Equal("Ethernet 2", ServiceChange.Confirm("Ethernet 2").Adapter);
        Assert.Equal(30, ServiceChange.Dhcp(a).WithConfirm(30).ConfirmSeconds);
        Assert.Null(ServiceChange.Dhcp(a).WithConfirm(0).ConfirmSeconds);
        Assert.Null(ServiceChange.Dhcp(a).WithConfirm(null).ConfirmSeconds);
        Assert.True(ServiceChange.CanConfirm(RequestKind.ApplyProfile) && ServiceChange.CanConfirm(RequestKind.Dhcp) && ServiceChange.CanConfirm(RequestKind.Reach) && ServiceChange.CanConfirm(RequestKind.RevertLastApply));
        Assert.False(ServiceChange.CanConfirm(RequestKind.ResetAdapter) || ServiceChange.CanConfirm(RequestKind.Status) || ServiceChange.CanConfirm(RequestKind.ConfirmApply));
        // the armed seconds travel on the wire as their own field, so a client can show the policy-raised window
        var wire = ServiceProtocol.Encode(new ServiceResponse(ServiceProtocol.Version, true) { Apply = new ApplyReport("lab", "Ethernet 2", true, false, [], [], []) { ConfirmArmed = true, ConfirmSeconds = 20 } });
        var back = System.Text.Json.JsonSerializer.Deserialize<ServiceResponse>(wire, ServiceProtocol.Json)!;
        Assert.Equal((true, 20), (back.Apply!.ConfirmArmed, back.Apply.ConfirmSeconds));
    }

    [Fact]
    public void An_adapter_without_a_readable_id_or_mac_sends_null_not_empty()
    {
        var r = ServiceChange.Dhcp(Fx.Adapter("Ethernet 2") with { Id = "", Mac = " " });
        Assert.Equal("Ethernet 2", r.Adapter); Assert.Null(r.AdapterId); Assert.Null(r.AdapterMac);
    }
}
