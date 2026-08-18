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
    [InlineData("1984-03-12", "2026-08-18", "042Y")]
    [InlineData("1984-08-18", "2026-08-18", "042Y")]  // birthday today
    [InlineData("1984-08-19", "2026-08-18", "041Y")]  // birthday tomorrow
    [InlineData("2027-01-01", "2026-08-18", null)]    // not yet born: nonsense in, nothing out
    [InlineData("1984-03", "2026-08-18", null)]
    [InlineData(null, "2026-08-18", null)]
    public void iso_to_age(string? birth, string on, string? expected) =>
        Assert.Equal(expected, DicomConversions.ToDicomAge(birth, DateOnly.Parse(on)));

    [Fact]
    public void address_keeps_the_narrow_parts_and_fits_the_field()
    {
        var record = new SurveyRecord
        {
            RecordId = "r1", RawJson = "{}",
            Address = "12 Mabini St", Barangay = "Real", City = "Calamba",
            Province = "Laguna", Region = "REGION IV-A CALABARZON", Zip = "4027",
        };

        var address = DicomConversions.ToPatientAddress(record)!;

        Assert.True(address.Length <= 64, address);
        Assert.StartsWith("12 Mabini St, Real, Calamba", address);
    }

    [Fact]
    public void address_is_omitted_when_nothing_was_entered() =>
        Assert.Null(DicomConversions.ToPatientAddress(new SurveyRecord { RecordId = "r1", RawJson = "{}" }));

    [Fact]
    public void the_study_uid_is_stable_for_a_record_and_unique_between_records()
    {
        // Every C-FIND rebuilds the item from scratch; a UID that changed per query
        // would break any link the console or PACS made to the study.
        var id = "8f14e45f-ceea-467a-9f9c-6a2f0b4c1d33";

        var first = DicomConversions.ToStudyInstanceUid(id);

        Assert.Equal(first, DicomConversions.ToStudyInstanceUid(id));
        Assert.NotEqual(first, DicomConversions.ToStudyInstanceUid("other-record"));
        Assert.StartsWith("2.25.", first);
        Assert.True(first.Length <= 64, first);
        Assert.Matches("^2[.]25[.][0-9]+$", first);
    }

    [Fact]
    public void a_non_uuid_record_id_still_yields_a_valid_uid() =>
        Assert.Matches("^2[.]25[.][0-9]+$", DicomConversions.ToStudyInstanceUid("r1"));

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
