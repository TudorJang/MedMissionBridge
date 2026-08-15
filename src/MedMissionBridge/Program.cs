using MedMissionBridge;
using MedMissionBridge.Data;
using MedMissionBridge.Dicom;
using MedMissionBridge.Ingest;
using MedMissionBridge.Mdns;
using MedMissionBridge.Ui;
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

builder.Services.AddDbContextFactory<BridgeDbContext>(o =>
    o.UseSqlite($"Data Source={bridge.ResolveDbPath()}"));
builder.Services.AddSingleton<SurveyStore>();
var runtimeState = new BridgeRuntimeState();
builder.Services.AddSingleton(runtimeState);

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<BridgeDbContext>>();
    using var db = dbFactory.CreateDbContext();
    db.Database.Migrate();
}

// Loopback gate: everything except the LAN-facing /api/v1 surface is local-only.
// Runs before static files and endpoints so new surfaces are protected by default.
app.Use(async (ctx, next) =>
{
    var lanFacing = ctx.Request.Path.StartsWithSegments("/api/v1");
    if (!lanFacing && !LoopbackOnly.IsAllowed(ctx))
    {
        ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }
    await next();
});

app.UseDefaultFiles();
app.UseStaticFiles();

IngestEndpoints.Map(app);
UiEndpoints.Map(app);

if (!isTesting)
{
    DicomSetup.EnsureInitialized();
    var store = app.Services.GetRequiredService<SurveyStore>();
    MwlService.WorklistSource = async () =>
    {
        var scheduled = await store.GetScheduledAsync();
        return scheduled.Select(r => DicomConversions.BuildWorklistItem(r, bridge.Mwl)).ToList();
    };

    // A bound MWL port must not take ingest down with it: log and continue.
    try
    {
        var mwlServer = new MwlServer(bridge.Mwl.ListenAddress, bridge.Mwl.Port);
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
        var advertiser = new MdnsAdvertiser(bridge.Mdns.ResolveServiceName(), bridge.HttpPort);
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
