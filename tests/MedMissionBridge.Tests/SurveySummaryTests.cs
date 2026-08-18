using MedMissionBridge.Dicom;

namespace MedMissionBridge.Tests;

/// <summary>
/// The console (MDVizio-X) has no HTTP client, so the only survey content that can
/// reach the operator at acquisition time is what fits in a DICOM description field.
/// These tests pin what gets picked and that it always fits.
/// </summary>
public class SurveySummaryTests
{
    private const string Fallback = "TB Screening Chest X-Ray";

    private static string Build(string json) => SurveySummary.Describe(json, Fallback);

    [Fact]
    public void the_findings_that_change_a_chest_read_are_the_ones_picked()
    {
        var json = """
        {"recordId":"r1",
         "symptoms":["COUGH_2WEEKS_PLUS","BLOOD_IN_SPUTUM","NIGHT_SWEATS","FATIGUE"],
         "tbInfo":{"everDiagnosedTB":"YES","closeContactActiveTB":"YES"}}
        """;

        var summary = Build(json);

        Assert.StartsWith("TB scr", summary);
        Assert.Contains("cough2w", summary);
        Assert.Contains("hemoptysis", summary);
        Assert.Contains("nightsweats", summary);
        Assert.Contains("prevTB", summary);
        Assert.Contains("contact", summary);
        // Fatigue is not a discriminating finding for a screening read — leaving it out
        // is what keeps the line short enough to be read at a glance.
        Assert.DoesNotContain("fatigue", summary);
    }

    [Fact]
    public void a_survey_with_nothing_notable_falls_back_to_the_configured_description()
    {
        var json = """{"recordId":"r1","symptoms":["NONE"],"tbInfo":{"everDiagnosedTB":"NO"}}""";

        Assert.Equal(Fallback, Build(json));
    }

    [Theory]
    [InlineData("""{"recordId":"r1"}""")]
    [InlineData("""{"recordId":"r1","symptoms":[],"tbInfo":{}}""")]
    [InlineData("not json at all")]
    [InlineData("")]
    public void anything_unusable_falls_back_rather_than_failing_the_worklist(string json)
    {
        // A malformed payload must not take the whole worklist response down with it.
        Assert.Equal(Fallback, Build(json));
    }

    [Fact]
    public void the_line_never_exceeds_the_long_string_limit()
    {
        var json = """
        {"recordId":"r1",
         "symptoms":["COUGH","COUGH_2WEEKS_PLUS","SPUTUM","BLOOD_IN_SPUTUM","FEVER",
                     "CHEST_PAIN","SHORTNESS_OF_BREATH","WEIGHT_LOSS","NIGHT_SWEATS","FATIGUE"],
         "tbInfo":{"everDiagnosedTB":"YES","everReceivedTreatment":"YES",
                   "closeContactActiveTB":"YES","householdMemberTBTreatment":"YES"}}
        """;

        var summary = Build(json);

        // DICOM LO is capped at 64 characters; an over-length value is a protocol
        // violation, not a cosmetic problem.
        Assert.True(summary.Length <= 64, $"{summary.Length}자: {summary}");
        Assert.DoesNotContain("…", summary);
    }

    [Fact]
    public void only_a_yes_counts_as_a_positive_history()
    {
        var json = """{"recordId":"r1","tbInfo":{"everDiagnosedTB":"DONT_KNOW","closeContactActiveTB":"NO"}}""";

        Assert.Equal(Fallback, Build(json));
    }
}
