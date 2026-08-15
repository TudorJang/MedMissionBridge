using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace MedMissionBridge.Tests;

public class UiApiTests
{
    private const string Payload = """{"recordId":"r-200","no":"TAB-2","patient":{"firstName":"Maria","lastName":"Santos","city":"Taytay"},"medicalHistory":{},"vitalSigns":{},"symptoms":[],"tbInfo":{},"smoking":{},"alcohol":{},"environmentalExposure":{}}""";

    private static async Task Seed(HttpClient client)
    {
        var m = new HttpRequestMessage(HttpMethod.Post, "/api/v1/surveys")
        { Content = new StringContent(Payload) };
        m.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        m.Headers.Add("X-Api-Key", BridgeAppFactory.ApiKey);
        await client.SendAsync(m);
    }

    [Fact]
    public async Task list_search_and_detail_round_trip()
    {
        using var app = new BridgeAppFactory();
        var client = app.CreateClient();
        await Seed(client);

        var list = await client.GetFromJsonAsync<JsonElement>("/api/ui/records?search=santos");
        Assert.Equal(1, list.GetArrayLength());
        Assert.Equal("r-200", list[0].GetProperty("recordId").GetString());
        Assert.Equal("Received", list[0].GetProperty("status").GetString());

        var detail = await client.GetFromJsonAsync<JsonElement>("/api/ui/records/r-200");
        Assert.Equal(Payload, detail.GetProperty("rawJson").GetString());

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/ui/records/none")).StatusCode);
    }

    [Fact]
    public async Task status_change_paths()
    {
        using var app = new BridgeAppFactory();
        var client = app.CreateClient();
        await Seed(client);

        var ok = await client.PostAsJsonAsync("/api/ui/records/r-200/status", new { status = "InProgress" });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        var invalid = await client.PostAsJsonAsync("/api/ui/records/r-200/status", new { status = "InProgress" });
        Assert.Equal(HttpStatusCode.Conflict, invalid.StatusCode); // InProgress→InProgress not allowed

        var unknownValue = await client.PostAsJsonAsync("/api/ui/records/r-200/status", new { status = "Nonsense" });
        Assert.Equal(HttpStatusCode.BadRequest, unknownValue.StatusCode);

        // Enum.TryParse also accepts the raw numeric value ("2" == Completed);
        // that must be rejected — only the named statuses are valid input.
        var numeric = await client.PostAsJsonAsync("/api/ui/records/r-200/status", new { status = "2" });
        Assert.Equal(HttpStatusCode.BadRequest, numeric.StatusCode);

        var missing = await client.PostAsJsonAsync("/api/ui/records/none/status", new { status = "Completed" });
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task health_reports_config()
    {
        using var app = new BridgeAppFactory();
        var client = app.CreateClient();
        var health = await client.GetFromJsonAsync<JsonElement>("/api/ui/health");
        Assert.Equal(11112, health.GetProperty("mwlPort").GetInt32());
        Assert.Equal("MEDMISSION", health.GetProperty("mwlAeTitle").GetString());
        // AE title is advertised only, never enforced by the SCP.
        Assert.False(health.GetProperty("mwlAeTitleEnforced").GetBoolean());
        // The MWL/mDNS background servers are skipped entirely in the Testing
        // environment (see Program.cs [ANCHOR:SERVERS]) — false is honest here.
        Assert.False(health.GetProperty("mwlRunning").GetBoolean());
        Assert.False(health.GetProperty("mdnsRunning").GetBoolean());
        // BridgeAppFactory configures a non-default test key.
        Assert.False(health.GetProperty("apiKeyIsDefault").GetBoolean());
    }

    [Fact]
    public async Task root_serves_the_management_page()
    {
        using var app = new BridgeAppFactory();
        var client = app.CreateClient();
        var html = await client.GetStringAsync("/");
        Assert.Contains("MedMission Bridge", html);
    }
}
