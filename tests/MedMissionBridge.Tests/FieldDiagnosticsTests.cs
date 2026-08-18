using MedMissionBridge.Deployment;

namespace MedMissionBridge.Tests;

public class FieldDiagnosticsTests
{
    private static BridgeOptions Options(int mwlPort = 11112) =>
        new() { Mwl = new MwlOptions { Port = mwlPort } };

    private static BridgeRuntimeState Healthy() => new()
    {
        MwlRunning = true,
        MdnsRunning = true,
        MdnsAddresses = ["192.168.8.54"],
    };

    private static IReadOnlyList<PortRange> Reserving11112 => [new PortRange(11105, 11204)];

    [Fact]
    public void a_healthy_laptop_reports_no_warnings()
    {
        var found = FieldDiagnostics.Build(Options(), Healthy(), []);

        Assert.DoesNotContain(found, d => d.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public void a_failed_mwl_bind_on_a_reserved_port_names_the_range_and_a_free_port()
    {
        var state = Healthy();
        state.MwlRunning = false;

        var warning = Assert.Single(FieldDiagnostics.Build(Options(), state, Reserving11112),
            d => d.Severity == DiagnosticSeverity.Warning);

        // The operator has to act on this without reading the log, so the message
        // has to carry the reserved range and the port to switch to.
        Assert.Contains("11105", warning.Message);
        Assert.Contains("11204", warning.Message);
        Assert.Contains("12112", warning.Message);
    }

    [Fact]
    public void a_failed_mwl_bind_on_a_free_port_blames_another_program_instead()
    {
        var state = Healthy();
        state.MwlRunning = false;

        var warning = Assert.Single(FieldDiagnostics.Build(Options(), state, []),
            d => d.Severity == DiagnosticSeverity.Warning);

        Assert.Contains("11112", warning.Message);
        // Telling the operator to change the port would send them to fix something
        // that was never the problem: nothing here has reserved this port.
        Assert.DoesNotContain("Bridge:Mwl:Port", warning.Message);
    }

    [Fact]
    public void a_running_mwl_on_a_reserved_port_is_not_a_warning()
    {
        // Reserved ranges are per-machine; if the bind succeeded, the range that
        // looks like it covers this port is not blocking anything here.
        var found = FieldDiagnostics.Build(Options(), Healthy(), Reserving11112);

        Assert.DoesNotContain(found, d => d.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public void mdns_down_tells_the_operator_tablets_need_the_address_by_hand()
    {
        var state = Healthy();
        state.MdnsRunning = false;

        var warning = Assert.Single(FieldDiagnostics.Build(Options(), state, []),
            d => d.Severity == DiagnosticSeverity.Warning);

        Assert.Contains("by hand", warning.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void advertising_nothing_warns_that_the_choice_fell_back_to_every_address()
    {
        var state = Healthy();
        state.MdnsAddresses = [];

        var warning = Assert.Single(FieldDiagnostics.Build(Options(), state, []),
            d => d.Severity == DiagnosticSeverity.Warning);

        Assert.Contains("AdvertiseAddress", warning.Message);
    }

    [Fact]
    public void advertising_several_addresses_warns_that_tablets_pick_one()
    {
        var state = Healthy();
        state.MdnsAddresses = ["192.168.8.54", "172.18.112.1"];

        var warning = Assert.Single(FieldDiagnostics.Build(Options(), state, []),
            d => d.Severity == DiagnosticSeverity.Warning);

        Assert.Contains("172.18.112.1", warning.Message);
        Assert.Contains("AdvertiseAddress", warning.Message);
    }

    [Fact]
    public void the_single_advertised_address_is_reported_for_the_operator_to_confirm()
    {
        // Detection cannot prove an address is the field LAN one — only a human
        // looking at it can — so this stays informational and always present.
        var info = Assert.Single(FieldDiagnostics.Build(Options(), Healthy(), []),
            d => d.Severity == DiagnosticSeverity.Info);

        Assert.Contains("192.168.8.54", info.Message);
    }
}
