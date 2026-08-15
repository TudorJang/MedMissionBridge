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
app.Use(async (ctx, next) =>
{
    var lanFacing = ctx.Request.Path.StartsWithSegments("/api/v1");
    if (!lanFacing && !MedMissionBridge.Ui.LoopbackOnly.IsAllowed(ctx))
    {
        ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }
    await next();
});
// [ANCHOR:STATIC] static web UI goes here
// [ANCHOR:ENDPOINTS] API endpoints go here
MedMissionBridge.Ingest.IngestEndpoints.Map(app);
MedMissionBridge.Ui.UiEndpoints.Map(app);
// [ANCHOR:SERVERS] MWL server and mDNS advertiser go here (guarded by !isTesting)
if (!isTesting)
{
    MedMissionBridge.Dicom.DicomSetup.EnsureInitialized();
    var store = app.Services.GetRequiredService<MedMissionBridge.Data.SurveyStore>();
    MedMissionBridge.Dicom.MwlService.WorklistSource = async () =>
    {
        var scheduled = await store.GetScheduledAsync();
        return scheduled
            .Select(r => MedMissionBridge.Dicom.DicomConversions.BuildWorklistItem(r, bridge.Mwl))
            .ToList();
    };
    var mwlServer = new MedMissionBridge.Dicom.MwlServer(bridge.Mwl.Port);
    app.Lifetime.ApplicationStopping.Register(mwlServer.Dispose);
    Log.Information("MWL SCP listening on port {Port}, AE {Ae}", bridge.Mwl.Port, bridge.Mwl.AeTitle);
    var advertiser = new MedMissionBridge.Mdns.MdnsAdvertiser(
        bridge.Mdns.ResolveServiceName(), bridge.HttpPort);
    app.Lifetime.ApplicationStopping.Register(advertiser.Dispose);
    Log.Information("mDNS advertising {Name} on _medmission._tcp:{Port}",
        bridge.Mdns.ResolveServiceName(), bridge.HttpPort);
}

app.Run();

public partial class Program;
