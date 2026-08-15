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
}
