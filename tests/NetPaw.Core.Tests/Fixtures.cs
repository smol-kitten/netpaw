using NetPaw.Adapters;
using NetPaw.Model;
using NetPaw.Planning;

namespace NetPaw.Tests;

static class Fx
{
    public static AdapterInfo Adapter(string name = "Ethernet", bool dhcp = false, string[]? addrs = null, string? gw = null, bool up = true) =>
        new(name, "Intel I219", "{guid}", 7, up, true, dhcp,
            (addrs ?? ["192.168.1.10/24"]).Select(IpAddr.Parse).ToList(),
            gw is null ? [] : [gw], ["192.168.1.1"], "AA:BB:CC:DD:EE:FF");

    /// <summary>One builder for every "live adapter" a test needs; the per-file copies used to drift.</summary>
    public static AdapterInfo Nic(string name = "Ethernet", string desc = "x", bool up = true, bool physical = true, bool dhcp = false,
        string[]? addrs = null, string? gw = null, string[]? dns = null, bool hyperv = false, bool wireless = false, int index = 1, string? dhcpServer = null) =>
        new(name, desc, "{" + name + "}", index, up, physical, dhcp, (addrs ?? []).Select(IpAddr.Parse).ToList(), gw is null ? [] : [gw], dns ?? [], "AA")
        { HyperVVirtual = hyperv, Wireless = wireless, DhcpServers = dhcpServer is null ? [] : [dhcpServer] };

    public static Profile Static(string name, string? gw = "192.168.1.1", params string[] addrs) => new()
    {
        Name = name, Addresses = addrs.Length == 0 ? [IpAddr.Parse("192.168.1.10/24")] : addrs.Select(IpAddr.Parse).ToList(), Gateway = gw, Dns = ["1.1.1.1", "9.9.9.9"],
    };
}

/// <summary>Records steps instead of running them; optionally fails a step matching a substring.</summary>
sealed class FakeRunner(string? failContaining = null) : IStepRunner
{
    public List<Step> Ran { get; } = [];
    public StepResult Run(Step step)
    {
        Ran.Add(step);
        var fail = failContaining is not null && step.Arguments.Contains(failContaining);
        return new StepResult(step, fail ? 1 : 0, fail ? "The object already exists." : "Ok.");
    }
}

sealed class FakeAdapters(params AdapterInfo[] adapters) : IAdapterProvider
{
    public IReadOnlyList<AdapterInfo> GetAdapters() => adapters;
}

sealed class FakeVlan(bool capable, int? id = null) : IVlanProvider
{
    public VlanInfo Query(string adapterName) => new(capable, id, capable ? "VlanID" : null);
}
