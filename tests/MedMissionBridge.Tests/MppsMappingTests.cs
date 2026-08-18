using FellowOakDicom;
using MedMissionBridge.Data;
using MedMissionBridge.Dicom;

namespace MedMissionBridge.Tests;

public class MppsMappingTests
{
    [Theory]
    [InlineData("IN PROGRESS", WorklistStatus.InProgress)]
    [InlineData("COMPLETED", WorklistStatus.Completed)]
    [InlineData("DISCONTINUED", WorklistStatus.Cancelled)]
    [InlineData("in progress", WorklistStatus.InProgress)]
    [InlineData(" COMPLETED ", WorklistStatus.Completed)]
    public void performed_status_maps_to_a_worklist_status(string performed, WorklistStatus expected) =>
        Assert.Equal(expected, MppsMapping.ToWorklistStatus(performed));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("SOMETHING ELSE")]
    public void an_unknown_status_changes_nothing(string? performed) =>
        // Guessing at an unrecognised status could close a study that was never shot.
        Assert.Null(MppsMapping.ToWorklistStatus(performed));

    [Fact]
    public void patient_id_identifies_the_survey_directly()
    {
        var ds = new DicomDataset { { DicomTag.PatientID, "8f14e45f-ceea-467a-9f9c-6a2f0b4c1d33" } };

        Assert.Equal("8f14e45f-ceea-467a-9f9c-6a2f0b4c1d33", MppsMapping.FindRecordId(ds));
    }

    [Fact]
    public void the_study_uid_identifies_it_when_patient_id_is_absent()
    {
        // Some consoles put only the scheduled step attributes in the N-CREATE. The UID
        // is ours, derived from the record id, so it can be turned back into one.
        const string recordId = "8f14e45f-ceea-467a-9f9c-6a2f0b4c1d33";
        var step = new DicomDataset
        {
            { DicomTag.StudyInstanceUID, DicomConversions.ToStudyInstanceUid(recordId) },
        };
        var ds = new DicomDataset();
        ds.Add(new DicomSequence(DicomTag.ScheduledStepAttributesSequence, step));

        Assert.Equal(recordId, MppsMapping.FindRecordId(ds));
    }

    [Fact]
    public void a_uid_that_is_not_ours_is_left_alone()
    {
        // A console's own study UID must not be mistaken for a record id.
        Assert.False(MppsMapping.TryRecordIdFromStudyUid("1.2.840.113619.2.55.3.12345", out _));
        Assert.False(MppsMapping.TryRecordIdFromStudyUid("2.25.not-a-number", out _));
        Assert.False(MppsMapping.TryRecordIdFromStudyUid(null, out _));
    }

    [Fact]
    public void a_message_about_nothing_we_know_returns_no_record()
    {
        var ds = new DicomDataset { { DicomTag.StudyInstanceUID, "1.2.840.113619.2.55.3.99" } };

        Assert.Null(MppsMapping.FindRecordId(ds));
    }

    [Theory]
    [InlineData("8f14e45f-ceea-467a-9f9c-6a2f0b4c1d33")]
    [InlineData("00000000-0000-0000-0000-000000000001")]
    [InlineData("ffffffff-ffff-ffff-ffff-ffffffffffff")]
    public void every_record_id_survives_the_round_trip_through_a_study_uid(string recordId)
    {
        // Leading-zero and all-ones ids are where a big-integer conversion loses bytes.
        Assert.True(MppsMapping.TryRecordIdFromStudyUid(
            DicomConversions.ToStudyInstanceUid(recordId), out var back));
        Assert.Equal(recordId, back);
    }
}
