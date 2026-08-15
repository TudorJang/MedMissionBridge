using FellowOakDicom;
using MedMissionBridge.Data;
using MedMissionBridge.Dicom;

namespace MedMissionBridge.Tests;

public class DicomConversionsTests
{
    [Theory]
    [InlineData("1980-03-04", "19800304")]
    [InlineData("2026-08-14", "20260814")]
    [InlineData("1980-03", null)]     // partial entry: attribute omitted (wire contract §6)
    [InlineData("1980-13-99", null)]  // not a calendar date
    [InlineData("", null)]
    [InlineData(null, null)]
    public void iso_to_da(string? iso, string? expected) =>
        Assert.Equal(expected, DicomConversions.ToDicomDate(iso));

    [Theory]
    [InlineData("Dela Cruz", "Juan", "Dela Cruz^Juan")]
    [InlineData("Dela Cruz", null, "Dela Cruz")]
    [InlineData(null, "Juan", "^Juan")]
    [InlineData(null, null, null)]
    public void person_name(string? last, string? first, string? expected) =>
        Assert.Equal(expected, DicomConversions.ToPersonName(last, first));

    [Theory]
    [InlineData("MALE", "M")]
    [InlineData("FEMALE", "F")]
    [InlineData("other", null)]
    [InlineData(null, null)]
    public void sex(string? gender, string? expected) =>
        Assert.Equal(expected, DicomConversions.ToSex(gender));

    [Fact]
    public void worklist_item_carries_the_mapping_table()
    {
        var r = new SurveyRecord
        {
            RecordId = "3f8b1c2e-9a4d-4c11-8e77-2b6a0d5f9c31", RawJson = "{}",
            No = "TAB-3FBB-0001", Date = "2026-08-14", FirstName = "Juan",
            LastName = "Dela Cruz", BirthDate = "1980-03-04", Gender = "MALE",
            ReceivedAtUtc = new DateTime(2026, 8, 14, 3, 0, 0, DateTimeKind.Utc),
        };
        var ds = DicomConversions.BuildWorklistItem(r, new MwlOptions());

        Assert.Equal("Dela Cruz^Juan", ds.GetSingleValue<string>(DicomTag.PatientName));
        Assert.Equal(r.RecordId, ds.GetSingleValue<string>(DicomTag.PatientID));
        Assert.Equal("19800304", ds.GetSingleValue<string>(DicomTag.PatientBirthDate));
        Assert.Equal("M", ds.GetSingleValue<string>(DicomTag.PatientSex));
        Assert.Equal("TAB-3FBB-0001", ds.GetSingleValue<string>(DicomTag.AccessionNumber));

        var sps = ds.GetSequence(DicomTag.ScheduledProcedureStepSequence).Items.Single();
        Assert.Equal("20260814", sps.GetSingleValue<string>(DicomTag.ScheduledProcedureStepStartDate));
        Assert.Equal("CR", sps.GetSingleValue<string>(DicomTag.Modality));
        Assert.Equal("MEDMISSION", sps.GetSingleValue<string>(DicomTag.ScheduledStationAETitle));
        Assert.Equal("TB Screening Chest X-Ray", sps.GetSingleValue<string>(DicomTag.ScheduledProcedureStepDescription));
    }

    [Fact]
    public void unparseable_birth_date_and_missing_name_omit_the_attributes()
    {
        var r = new SurveyRecord { RecordId = "x", RawJson = "{}", BirthDate = "1980-03" };
        var ds = DicomConversions.BuildWorklistItem(r, new MwlOptions());
        Assert.False(ds.Contains(DicomTag.PatientBirthDate));
        Assert.False(ds.Contains(DicomTag.PatientName));
        // SPS date falls back to today when the survey date is absent
        var sps = ds.GetSequence(DicomTag.ScheduledProcedureStepSequence).Items.Single();
        Assert.Equal(8, sps.GetSingleValue<string>(DicomTag.ScheduledProcedureStepStartDate).Length);
    }
}
