using MedMissionBridge.Ingest;

namespace MedMissionBridge.Tests;

public class PayloadExtractorTests
{
    private const string Full = """
        {"recordId":"3f8b1c2e-9a4d-4c11-8e77-2b6a0d5f9c31","no":"TAB-3FBB-0001",
         "date":"2026-08-14",
         "patient":{"firstName":"Juan","lastName":"Dela Cruz","birthDate":"1980-03-04",
           "gender":"MALE","address":"12 Mabini St","region":"NATIONAL CAPITAL REGION (NCR)",
           "city":"City Of Manila","barangay":"Ermita","zip":"1000"},
         "medicalHistory":{},"vitalSigns":{},"symptoms":[],"tbInfo":{},"smoking":{},
         "alcohol":{},"environmentalExposure":{}}
        """;

    [Fact]
    public void extracts_worklist_fields_from_a_full_payload()
    {
        Assert.True(PayloadExtractor.TryExtract(Full, out var e));
        Assert.Equal("3f8b1c2e-9a4d-4c11-8e77-2b6a0d5f9c31", e!.RecordId);
        Assert.Equal("TAB-3FBB-0001", e.No);
        Assert.Equal("2026-08-14", e.Date);
        Assert.Equal("Juan", e.FirstName);
        Assert.Equal("Dela Cruz", e.LastName);
        Assert.Equal("1980-03-04", e.BirthDate);
        Assert.Equal("MALE", e.Gender);
        Assert.Equal("NATIONAL CAPITAL REGION (NCR)", e.Region);
        Assert.Null(e.Province); // absent-not-null: NCR has no province key at all
        Assert.Equal("City Of Manila", e.City);
        Assert.Equal("Ermita", e.Barangay);
        Assert.Equal("1000", e.Zip);
        Assert.Equal("12 Mabini St", e.Address);
    }

    [Fact]
    public void a_blank_survey_still_extracts_its_record_id()
    {
        var json = """{"recordId":"abc","patient":{},"medicalHistory":{},"vitalSigns":{},"symptoms":[],"tbInfo":{},"smoking":{},"alcohol":{},"environmentalExposure":{}}""";
        Assert.True(PayloadExtractor.TryExtract(json, out var e));
        Assert.Equal("abc", e!.RecordId);
        Assert.Null(e.No);
        Assert.Null(e.FirstName);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{}")]
    [InlineData("""{"recordId":""}""")]
    [InlineData("""{"recordId":null}""")]
    public void rejects_payloads_without_a_usable_record_id(string json)
    {
        Assert.False(PayloadExtractor.TryExtract(json, out var e));
        Assert.Null(e);
    }
}
