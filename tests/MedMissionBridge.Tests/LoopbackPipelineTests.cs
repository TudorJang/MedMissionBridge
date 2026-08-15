using System.Net;
using Microsoft.AspNetCore.Http;

namespace MedMissionBridge.Tests;

/// <summary>
/// Proves the loopback gate middleware is wired at the right pipeline
/// position: it must deny a LAN caller on a UI route, but exempt the
/// LAN-facing ingest route (which is still API-key gated downstream).
/// </summary>
public class LoopbackPipelineTests
{
    [Fact]
    public async Task lan_caller_is_denied_on_a_ui_route()
    {
        using var app = new BridgeAppFactory();
        var server = app.Server;

        var ctx = await server.SendAsync(c =>
        {
            c.Request.Path = "/api/ui/health";
            c.Connection.RemoteIpAddress = IPAddress.Parse("192.168.1.50");
        });

        Assert.Equal(StatusCodes.Status403Forbidden, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task lan_caller_is_exempted_on_the_ingest_route_but_still_needs_a_key()
    {
        using var app = new BridgeAppFactory();
        var server = app.Server;

        var ctx = await server.SendAsync(c =>
        {
            c.Request.Method = "POST";
            c.Request.Path = "/api/v1/surveys";
            c.Connection.RemoteIpAddress = IPAddress.Parse("192.168.1.50");
        });

        // Not 403 (loopback gate exempts /api/v1) — 401 because no X-Api-Key was sent.
        Assert.Equal(StatusCodes.Status401Unauthorized, ctx.Response.StatusCode);
    }
}
