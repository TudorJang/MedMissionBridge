using System.Net;
using MedMissionBridge;

namespace MedMissionBridge.Tests;

public class BridgeOptionsTests
{
    [Fact]
    public void defaults_match_the_spec()
    {
        var o = new BridgeOptions();
        Assert.Equal(18080, o.HttpPort);
        Assert.Equal(11112, o.Mwl.Port);
        Assert.Equal("MEDMISSION", o.Mwl.AeTitle);
        Assert.Equal("CR", o.Mwl.Modality);
    }

    [Fact]
    public void empty_db_path_resolves_under_program_data()
    {
        var o = new BridgeOptions();
        Assert.EndsWith(Path.Combine("MedMissionBridge", "bridge.db"), o.ResolveDbPath());
    }

    [Fact]
    public void explicit_db_path_wins()
    {
        var o = new BridgeOptions { DbPath = @"C:\tmp\x.db" };
        Assert.Equal(@"C:\tmp\x.db", o.ResolveDbPath());
    }

    [Fact]
    public void pinned_advertise_address_wins_over_detection()
    {
        var o = new BridgeOptions { Mdns = { AdvertiseAddress = "192.168.8.54" } };
        Assert.Equal(new[] { IPAddress.Parse("192.168.8.54") }, o.Mdns.ResolveAdvertiseAddresses());
    }

    [Fact]
    public void unparseable_advertise_address_falls_back_to_detection()
    {
        // A typo in the field must not silence discovery outright.
        var o = new BridgeOptions { Mdns = { AdvertiseAddress = "not-an-ip" } };
        Assert.Equal(new BridgeOptions().Mdns.ResolveAdvertiseAddresses(), o.Mdns.ResolveAdvertiseAddresses());
    }
}
