using MedMissionBridge;
using Microsoft.EntityFrameworkCore;
using Serilog;

var builder = WebApplication.CreateBuilder(args);
var bridge = builder.Configuration.GetSection("Bridge").Get<BridgeOptions>() ?? new BridgeOptions();
builder.Services.AddSingleton(bridge);

var isTesting = builder.Environment.IsEnvironment("Testing");
if (!isTesting)
{
    Log.Logger = new LoggerConfiguration()
        .WriteTo.Console()
        .WriteTo.File(Path.Combine(bridge.ResolveDataDir(), "logs", "bridge-.log"),
            rollingInterval: RollingInterval.Day, retainedFileCountLimit: 30)
        .CreateLogger();
    builder.Host.UseSerilog();
    builder.WebHost.UseUrls($"http://0.0.0.0:{bridge.HttpPort}");
}

// [ANCHOR:SERVICES] later tasks register services below this line
builder.Services.AddDbContextFactory<MedMissionBridge.Data.BridgeDbContext>(o =>
    o.UseSqlite($"Data Source={bridge.ResolveDbPath()}"));
builder.Services.AddSingleton<MedMissionBridge.Data.SurveyStore>();

var app = builder.Build();

// [ANCHOR:MIGRATE] database migration on startup goes here
using (var scope = app.Services.CreateScope())
{
    var dbFactory = scope.ServiceProvider
        .GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<MedMissionBridge.Data.BridgeDbContext>>();
    using var db = dbFactory.CreateDbContext();
    db.Database.Migrate();
}
// [ANCHOR:MIDDLEWARE] loopback gate goes here
// [ANCHOR:STATIC] static web UI goes here
// [ANCHOR:ENDPOINTS] API endpoints go here
// [ANCHOR:SERVERS] MWL server and mDNS advertiser go here (guarded by !isTesting)

app.Run();

public partial class Program;
