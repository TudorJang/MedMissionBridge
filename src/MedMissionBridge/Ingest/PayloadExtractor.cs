using System.Text.Json;

namespace MedMissionBridge.Ingest;

public record ExtractedSurvey(
    string RecordId, string? No, string? Date,
    string? FirstName, string? LastName, string? BirthDate, string? Gender,
    string? Region, string? Province, string? City, string? Barangay,
    string? Zip, string? Address);

public static class PayloadExtractor
{
    public static bool TryExtract(string json, out ExtractedSurvey? extracted)
    {
        extracted = null;
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { return false; }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            var recordId = Str(root, "recordId");
            if (string.IsNullOrEmpty(recordId)) return false;

            root.TryGetProperty("patient", out var patient);
            extracted = new ExtractedSurvey(
                RecordId: recordId,
                No: Str(root, "no"),
                Date: Str(root, "date"),
                FirstName: Str(patient, "firstName"),
                LastName: Str(patient, "lastName"),
                BirthDate: Str(patient, "birthDate"),
                Gender: Str(patient, "gender"),
                Region: Str(patient, "region"),
                Province: Str(patient, "province"),
                City: Str(patient, "city"),
                Barangay: Str(patient, "barangay"),
                Zip: Str(patient, "zip"),
                Address: Str(patient, "address"));
            return true;
        }
    }

    private static string? Str(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object
            && parent.TryGetProperty(name, out var p)
            && p.ValueKind == JsonValueKind.String
            && p.GetString() is { Length: > 0 } s
        ? s : null;
}
