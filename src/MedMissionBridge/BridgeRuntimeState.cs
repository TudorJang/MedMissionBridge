using MedMissionBridge.Deployment;

namespace MedMissionBridge;

/// <summary>
/// Mutable, singleton snapshot of which optional background servers actually
/// came up. A bound MWL port or a failed mDNS advertiser must not take down
/// ingest, so startup catches those failures and records the outcome here
/// instead of throwing; /api/ui/health reports it for the operator.
/// </summary>
public sealed class BridgeRuntimeState
{
    public bool MwlRunning { get; set; }
    public bool MdnsRunning { get; set; }

    /// <summary>Addresses tablets are told to connect to. Surfaced in health so an
    /// operator can spot a virtual-adapter address before the field does.</summary>
    public IReadOnlyList<string> MdnsAddresses { get; set; } = [];

    /// <summary>Whether the running key came from appsettings.json or was generated
    /// on first start. Health reports it so the operator knows which key the tablets
    /// need and where it came from.</summary>
    public ApiKeySource ApiKeySource { get; set; } = ApiKeySource.Configured;

    /// <summary>TCP ranges Windows reserved on this laptop, read once at startup.
    /// Empty when they could not be read — the diagnostics then stay generic.</summary>
    public IReadOnlyList<PortRange> ExcludedTcpPorts { get; set; } = [];
}
