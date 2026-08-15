using System.Net;
using System.Net.Http.Headers;

namespace MedMissionBridge.Tests;

public class IngestApiTests
{
    private const string Payload = """{"recordId":"r-100","no":"TAB-1","patient":{"firstName":"Juan"},"medicalHistory":{},"vitalSigns":{},"symptoms":[],"tbInfo":{},"smoking":{},"alcohol":{},"environmentalExposure":{}}""";

    private static HttpRequestMessage Post(string body, string? key = BridgeAppFactory.ApiKey)
    {
        var m = new HttpRequestMessage(HttpMethod.Post, "/api/v1/surveys")
        { Content = new StringContent(body) };
        m.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        if (key != null) m.Headers.Add("X-Api-Key", key);
        return m;
    }

    [Fact]
    public async Task accepts_a_valid_payload_and_is_idempotent()
    {
        using var app = new BridgeAppFactory();
        var client = app.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Post(Payload))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Post(Payload))).StatusCode);
    }

    [Fact]
    public async Task wrong_or_missing_key_is_401()
    {
        using var app = new BridgeAppFactory();
        var client = app.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Post(Payload, "bad"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Post(Payload, null))).StatusCode);
    }

    [Fact]
    public async Task body_without_record_id_is_400()
    {
        using var app = new BridgeAppFactory();
        var client = app.CreateClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(Post("{}"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(Post("not json"))).StatusCode);
    }
}
