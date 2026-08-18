using System.Globalization;
using System.Text;
using System.Text.Json;
using FellowOakDicom;
using MedMissionBridge.Data;

namespace MedMissionBridge.Dicom;

/// <summary>
/// Puts the whole survey into the worklist item. The console cannot call the survey API,
/// and whatever the worklist carries is what the console copies into the acquired image
/// and sends on to PACS — so this is the channel that decides whether a reader, months
/// later, can see what the patient actually reported.
///
/// Three layers, most standard first: real DICOM attributes where one exists, a readable
/// rendering in Additional Patient History for anything that displays free text, and the
/// exact payload in a private element for anything that wants to parse it. Nothing is
/// dropped at any layer, and the private element is the one that is lossless.
/// </summary>
public static class SurveyDetail
{
    /// <summary>Identifies our private block, so the tags below cannot collide with a
    /// vendor that happens to use the same group.</summary>
    public const string PrivateCreator = "MEDMISSION SURVEY";

    private const ushort PrivateGroup = 0x7777;

    /// <summary>The payload exactly as the tablet sent it. UT, so length is never a limit.</summary>
    public static DicomTag SurveyJson { get; } = new(PrivateGroup, 0x01, PrivateCreator);

    /// <summary>Names the payload format, so a reader knows what it is holding.</summary>
    public static DicomTag SurveySchema { get; } = new(PrivateGroup, 0x02, PrivateCreator);

    public const string SchemaName = "medmission-survey/1";

    /// <summary>Additional Patient History is LT — 10240 characters, far more than a survey needs.</summary>
    private const int HistoryLimit = 10240;

    public static void Apply(DicomDataset ds, SurveyRecord record)
    {
        JsonDocument? doc = null;
        try { doc = JsonDocument.Parse(record.RawJson); }
        catch (JsonException) { /* keep whatever the standard fields already carry */ }

        using (doc)
        {
            var root = doc?.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement : default;

            AddIfPresent(ds, DicomTag.PatientTelephoneNumbers, Str(Child(root, "patient"), "cellPhone"), 64);
            AddIfPresent(ds, DicomTag.PatientSize, HeightInMetres(root));
            AddIfPresent(ds, DicomTag.PatientWeight, Number(Child(root, "vitalSigns"), "weight"));
            AddIfPresent(ds, DicomTag.SmokingStatus, SmokingStatus(root));

            var history = ToText(record.RawJson);
            if (history.Length > 0)
                ds.AddOrUpdate(DicomTag.AdditionalPatientHistory, Clip(history, HistoryLimit));
        }

        // Always last and always whole: a reader that understands nothing else can still
        // recover every answer from here.
        ds.Add(DicomVR.LO, SurveySchema, SchemaName);
        ds.Add(DicomVR.UT, SurveyJson, record.RawJson);
    }

    /// <summary>The survey as lines a person can read. Sections with no answers are left
    /// out entirely rather than printed empty, so what is on screen is what was asked.</summary>
    public static string ToText(string rawJson)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(rawJson); }
        catch (JsonException) { return ""; }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return "";
            var root = doc.RootElement;
            var lines = new List<string>();

            Line(lines, "Patient", Join(
                Str(root, "no"),
                Labelled("age", Number(Child(root, "patient"), "age")),
                Str(Child(root, "patient"), "gender"),
                Str(Child(root, "patient"), "maritalStatus")));

            Line(lines, "Vitals", Vitals(Child(root, "vitalSigns")));

            var history = Child(root, "medicalHistory");
            Line(lines, "History", Join(
                Array(history, "items"),
                Labelled("other", Str(history, "others")),
                Labelled("surgery", Str(history, "recentSurgeriesOrHospitalization")),
                Labelled("meds", Str(history, "currentMedication"))));

            Line(lines, "Symptoms", Array(root, "symptoms") ?? "");

            var tb = Child(root, "tbInfo");
            Line(lines, "TB history", Join(
                Labelled("diagnosed", Str(tb, "everDiagnosedTB")),
                Labelled("year", Str(tb, "diagnosisYear")),
                Labelled("treated", Str(tb, "everReceivedTreatment")),
                Labelled("completed", Str(tb, "treatmentCompleted")),
                Labelled("contact", Str(tb, "closeContactActiveTB")),
                Labelled("when", Str(tb, "closeContactWhen")),
                Labelled("household", Str(tb, "householdMemberTBTreatment"))));

            Line(lines, "Smoking", Join(
                Str(Child(root, "smoking"), "status"),
                Str(Child(root, "smoking"), "duration")));

            Line(lines, "Alcohol", Join(
                Bool(Child(root, "alcohol"), "drinks"),
                Str(Child(root, "alcohol"), "amount")));

            var exposure = Child(root, "environmentalExposure");
            Line(lines, "Exposure", Join(
                Flag(exposure, "dustSmokeChemicalExposure", "dust/smoke/chemical"),
                Flag(exposure, "cooksWithSolidFuels", "solid fuels"),
                Flag(exposure, "secondhandSmokeExposure", "secondhand smoke"),
                Flag(exposure, "crowdedLivingConditions", "crowded living")));

            var patient = Child(root, "patient");
            Line(lines, "Contact", Join(
                Str(patient, "cellPhone"),
                Str(patient, "email"),
                Str(patient, "address"),
                Str(patient, "barangay"),
                Str(patient, "city"),
                Str(patient, "province"),
                Str(patient, "region"),
                Str(patient, "zip")));

            return string.Join("\n", lines);
        }
    }

    private static string Vitals(JsonElement v)
    {
        var parts = new List<string>();
        Add(parts, "Ht", Number(v, "height"), "cm");
        Add(parts, "Wt", Number(v, "weight"), "kg");
        var systolic = Number(v, "bpSystolic");
        var diastolic = Number(v, "bpDiastolic");
        if (systolic is not null || diastolic is not null)
            parts.Add($"BP {systolic ?? "?"}/{diastolic ?? "?"} mmHg");
        Add(parts, "HR", Number(v, "pulseRate"), "bpm");
        Add(parts, "RR", Number(v, "respiratoryRate"), "/min");
        Add(parts, "Temp", Number(v, "temperature"), "C");
        Add(parts, "SpO2", Number(v, "oxygenSaturation"), "%");
        Add(parts, "Glucose", Number(v, "bloodGlucose"), "mg/dL");
        return string.Join(", ", parts);
    }

    private static void Add(List<string> parts, string label, string? value, string unit)
    {
        if (value is not null) parts.Add($"{label} {value}{unit}");
    }

    private static void Line(List<string> lines, string label, string value)
    {
        if (value.Length > 0) lines.Add($"{label}: {value}");
    }

    private static string Join(params string?[] parts) =>
        string.Join(", ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));

    private static string? Labelled(string label, string? value) =>
        value is null ? null : $"{label} {value}";

    private static string? Flag(JsonElement parent, string name, string label) =>
        parent.ValueKind == JsonValueKind.Object
        && parent.TryGetProperty(name, out var p)
        && p.ValueKind == JsonValueKind.True ? label : null;

    private static string? Bool(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var p)
            ? p.ValueKind switch { JsonValueKind.True => "yes", JsonValueKind.False => "no", _ => null }
            : null;

    private static string? Array(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object
            || !parent.TryGetProperty(name, out var a)
            || a.ValueKind != JsonValueKind.Array) return null;
        var values = a.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString()!)
            .ToList();
        return values.Count > 0 ? string.Join(", ", values) : null;
    }

    private static JsonElement Child(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var c) ? c : default;

    private static string? Str(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object
        && parent.TryGetProperty(name, out var p)
        && p.ValueKind == JsonValueKind.String
        && p.GetString() is { Length: > 0 } s ? s : null;

    /// <summary>Trims the trailing zeros JSON carries, so a height reads "168cm" rather
    /// than "168.0cm" on a line an operator scans quickly.</summary>
    private static string? Number(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object
            || !parent.TryGetProperty(name, out var p)
            || p.ValueKind != JsonValueKind.Number) return null;
        return p.TryGetDouble(out var value)
            ? value.ToString("0.###", CultureInfo.InvariantCulture)
            : p.GetRawText();
    }

    /// <summary>DICOM records patient size in metres; the tablet asks for centimetres.</summary>
    private static string? HeightInMetres(JsonElement root)
    {
        var cm = Number(Child(root, "vitalSigns"), "height");
        return cm is not null && double.TryParse(cm, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            && v > 0
            ? (v / 100).ToString("0.###", CultureInfo.InvariantCulture)
            : null;
    }

    /// <summary>
    /// Smoking Status is a yes/no/unknown attribute about smoking now, so a former
    /// smoker has no honest value here — the attribute is left out and the history line
    /// carries the detail instead.
    /// </summary>
    private static string? SmokingStatus(JsonElement root) =>
        Str(Child(root, "smoking"), "status") switch
        {
            "CURRENT" => "YES",
            "NEVER" => "NO",
            _ => null,
        };

    private static void AddIfPresent(DicomDataset ds, DicomTag tag, string? value, int? max = null)
    {
        if (value is not { Length: > 0 }) return;
        ds.AddOrUpdate(tag, max is { } m ? Clip(value, m) : value);
    }

    private static string Clip(string value, int max) => value.Length <= max ? value : value[..max];
}
