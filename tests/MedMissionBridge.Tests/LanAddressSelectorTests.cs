using System.Net;
using MedMissionBridge.Mdns;

namespace MedMissionBridge.Tests;

public class LanAddressSelectorTests
{
    static NicCandidate Nic(string ip, bool hasGateway, string description = "Intel(R) Ethernet Connection") =>
        new(IPAddress.Parse(ip), hasGateway, description);

    [Fact]
    public void picks_the_routable_nic_over_a_hypervisor_host_adapter()
    {
        var selected = LanAddressSelector.Select(new[]
        {
            Nic("172.18.112.1", hasGateway: false, "Hyper-V Virtual Ethernet Adapter"),
            Nic("192.168.8.54", hasGateway: true),
        });

        Assert.Equal(new[] { IPAddress.Parse("192.168.8.54") }, selected);
    }

    [Fact]
    public void drops_virtual_adapters_even_when_they_advertise_a_gateway()
    {
        var selected = LanAddressSelector.Select(new[]
        {
            Nic("192.168.56.1", hasGateway: true, "VirtualBox Host-Only Ethernet Adapter"),
            Nic("192.168.8.54", hasGateway: true),
        });

        Assert.Equal(new[] { IPAddress.Parse("192.168.8.54") }, selected);
    }

    [Fact]
    public void keeps_a_gatewayless_nic_when_nothing_has_a_gateway()
    {
        // A field switch with no router hands out no default gateway. Advertising
        // nothing there would be worse than advertising the one real address.
        var selected = LanAddressSelector.Select(new[]
        {
            Nic("192.168.8.54", hasGateway: false),
        });

        Assert.Equal(new[] { IPAddress.Parse("192.168.8.54") }, selected);
    }

    [Fact]
    public void keeps_virtual_adapters_when_they_are_the_only_candidates()
    {
        // The description heuristic must never leave us with an empty set while a
        // usable address exists — a wrong address still beats no discovery at all.
        var selected = LanAddressSelector.Select(new[]
        {
            Nic("172.18.112.1", hasGateway: false, "Hyper-V Virtual Ethernet Adapter"),
        });

        Assert.Equal(new[] { IPAddress.Parse("172.18.112.1") }, selected);
    }

    [Fact]
    public void ignores_loopback_and_link_local_addresses()
    {
        var selected = LanAddressSelector.Select(new[]
        {
            Nic("127.0.0.1", hasGateway: false),
            Nic("169.254.13.7", hasGateway: false),
        });

        Assert.Empty(selected);
    }

    [Fact]
    public void keeps_every_routable_address_when_several_are_real()
    {
        // Wired and wireless on the same laptop: tablets may reach either one.
        var selected = LanAddressSelector.Select(new[]
        {
            Nic("192.168.8.54", hasGateway: true),
            Nic("192.168.8.77", hasGateway: true, "Intel(R) Wi-Fi 6 AX201"),
        });

        Assert.Equal(
            new[] { IPAddress.Parse("192.168.8.54"), IPAddress.Parse("192.168.8.77") },
            selected);
    }
}
