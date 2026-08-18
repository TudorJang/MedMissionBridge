using MedMissionBridge;
using MedMissionBridge.Data;
using MedMissionBridge.Deployment;
using MedMissionBridge.Dicom;
using MedMissionBridge.Ingest;
using MedMissionBridge.Mdns;
using MedMissionBridge.Ui;
using Microsoft.EntityFrameworkCore;
using Serilog;

var builder = WebApplication.CreateBuilder(args);
var bridge = builder.Configuration.GetSection("Bridge").Get<BridgeOptions>() ?? new BridgeOptions();

// A site that never edited appsettings.json would otherwise run on the published
// placeholder key, which every copy of the bridge shares. Resolve to a generated,
// site-unique key instead; the operator reads it off the management page.
var apiKey = ApiKeyBootstrap.Resolve(bridge.ApiKey, bridge.ResolveDataDir);
bridge.ApiKey = apiKey.Key;
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

    if (apiKey.Source == ApiKeySource.Generated)
    {
        Log.Information(
            "Bridge:ApiKey was left at its placeholder value, so this laptop is using its own " +
            "generated key, kept in {KeyFile}. Read it from the management page at " +
            "http://127.0.0.1:{Port}/ and enter it on each tablet.",
            Path.Combine(bridge.ResolveDataDir(), ApiKeyBootstrap.FileName), bridge.HttpPort);
    }
}

builder.Services.AddDbContextFactory<BridgeDbContext>(o =>
    o.UseSqlite($"Data Source={bridge.ResolveDbPath()}"));
builder.Services.AddSingleton<SurveyStore>();
var runtimeState = new BridgeRuntimeState { ApiKeySource = apiKey.Source };
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

    // The console reports its own progress over MPPS, which is what keeps the worklist
    // from filling up with studies nobody remembered to close.
    MwlService.StatusSink = async (recordId, status) =>
    {
        var result = await store.TryChangeStatusAsync(recordId, status);
        if (result != StatusChangeResult.Changed)
            Log.Warning("MPPS reported {Status} for {RecordId} but the record is {Result}",
                status, recordId, result);
    };

    // Read before starting the SCP so a bind failure can name the range in the way.
    runtimeState.ExcludedTcpPorts = PortExclusions.FromSystem();

    // A bound MWL port must not take ingest down with it: log and continue.
    try
    {
        var mwlServer = new MwlServer(bridge.Mwl.ListenAddress, bridge.Mwl.Port);
        app.Lifetime.ApplicationStopping.Register(mwlServer.Dispose);
        runtimeState.MwlRunning = true;
        Log.Information(
            "MWL and MPPS SCP listening on {Address}:{Port}, AE {Ae} — point the console's "
            + "worklist and MPPS destinations at the same host, port and AE title",
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
        var addresses = bridge.Mdns.ResolveAdvertiseAddresses();
        var advertiser = new MdnsAdvertiser(bridge.Mdns.ResolveServiceName(), bridge.HttpPort, addresses);
        app.Lifetime.ApplicationStopping.Register(advertiser.Dispose);
        runtimeState.MdnsRunning = true;
        runtimeState.MdnsAddresses = addresses.Select(a => a.ToString()).ToList();
        Log.Information("mDNS advertising {Name} on _medmission._tcp:{Port} at {Addresses}",
            bridge.Mdns.ResolveServiceName(), bridge.HttpPort,
            addresses.Count > 0 ? string.Join(", ", addresses) : "every local address");
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
