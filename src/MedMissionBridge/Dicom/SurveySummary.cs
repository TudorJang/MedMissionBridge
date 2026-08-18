using System.Text.Json;

namespace MedMissionBridge.Dicom;

/// <summary>
/// Condenses a survey into one DICOM-sized line. The acquisition console reads the
/// worklist and nothing else — it has no way to call the survey API — so a description
/// field is the only channel that puts the patient's answers in front of the operator
/// before the exposure. Everything here is therefore chosen for a chest read at a
/// screening site, not for completeness; the full survey stays available over REST.
/// </summary>
public static class SurveySummary
{
    /// <summary>DICOM LO (Long String) is capped at 64 characters.</summary>
    public const int MaxLength = 64;

    private const string Prefix = "TB scr";

    /// <summary>Symptoms that change how a screening chest film is read, shortest label first.</summary>
    private static readonly (string Code, string Label)[] Symptoms =
    [
        ("COUGH_2WEEKS_PLUS", "cough2w"),
        ("BLOOD_IN_SPUTUM", "hemoptysis"),
        ("NIGHT_SWEATS", "nightsweats"),
        ("WEIGHT_LOSS", "wtloss"),
        ("FEVER", "fever"),
    ];

    private static readonly (string Field, string Label)[] History =
    [
        ("everDiagnosedTB", "prevTB"),
        ("closeContactActiveTB", "contact"),
    ];

    public static string Describe(string rawJson, string fallback)
    {
        var parts = Collect(rawJson);
        if (parts.Count == 0) return fallback;

        var line = $"{Prefix}: {string.Join(",", parts)}";
        // Dropping whole findings from the end beats truncating mid-word into
        // something the operator could misread as a different finding.
        while (line.Length > MaxLength && parts.Count > 1)
        {
            parts.RemoveAt(parts.Count - 1);
            line = $"{Prefix}: {string.Join(",", parts)}";
        }
        return line.Length <= MaxLength ? line : fallback;
    }

    private static List<string> Collect(string rawJson)
    {
        var found = new List<string>();
        JsonDocument doc;
        try { doc = JsonDocument.Parse(rawJson); }
        catch (JsonException) { return found; }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return found;

            if (doc.RootElement.TryGetProperty("symptoms", out var symptoms)
                && symptoms.ValueKind == JsonValueKind.Array)
            {
                var reported = symptoms.EnumerateArray()
                    .Where(s => s.ValueKind == JsonValueKind.String)
                    .Select(s => s.GetString())
                    .ToHashSet(StringComparer.Ordinal);

                found.AddRange(Symptoms.Where(s => reported.Contains(s.Code)).Select(s => s.Label));
            }

            if (doc.RootElement.TryGetProperty("tbInfo", out var tb)
                && tb.ValueKind == JsonValueKind.Object)
            {
                // DONT_KNOW is not a positive finding — reporting it as one would put a
                // history on the screen that the patient never confirmed.
                found.AddRange(History
                    .Where(h => tb.TryGetProperty(h.Field, out var v)
                                && v.ValueKind == JsonValueKind.String
                                && v.GetString() == "YES")
                    .Select(h => h.Label));
            }
        }
        return found;
    }
}
