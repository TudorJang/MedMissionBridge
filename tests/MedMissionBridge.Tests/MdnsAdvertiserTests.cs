using MedMissionBridge.Mdns;

namespace MedMissionBridge.Tests;

public class MdnsAdvertiserTests
{
    [Fact]
    public void constructs_and_disposes_without_throwing()
    {
        using var advertiser = new MdnsAdvertiser("TEST-LAPTOP", 18080);
    }
}
