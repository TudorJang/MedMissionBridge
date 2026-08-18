namespace MedMissionBridge.Deployment;

public enum DiagnosticSeverity { Info, Warning }

public sealed record Diagnostic(DiagnosticSeverity Severity, string Message);

/// <summary>
/// The two checks the README asks an operator to run by hand on every field laptop,
/// done by the bridge itself and surfaced on the management page. Nobody reads a
/// deployment checklist at a screening site; a line on the page they already have
/// open is the only place a warning gets seen.
/// </summary>
public static class FieldDiagnostics
{
    public static IReadOnlyList<Diagnostic> Build(
        BridgeOptions options, BridgeRuntimeState state, IReadOnlyList<PortRange> excludedPorts)
    {
        var found = new List<Diagnostic>();
        var port = options.Mwl.Port;

        if (!state.MwlRunning)
        {
            // Only blame a Windows reservation when one actually covers this port —
            // the alternative cause, another program holding it, needs a different fix.
            PortRange? blocking = excludedPorts.Any(r => r.Contains(port))
                ? excludedPorts.First(r => r.Contains(port))
                : null;
            if (blocking is { } range)
            {
                var suggestion = PortExclusions.SuggestFreePort(excludedPorts, port);
                found.Add(new Diagnostic(DiagnosticSeverity.Warning,
                    $"The modality worklist is not running: Windows has reserved TCP "
                    + $"{range.Start}-{range.End} on this laptop, which covers the MWL port "
                    + $"{port}. Set Bridge:Mwl:Port to {suggestion?.ToString() ?? "a free port"}, "
                    + "restart, and tell the X-ray software the new port."));
            }
            else
            {
                found.Add(new Diagnostic(DiagnosticSeverity.Warning,
                    $"The modality worklist is not running: port {port} could not be bound, and "
                    + "no Windows port reservation covers it. Another program is most likely "
                    + "holding it — the log has the exact error. Survey ingest is unaffected."));
            }
        }

        if (!state.MdnsRunning)
        {
            found.Add(new Diagnostic(DiagnosticSeverity.Warning,
                "Discovery is not running, so tablets cannot find this laptop. Enter this "
                + $"laptop's LAN address and port {options.HttpPort} on each tablet by hand."));
        }
        else if (state.MdnsAddresses.Count == 0)
        {
            found.Add(new Diagnostic(DiagnosticSeverity.Warning,
                "No network adapter looked like the field LAN, so every local address is being "
                + "advertised. A tablet may pick a virtual adapter and fail to send. Pin "
                + "Bridge:Mdns:AdvertiseAddress to this laptop's LAN address."));
        }
        else if (state.MdnsAddresses.Count > 1)
        {
            found.Add(new Diagnostic(DiagnosticSeverity.Warning,
                $"Tablets are told about {state.MdnsAddresses.Count} addresses "
                + $"({string.Join(", ", state.MdnsAddresses)}) and will use whichever one they "
                + "resolve. Pin Bridge:Mdns:AdvertiseAddress to this laptop's LAN address."));
        }
        else
        {
            // Nothing on this laptop can prove the chosen address is reachable from the
            // tablets' network — only the person standing next to both can.
            found.Add(new Diagnostic(DiagnosticSeverity.Info,
                $"Tablets are told to connect to {state.MdnsAddresses[0]}:{options.HttpPort}. "
                + "Confirm that is this laptop's address on the field network."));
        }

        // The disk fills with images the console writes, not with anything of ours,
        // but this page is the one an operator opens each morning.
        if (DiskSpace.ForPath(options.ResolveDbPath()) is { } disk) found.Add(disk);

        return found;
    }
}
