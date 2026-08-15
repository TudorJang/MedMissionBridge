using System.Globalization;
using FellowOakDicom;
using MedMissionBridge.Data;

namespace MedMissionBridge.Dicom;

public static class DicomConversions
{
    public static string? ToDicomDate(string? iso) =>
        DateOnly.TryParseExact(iso, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var d)
            ? d.ToString("yyyyMMdd", CultureInfo.InvariantCulture) : null;

    public static string? ToPersonName(string? last, string? first)
    {
        var l = (last ?? "").Trim();
        var f = (first ?? "").Trim();
        if (l.Length == 0 && f.Length == 0) return null;
        return $"{l}^{f}".TrimEnd('^');
    }

    public static string? ToSex(string? gender) => gender switch
    {
        "MALE" => "M", "FEMALE" => "F", _ => null,
    };

    public static DicomDataset BuildWorklistItem(SurveyRecord r, MwlOptions m)
    {
        var ds = new DicomDataset { { DicomTag.SpecificCharacterSet, "ISO_IR 192" } };
        AddIfPresent(ds, DicomTag.PatientName, ToPersonName(r.LastName, r.FirstName));
        ds.Add(DicomTag.PatientID, r.RecordId);
        AddIfPresent(ds, DicomTag.PatientBirthDate, ToDicomDate(r.BirthDate));
        AddIfPresent(ds, DicomTag.PatientSex, ToSex(r.Gender));
        AddIfPresent(ds, DicomTag.AccessionNumber, r.No);

        var sps = new DicomDataset
        {
            { DicomTag.ScheduledProcedureStepStartDate,
              ToDicomDate(r.Date) ?? DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture) },
            { DicomTag.ScheduledProcedureStepStartTime,
              r.ReceivedAtUtc.ToLocalTime().ToString("HHmmss", CultureInfo.InvariantCulture) },
            { DicomTag.Modality, m.Modality },
            { DicomTag.ScheduledStationAETitle, m.StationAeTitle },
            { DicomTag.ScheduledProcedureStepDescription, m.ProcedureDescription },
        };
        ds.Add(new DicomSequence(DicomTag.ScheduledProcedureStepSequence, sps));
        return ds;
    }

    private static void AddIfPresent(DicomDataset ds, DicomTag tag, string? value)
    {
        if (value is { Length: > 0 }) ds.Add(tag, value);
    }
}
