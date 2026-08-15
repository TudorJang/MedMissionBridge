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
            if (!Enum.TryParse<WorklistStatus>(body.Status, ignoreCase: true, out var to))
                return Results.BadRequest();
            return await store.TryChangeStatusAsync(recordId, to) switch
            {
                StatusChangeResult.Changed => Results.Ok(),
                StatusChangeResult.NotFound => Results.NotFound(),
                _ => Results.Conflict(),
            };
        });

        app.MapGet("/api/ui/health", (BridgeOptions options) => Results.Ok(new
        {
            httpPort = options.HttpPort,
            mwlPort = options.Mwl.Port,
            mwlAeTitle = options.Mwl.AeTitle,
            dbPath = options.ResolveDbPath(),
            serviceName = options.Mdns.ResolveServiceName(),
        }));
    }
}
