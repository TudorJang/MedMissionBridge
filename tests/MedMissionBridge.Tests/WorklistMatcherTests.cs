using FellowOakDicom;
using MedMissionBridge.Data;
using MedMissionBridge.Dicom;

namespace MedMissionBridge.Tests;

public class WorklistMatcherTests
{
    private static DicomDataset Item() => DicomConversions.BuildWorklistItem(
        new SurveyRecord
        {
            RecordId = "r1", RawJson = "{}", No = "TAB-1", Date = "2026-08-14",
            FirstName = "Juan", LastName = "Dela Cruz",
        }, new MwlOptions());

    private static DicomDataset Query(Action<DicomDataset, DicomDataset>? fill = null)
    {
        var q = new DicomDataset();
        var sps = new DicomDataset();
        fill?.Invoke(q, sps);
        if (sps.Any()) q.Add(new DicomSequence(DicomTag.ScheduledProcedureStepSequence, sps));
        return q;
    }

    [Fact]
    public void empty_query_matches() => Assert.True(WorklistMatcher.Matches(Query(), Item()));

    [Theory]
    [InlineData("r1", true)]
    [InlineData("other", false)]
    public void patient_id_exact(string id, bool expected) =>
        Assert.Equal(expected, WorklistMatcher.Matches(
            Query((q, _) => q.Add(DicomTag.PatientID, id)), Item()));

    [Theory]
    [InlineData("TAB-1", true)]
    [InlineData("tab-1", true)]        // typed off a form by hand, case ignored
    [InlineData("TAB-*", true)]
    [InlineData("TAB-2", false)]
    public void accession_number_matches(string accession, bool expected) =>
        // The number printed on the form is what an operator has to hand at the
        // console; without this key the query returned every scheduled patient.
        Assert.Equal(expected, WorklistMatcher.Matches(
            Query((q, _) => q.Add(DicomTag.AccessionNumber, accession)), Item()));

    [Fact]
    public void an_item_without_an_accession_number_does_not_match_one()
    {
        var noAccession = DicomConversions.BuildWorklistItem(
            new SurveyRecord { RecordId = "r2", RawJson = "{}", FirstName = "Ana" },
            new MwlOptions());

        Assert.False(WorklistMatcher.Matches(
            Query((q, _) => q.Add(DicomTag.AccessionNumber, "TAB-1")), noAccession));
    }

    [Theory]
    [InlineData("Dela Cruz^Juan", true)]
    [InlineData("DELA*", true)]
    [InlineData("*juan*", true)]
    [InlineData("?ela*", true)]
    [InlineData("Santos*", false)]
    public void patient_name_wildcards(string pattern, bool expected) =>
        Assert.Equal(expected, WorklistMatcher.Matches(
            Query((q, _) => q.Add(DicomTag.PatientName, pattern)), Item()));

    [Theory]
    [InlineData("DX", true)]
    [InlineData("CR", false)]
    public void modality_exact(string modality, bool expected) =>
        Assert.Equal(expected, WorklistMatcher.Matches(
            Query((_, sps) => sps.Add(DicomTag.Modality, modality)), Item()));

    [Theory]
    [InlineData("20260814", true)]
    [InlineData("20260813", false)]
    [InlineData("20260801-20260820", true)]
    [InlineData("20260815-", false)]
    [InlineData("-20260815", true)]
    public void sps_date_single_and_ranges(string date, bool expected) =>
        Assert.Equal(expected, WorklistMatcher.Matches(
            Query((_, sps) => sps.Add(DicomTag.ScheduledProcedureStepStartDate, date)), Item()));
}
