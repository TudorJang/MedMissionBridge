using MedMissionBridge.Data;

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

        app.MapGet("/api/ui/health", (BridgeOptions options, BridgeRuntimeState runtime) => Results.Ok(new
        {
            httpPort = options.HttpPort,
            mwlPort = options.Mwl.Port,
            mwlAeTitle = options.Mwl.AeTitle,
            // The AE title is advertised only — the SCP accepts any calling/called AE.
            mwlAeTitleEnforced = false,
            mwlRunning = runtime.MwlRunning,
            mdnsRunning = runtime.MdnsRunning,
            apiKeyIsDefault = string.IsNullOrWhiteSpace(options.ApiKey) || options.ApiKey == "changeme-dev-key",
            dbPath = options.ResolveDbPath(),
            serviceName = options.Mdns.ResolveServiceName(),
        }));
    }
}
