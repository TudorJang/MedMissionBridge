using MedMissionBridge.Data;

namespace MedMissionBridge.Ingest;

public static class IngestEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapPost("/api/v1/surveys", async (HttpRequest request, SurveyStore store, BridgeOptions options) =>
        {
            if (!ApiKeyGate.Allows(request, options.ApiKey)) return Results.Unauthorized();
            using var reader = new StreamReader(request.Body);
            var json = await reader.ReadToEndAsync();
            if (!PayloadExtractor.TryExtract(json, out var extracted)) return Results.BadRequest();
            await store.UpsertAsync(extracted!, json);
            return Results.Ok();
        });

        app.MapGet("/api/v1/surveys/{recordId}", async (string recordId, HttpRequest request, SurveyStore store, BridgeOptions options) =>
        {
            if (!ApiKeyGate.Allows(request, options.ApiKey)) return Results.Unauthorized();
            var record = await store.GetAsync(recordId);
            return record is null ? Results.NotFound()
                : Results.Content(record.RawJson, "application/json");
        });

        app.MapGet("/api/v1/surveys", async (string? accession, HttpRequest request, SurveyStore store, BridgeOptions options) =>
        {
            if (!ApiKeyGate.Allows(request, options.ApiKey)) return Results.Unauthorized();
            if (string.IsNullOrWhiteSpace(accession)) return Results.BadRequest();
            var record = await store.GetByAccessionAsync(accession);
            return record is null ? Results.NotFound()
                : Results.Content(record.RawJson, "application/json");
        });
    }
}
