using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
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

    /// <summary>DICOM AS, e.g. "042Y". The console shows an Age column and leaves it
    /// blank when the worklist omits it, so it is derived rather than left out.</summary>
    public static string? ToDicomAge(string? isoBirthDate, DateOnly on)
    {
        if (!DateOnly.TryParseExact(isoBirthDate, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var born)) return null;
        if (born > on) return null;
        var years = on.Year - born.Year;
        if (on < born.AddYears(years)) years--;
        return years > 999 ? null : $"{years:D3}Y";
    }

    /// <summary>Philippine addresses run barangay, city, province, region — kept in that
    /// order and cut from the widest end, because the narrow parts locate the patient.</summary>
    public static string? ToPatientAddress(SurveyRecord r)
    {
        var parts = new[] { r.Address, r.Barangay, r.City, r.Province, r.Region, r.Zip }
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!.Trim())
            .ToList();
        while (parts.Count > 1 && string.Join(", ", parts).Length > 64) parts.RemoveAt(parts.Count - 1);
        var address = string.Join(", ", parts);
        return address.Length is > 0 and <= 64 ? address : null;
    }

    /// <summary>A Study Instance UID derived from the record id, so every C-FIND for the
    /// same survey returns the same UID without storing one. 2.25 is the OID arc reserved
    /// for UUID-derived identifiers, so no organisation root has to be registered.</summary>
    public static string ToStudyInstanceUid(string recordId)
    {
        var guid = Guid.TryParse(recordId, out var parsed)
            ? parsed
            : new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(recordId))[..16]);
        var value = new BigInteger(guid.ToByteArray(), isUnsigned: true, isBigEndian: false);
        return $"2.25.{value}";
    }

    public static DicomDataset BuildWorklistItem(SurveyRecord r, MwlOptions m)
    {
        // The console has no way to fetch the survey itself, so the findings that
        // change a chest read ride along in the description fields it already shows.
        var description = SurveySummary.Describe(r.RawJson, m.ProcedureDescription);
        var today = DateOnly.FromDateTime(DateTime.Now);
        var procedureId = Truncate(r.No, 16);

        var ds = new DicomDataset { { DicomTag.SpecificCharacterSet, "ISO_IR 192" } };
        AddIfPresent(ds, DicomTag.PatientName, ToPersonName(r.LastName, r.FirstName));
        ds.Add(DicomTag.PatientID, r.RecordId);
        AddIfPresent(ds, DicomTag.PatientBirthDate, ToDicomDate(r.BirthDate));
        AddIfPresent(ds, DicomTag.PatientSex, ToSex(r.Gender));
        AddIfPresent(ds, DicomTag.PatientAge, ToDicomAge(r.BirthDate, today));
        AddIfPresent(ds, DicomTag.PatientAddress, ToPatientAddress(r));
        AddIfPresent(ds, DicomTag.AccessionNumber, r.No);
        AddIfPresent(ds, DicomTag.ReferringPhysicianName, m.ReferringPhysician);
        ds.Add(DicomTag.StudyInstanceUID, ToStudyInstanceUid(r.RecordId));
        ds.Add(DicomTag.StudyDescription, description);
        ds.Add(DicomTag.RequestedProcedureDescription, description);
        AddIfPresent(ds, DicomTag.RequestedProcedureID, procedureId);

        var sps = new DicomDataset
        {
            { DicomTag.ScheduledProcedureStepStartDate,
              ToDicomDate(r.Date) ?? today.ToString("yyyyMMdd", CultureInfo.InvariantCulture) },
            { DicomTag.ScheduledProcedureStepStartTime,
              r.ReceivedAtUtc.ToLocalTime().ToString("HHmmss", CultureInfo.InvariantCulture) },
            { DicomTag.Modality, m.Modality },
            { DicomTag.ScheduledStationAETitle, m.StationAeTitle },
            { DicomTag.ScheduledProcedureStepDescription, description },
            // The console filters its list on status; an item without one can be
            // filtered out of the default "scheduled" view and never appear.
            { DicomTag.ScheduledProcedureStepStatus, "SCHEDULED" },
        };
        AddIfPresent(sps, DicomTag.ScheduledProcedureStepID, procedureId);
        ds.Add(new DicomSequence(DicomTag.ScheduledProcedureStepSequence, sps));
        return ds;
    }

    private static void AddIfPresent(DicomDataset ds, DicomTag tag, string? value)
    {
        if (value is { Length: > 0 }) ds.Add(tag, value);
    }

    private static string? Truncate(string? value, int max) =>
        value is null || value.Length <= max ? value : value[..max];
}
