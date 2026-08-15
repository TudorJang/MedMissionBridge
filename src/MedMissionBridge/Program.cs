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
        .MinimumLevel.Override("Microsoft.EntityFrameworkCore", Serilog.Events.LogEventLevel.Warning)
        .WriteTo.Console()
        .WriteTo.File(Path.Combine(bridge.ResolveDataDir(), "logs", "bridge-.log"),
            rollingInterval: RollingInterval.Day, retainedFileCountLimit: 30)
        .CreateLogger();
    builder.Host.UseSerilog();
    builder.WebHost.UseUrls($"http://0.0.0.0:{bridge.HttpPort}");

    if (bridge.ApiKey == "changeme-dev-key" || string.IsNullOrWhiteSpace(bridge.ApiKey))
    {
        Log.Warning(
            "Bridge:ApiKey is left at its default/blank value — tablet ingest on the LAN " +
            "is effectively unauthenticated. Set a real value in appsettings.json before deploying.");
    }
}

// [ANCHOR:SERVICES] later tasks register services below this line
builder.Services.AddDbContextFactory<MedMissionBridge.Data.BridgeDbContext>(o =>
    o.UseSqlite($"Data Source={bridge.ResolveDbPath()}"));
builder.Services.AddSingleton<MedMissionBridge.Data.SurveyStore>();
var runtimeState = new MedMissionBridge.BridgeRuntimeState();
builder.Services.AddSingleton(runtimeState);

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
app.UseDefaultFiles();
app.UseStaticFiles();
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

    // A bound MWL port must not take ingest down with it: log and continue.
    try
    {
        var mwlServer = new MedMissionBridge.Dicom.MwlServer(bridge.Mwl.ListenAddress, bridge.Mwl.Port);
        app.Lifetime.ApplicationStopping.Register(mwlServer.Dispose);
        runtimeState.MwlRunning = true;
        Log.Information("MWL SCP listening on {Address}:{Port}, AE {Ae}",
            bridge.Mwl.ListenAddress, bridge.Mwl.Port, bridge.Mwl.AeTitle);
    }
    catch (Exception ex)
    {
        Log.Error(ex,
            "Failed to start the MWL SCP on {Address}:{Port} — the modality worklist will be " +
            "unavailable, but survey ingest continues normally", bridge.Mwl.ListenAddress, bridge.Mwl.Port);
    }

    // Same for mDNS: a discovery failure must not take ingest down with it.
    try
    {
        var advertiser = new MedMissionBridge.Mdns.MdnsAdvertiser(
            bridge.Mdns.ResolveServiceName(), bridge.HttpPort);
        app.Lifetime.ApplicationStopping.Register(advertiser.Dispose);
        runtimeState.MdnsRunning = true;
        Log.Information("mDNS advertising {Name} on _medmission._tcp:{Port}",
            bridge.Mdns.ResolveServiceName(), bridge.HttpPort);
    }
    catch (Exception ex)
    {
        Log.Error(ex,
            "Failed to start mDNS advertising for {Name} on port {Port} — tablets will need this " +
            "laptop's address configured manually, but survey ingest continues normally",
            bridge.Mdns.ResolveServiceName(), bridge.HttpPort);
    }
}

app.Run();

// CloseAndFlush on an unconfigured static logger (Testing env) is a no-op.
Log.CloseAndFlush();

public partial class Program;
