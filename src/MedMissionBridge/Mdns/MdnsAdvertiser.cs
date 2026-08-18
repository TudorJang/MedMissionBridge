using System.Net;
using Makaretu.Dns;

namespace MedMissionBridge.Mdns;

/// <summary>
/// Advertises `_medmission._tcp` so tablets list this laptop automatically.
/// The service type must match the tablet's NsdDiscoveryService exactly.
/// </summary>
public sealed class MdnsAdvertiser : IDisposable
{
    private readonly ServiceDiscovery _sd = new();

    /// <param name="addresses">
    /// Addresses tablets should connect to. Empty or null hands the choice back to
    /// the library, which publishes every local address — including host-only
    /// adapters a tablet cannot route to. See <see cref="LanAddressSelector"/>.
    /// </param>
    public MdnsAdvertiser(string instanceName, int port, IEnumerable<IPAddress>? addresses = null)
    {
        var chosen = addresses?.ToList();
        _sd.Advertise(chosen is { Count: > 0 }
            ? new ServiceProfile(instanceName, "_medmission._tcp", (ushort)port, chosen)
            : new ServiceProfile(instanceName, "_medmission._tcp", (ushort)port));
    }

    public void Dispose() => _sd.Dispose();
}
