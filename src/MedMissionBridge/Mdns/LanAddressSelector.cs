using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace MedMissionBridge.Mdns;

/// <summary>One IPv4 address on a NIC, with the two signals used to judge whether
/// a tablet on the field network could actually reach it.</summary>
public sealed record NicCandidate(IPAddress Address, bool HasGateway, string Description);

/// <summary>
/// Chooses which addresses mDNS advertises. Left to itself the mDNS library
/// publishes every local address, so a laptop running Hyper-V, VirtualBox or a
/// VPN also publishes host-only addresses like 172.18.112.1. Tablets resolve one
/// address, and picking an unroutable one leaves the laptop visible in the list
/// but unreachable on send — the worst possible failure in the field.
/// </summary>
public static class LanAddressSelector
{
    static readonly string[] VirtualMarkers =
    [
        "hyper-v", "vethernet", "virtualbox", "vmware", "virtual ethernet",
        "host-only", "tap-windows", "docker", "wsl", "loopback",
    ];

    public static IReadOnlyList<IPAddress> Select(IEnumerable<NicCandidate> candidates)
    {
        var usable = candidates
            .Where(c => c.Address.AddressFamily == AddressFamily.InterNetwork)
            .Where(c => !IPAddress.IsLoopback(c.Address) && !IsLinkLocal(c.Address))
            .ToList();

        // Names are only a hint, so never let the hint empty the set: advertising a
        // questionable address still beats advertising none.
        var physical = usable.Where(c => !LooksVirtual(c.Description)).ToList();
        var pool = physical.Count > 0 ? physical : usable;

        // A default gateway is the strongest evidence a NIC sits on the real LAN.
        // A field switch with no router hands out none, so fall back to the pool.
        var routable = pool.Where(c => c.HasGateway).ToList();
        var chosen = routable.Count > 0 ? routable : pool;

        return chosen.Select(c => c.Address).ToList();
    }

    /// <summary>Empty when no address qualifies — callers then leave the choice to the mDNS library.</summary>
    public static IReadOnlyList<IPAddress> FromSystem() => Select(EnumerateSystem());

    static IEnumerable<NicCandidate> EnumerateSystem()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            var props = nic.GetIPProperties();
            var hasGateway = props.GatewayAddresses.Any(g =>
                g.Address is not null
                && g.Address.AddressFamily == AddressFamily.InterNetwork
                && !g.Address.Equals(IPAddress.Any));

            foreach (var unicast in props.UnicastAddresses)
                if (unicast.Address.AddressFamily == AddressFamily.InterNetwork)
                    yield return new NicCandidate(unicast.Address, hasGateway, nic.Description);
        }
    }

    static bool LooksVirtual(string description) =>
        VirtualMarkers.Any(marker =>
            description.Contains(marker, StringComparison.OrdinalIgnoreCase));

    static bool IsLinkLocal(IPAddress address)
    {
        var octets = address.GetAddressBytes();
        return octets[0] == 169 && octets[1] == 254;
    }
}
