namespace MedMissionBridge.Ingest;

public static class ApiKeyGate
{
    public static bool Allows(HttpRequest request, string configuredKey) =>
        request.Headers.TryGetValue("X-Api-Key", out var got) && got == configuredKey;
}
