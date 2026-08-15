using System.Net;
using MedMissionBridge.Ui;
using Microsoft.AspNetCore.Http;

namespace MedMissionBridge.Tests;

public class LoopbackOnlyTests
{
    private static HttpContext Ctx(string? remote)
    {
        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = remote is null ? null : IPAddress.Parse(remote);
        return ctx;
    }

    [Theory]
    [InlineData(null, true)]          // in-process test server
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("192.168.1.50", false)]
    public void loopback_rule(string? remote, bool allowed) =>
        Assert.Equal(allowed, LoopbackOnly.IsAllowed(Ctx(remote)));

    [Fact]
    public void same_machine_lan_address_is_allowed()
    {
        var ctx = Ctx("192.168.1.10");
        ctx.Connection.LocalIpAddress = IPAddress.Parse("192.168.1.10");
        Assert.True(LoopbackOnly.IsAllowed(ctx));
    }
}
