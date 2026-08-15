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
}
