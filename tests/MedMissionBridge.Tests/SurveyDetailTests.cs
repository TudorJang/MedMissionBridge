using FellowOakDicom;
using MedMissionBridge.Data;
using MedMissionBridge.Dicom;

namespace MedMissionBridge.Tests;

public class SurveyDetailTests
{
    private const string FullSurvey = """
    {"recordId":"8f14e45f-ceea-467a-9f9c-6a2f0b4c1d33","no":"TAB-A3F2-0007","date":"2026-08-18",
     "patient":{"firstName":"Juan","lastName":"Dela Cruz","birthDate":"1984-03-12","gender":"MALE",
                "age":42,"address":"12 Mabini St","region":"REGION IV-A","province":"Laguna",
                "city":"Calamba","barangay":"Real","zip":"4027","email":"juan@example.com",
                "cellPhone":"0917-000-0000","maritalStatus":"MARRIED"},
     "medicalHistory":{"items":["HYPERTENSION","DIABETES"],"others":"Migraine",
                       "recentSurgeriesOrHospitalization":"Appendectomy 2019",
                       "currentMedication":"Metformin 500mg"},
     "vitalSigns":{"height":168.0,"weight":61.5,"bpSystolic":130,"bpDiastolic":85,
                   "pulseRate":78,"respiratoryRate":18,"temperature":36.8,
                   "oxygenSaturation":98.0,"bloodGlucose":105.0},
     "symptoms":["COUGH_2WEEKS_PLUS","NIGHT_SWEATS"],
     "tbInfo":{"everDiagnosedTB":"NO","everReceivedTreatment":"NO",
               "closeContactActiveTB":"YES","closeContactWhen":"2025",
               "householdMemberTBTreatment":"DONT_KNOW"},
     "smoking":{"status":"FORMER","duration":"FIVE_TO_10"},
     "alcohol":{"drinks":true,"amount":"ONE_TO_TWO"},
     "environmentalExposure":{"dustSmokeChemicalExposure":true,"cooksWithSolidFuels":true,
                              "secondhandSmokeExposure":false,"crowdedLivingConditions":false}}
    """;

    private static DicomDataset Item(string rawJson) => DicomConversions.BuildWorklistItem(
        new SurveyRecord { RecordId = "r1", RawJson = rawJson, No = "TAB-A3F2-0007" },
        new MwlOptions());

    [Fact]
    public void the_payload_travels_byte_for_byte_in_the_private_element()
    {
        // This is the guarantee the whole design rests on: whatever else is lost in
        // translation, the answers themselves are recoverable exactly as given.
        var ds = Item(FullSurvey);

        Assert.Equal(FullSurvey, ds.GetSingleValue<string>(SurveyDetail.SurveyJson));
        Assert.Equal(SurveyDetail.SchemaName, ds.GetSingleValue<string>(SurveyDetail.SurveySchema));
    }

    [Fact]
    public void the_private_block_is_claimed_so_another_vendor_cannot_be_misread()
    {
        var ds = Item(FullSurvey);

        var creators = ds.Where(e => e.Tag.Group == 0x7777 && e.Tag.Element == 0x0010)
            .Select(e => ds.GetSingleValue<string>(e.Tag)).ToList();
        Assert.Contains(SurveyDetail.PrivateCreator, creators);
    }

    [Fact]
    public void standard_attributes_are_used_where_dicom_has_one()
    {
        var ds = Item(FullSurvey);

        Assert.Equal("0917-000-0000", ds.GetSingleValue<string>(DicomTag.PatientTelephoneNumbers));
        // DICOM records size in metres; the tablet asks in centimetres.
        Assert.Equal(1.68m, ds.GetSingleValue<decimal>(DicomTag.PatientSize));
        Assert.Equal(61.5m, ds.GetSingleValue<decimal>(DicomTag.PatientWeight));
    }

    [Theory]
    [InlineData("CURRENT", "YES")]
    [InlineData("NEVER", "NO")]
    public void smoking_status_is_sent_when_it_has_an_honest_value(string status, string expected)
    {
        var ds = Item($$""" {"recordId":"r1","smoking":{"status":"{{status}}"} } """.Trim());

        Assert.Equal(expected, ds.GetSingleValue<string>(DicomTag.SmokingStatus));
    }

    [Fact]
    public void a_former_smoker_leaves_the_attribute_out_rather_than_guessing()
    {
        // Smoking Status asks whether the patient smokes now. Neither YES nor NO is
        // true of a former smoker, and the detail is in the history text instead.
        var ds = Item("""{"recordId":"r1","smoking":{"status":"FORMER","duration":"FIVE_TO_10"}}""");

        Assert.False(ds.Contains(DicomTag.SmokingStatus));
        Assert.Contains("FORMER", ds.GetSingleValue<string>(DicomTag.AdditionalPatientHistory));
    }

    [Fact]
    public void the_readable_history_covers_every_section_that_was_answered()
    {
        var text = SurveyDetail.ToText(FullSurvey);

        Assert.Contains("Vitals: Ht 168cm, Wt 61.5kg, BP 130/85 mmHg", text);
        Assert.Contains("SpO2 98%", text);
        Assert.Contains("History: HYPERTENSION, DIABETES", text);
        Assert.Contains("meds Metformin 500mg", text);
        Assert.Contains("Symptoms: COUGH_2WEEKS_PLUS, NIGHT_SWEATS", text);
        Assert.Contains("contact YES", text);
        Assert.Contains("household DONT_KNOW", text);
        Assert.Contains("Alcohol: yes, ONE_TO_TWO", text);
        Assert.Contains("Exposure: dust/smoke/chemical, solid fuels", text);
        Assert.Contains("Calamba", text);
        // False answers are not findings; printing them would bury the true ones.
        Assert.DoesNotContain("secondhand smoke", text);
    }

    [Fact]
    public void a_blank_survey_produces_no_empty_sections()
    {
        var blank = """{"recordId":"r1","patient":{},"medicalHistory":{},"vitalSigns":{},"symptoms":[],"tbInfo":{},"smoking":{},"alcohol":{},"environmentalExposure":{}}""";

        Assert.Equal("", SurveyDetail.ToText(blank));
    }

    [Fact]
    public void an_unparseable_payload_still_produces_a_valid_worklist_item()
    {
        // A broken payload must cost that one patient's detail, not the whole worklist.
        var ds = Item("not json at all");

        Assert.Equal("not json at all", ds.GetSingleValue<string>(SurveyDetail.SurveyJson));
        Assert.False(ds.Contains(DicomTag.AdditionalPatientHistory));
        Assert.Equal("r1", ds.GetSingleValue<string>(DicomTag.PatientID));
    }
}
