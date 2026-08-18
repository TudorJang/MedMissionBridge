using System.Numerics;
using FellowOakDicom;
using MedMissionBridge.Data;

namespace MedMissionBridge.Dicom;

/// <summary>
/// Turns a Modality Performed Procedure Step message into "this survey is now in this
/// state". The console announces the start and end of an acquisition by itself, so
/// wiring MPPS is what stops the worklist from growing all day with studies nobody
/// remembered to close.
/// </summary>
public static class MppsMapping
{
    private const string UuidArc = "2.25.";

    /// <summary>PS3.3 C.4.14: IN PROGRESS, COMPLETED, DISCONTINUED.</summary>
    public static WorklistStatus? ToWorklistStatus(string? performedStatus) =>
        performedStatus?.Trim().ToUpperInvariant() switch
        {
            "IN PROGRESS" => WorklistStatus.InProgress,
            "COMPLETED" => WorklistStatus.Completed,
            "DISCONTINUED" => WorklistStatus.Cancelled,
            _ => null,
        };

    /// <summary>
    /// Which survey an N-CREATE is about. Patient ID is the survey's own record id and
    /// is the direct answer; the study UID is the fallback because the bridge derives it
    /// from that same record id, so it can be turned back into one.
    /// </summary>
    public static string? FindRecordId(DicomDataset dataset)
    {
        var patientId = dataset.GetSingleValueOrDefault(DicomTag.PatientID, string.Empty);
        if (patientId.Length > 0) return patientId;

        foreach (var uid in StudyUids(dataset))
            if (TryRecordIdFromStudyUid(uid, out var recordId))
                return recordId;

        return null;
    }

    /// <summary>Inverse of <see cref="DicomConversions.ToStudyInstanceUid"/>. Only a UID the
    /// bridge itself derived from a UUID record id can be reversed; anything else is
    /// somebody's own UID and is left alone.</summary>
    public static bool TryRecordIdFromStudyUid(string? studyUid, out string recordId)
    {
        recordId = string.Empty;
        if (studyUid is null || !studyUid.StartsWith(UuidArc, StringComparison.Ordinal)) return false;
        if (!BigInteger.TryParse(studyUid[UuidArc.Length..], out var value) || value.Sign < 0) return false;

        var bytes = value.ToByteArray(isUnsigned: true, isBigEndian: false);
        if (bytes.Length > 16) return false;
        var guidBytes = new byte[16];
        bytes.CopyTo(guidBytes, 0);
        recordId = new Guid(guidBytes).ToString();
        return true;
    }

    private static IEnumerable<string> StudyUids(DicomDataset dataset)
    {
        var top = dataset.GetSingleValueOrDefault(DicomTag.StudyInstanceUID, string.Empty);
        if (top.Length > 0) yield return top;

        if (!dataset.TryGetSequence(DicomTag.ScheduledStepAttributesSequence, out var steps))
            yield break;

        foreach (var step in steps.Items)
        {
            var uid = step.GetSingleValueOrDefault(DicomTag.StudyInstanceUID, string.Empty);
            if (uid.Length > 0) yield return uid;
        }
    }
}
