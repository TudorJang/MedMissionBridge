using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace MedMissionBridge.Tests;

public sealed class BridgeAppFactory : WebApplicationFactory<Program>
{
    public const string ApiKey = "test-key";
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"bridge-web-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Bridge:ApiKey", ApiKey);
        builder.UseSetting("Bridge:DbPath", _dbPath);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { File.Delete(_dbPath); } catch (IOException) { }
    }
}
