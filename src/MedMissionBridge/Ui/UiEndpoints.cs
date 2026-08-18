using MedMissionBridge.Data;
using MedMissionBridge.Deployment;
using Microsoft.Extensions.Logging;

namespace MedMissionBridge.Ui;

public static class UiEndpoints
{
    public record StatusChange(string Status);

    public static void Map(WebApplication app)
    {
        app.MapGet("/api/ui/records", async (string? search, string? status, SurveyStore store) =>
        {
            WorklistStatus? filter = null;
            if (!string.IsNullOrWhiteSpace(status))
            {
                if (!Enum.TryParse<WorklistStatus>(status, ignoreCase: true, out var parsed))
                    return Results.BadRequest();
                filter = parsed;
            }
            var records = await store.ListAsync(search, filter);
            return Results.Ok(records.Select(r => new
            {
                recordId = r.RecordId, no = r.No, firstName = r.FirstName, lastName = r.LastName,
                city = r.City, status = r.Status.ToString(), date = r.Date,
                receivedAtUtc = r.ReceivedAtUtc,
            }));
        });

        app.MapGet("/api/ui/records/{recordId}", async (string recordId, SurveyStore store) =>
        {
            var r = await store.GetAsync(recordId);
            return r is null ? Results.NotFound()
                : Results.Ok(new { recordId = r.RecordId, status = r.Status.ToString(), rawJson = r.RawJson });
        });

        app.MapPost("/api/ui/records/{recordId}/status", async (string recordId, StatusChange body, SurveyStore store) =>
        {
            // Enum.TryParse also accepts the underlying numeric value (e.g. "2" or " 2"),
            // which is not a valid client input — only the named statuses are. Status can
            // bind to null when the JSON body omits it, so guard that first.
            var requested = body.Status?.Trim();
            if (string.IsNullOrEmpty(requested)
                || requested.All(char.IsDigit)
                || !Enum.TryParse<WorklistStatus>(requested, ignoreCase: true, out var to)
                || !Enum.IsDefined(to))
                return Results.BadRequest();
            return await store.TryChangeStatusAsync(recordId, to) switch
            {
                StatusChangeResult.Changed => Results.Ok(),
                StatusChangeResult.NotFound => Results.NotFound(),
                _ => Results.Conflict(),
            };
        });

        app.MapPost("/api/ui/backup", (BridgeOptions options, ILogger<Program> logger) =>
        {
            // Everything the site collected lives only on this laptop; the operator
            // presses this before carrying a copy off on a USB drive.
            try
            {
                var dir = options.ResolveBackupDir();
                var path = BackupService.Create(options.ResolveDbPath(), dir, DateTime.Now);
                BackupService.Prune(dir);
                logger.LogInformation("Database backup written to {BackupPath}", path);
                return Results.Ok(new { path });
            }
            catch (Exception ex)
            {
                // A failed backup that looks like a success is how a site ends up with
                // no copy at all, so say so on the page.
                logger.LogError(ex, "Database backup failed");
                return Results.Problem("Backup failed — see the log window for details.");
            }
        });

        app.MapGet("/api/ui/health", (BridgeOptions options, BridgeRuntimeState runtime) => Results.Ok(new
        {
            httpPort = options.HttpPort,
            mwlPort = options.Mwl.Port,
            mwlAeTitle = options.Mwl.AeTitle,
            // The AE title is advertised only — the SCP accepts any calling/called AE.
            mwlAeTitleEnforced = false,
            mwlRunning = runtime.MwlRunning,
            mdnsRunning = runtime.MdnsRunning,
            mdnsAddresses = runtime.MdnsAddresses,
            // Safe to expose: this whole surface is loopback-only, and the operator
            // needs to read the key to type it into the tablets.
            apiKey = options.ApiKey,
            apiKeySource = runtime.ApiKeySource.ToString(),
            dbPath = options.ResolveDbPath(),
            serviceName = options.Mdns.ResolveServiceName(),
            // The per-laptop deployment checks, run here instead of by an operator
            // working through the README at a screening site.
            diagnostics = FieldDiagnostics.Build(options, runtime, runtime.ExcludedTcpPorts)
                .Select(d => new { severity = d.Severity.ToString(), message = d.Message }),
        }));
    }
}
