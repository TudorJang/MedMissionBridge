using System.Net;

namespace MedMissionBridge.Ui;

public static class LoopbackOnly
{
    public static bool IsAllowed(HttpContext ctx)
    {
        var remote = ctx.Connection.RemoteIpAddress;
        if (remote is null) return true; // in-process (tests) or unix socket
        if (IPAddress.IsLoopback(remote)) return true;
        return remote.Equals(ctx.Connection.LocalIpAddress); // the laptop calling its own LAN IP
    }
}
