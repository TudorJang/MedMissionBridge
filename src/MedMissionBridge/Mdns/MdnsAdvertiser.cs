using Makaretu.Dns;

namespace MedMissionBridge.Mdns;

/// <summary>
/// Advertises `_medmission._tcp` so tablets list this laptop automatically.
/// The service type must match the tablet's NsdDiscoveryService exactly.
/// </summary>
public sealed class MdnsAdvertiser : IDisposable
{
    private readonly ServiceDiscovery _sd = new();

    public MdnsAdvertiser(string instanceName, int port) =>
        _sd.Advertise(new ServiceProfile(instanceName, "_medmission._tcp", (ushort)port));

    public void Dispose() => _sd.Dispose();
}
