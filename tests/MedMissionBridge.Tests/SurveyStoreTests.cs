using MedMissionBridge.Data;
using MedMissionBridge.Ingest;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace MedMissionBridge.Tests;

public sealed class SurveyStoreTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"bridge-test-{Guid.NewGuid():N}.db");
    private readonly PooledDbContextFactory<BridgeDbContext> _factory;
    private readonly SurveyStore _store;

    public SurveyStoreTests()
    {
        var options = new DbContextOptionsBuilder<BridgeDbContext>()
            .UseSqlite($"Data Source={_dbPath}").Options;
        _factory = new PooledDbContextFactory<BridgeDbContext>(options);
        using var ctx = _factory.CreateDbContext();
        ctx.Database.Migrate();
        _store = new SurveyStore(_factory);
    }

    public void Dispose()
    {
        using (var ctx = _factory.CreateDbContext()) ctx.Database.EnsureDeleted();
    }

    private static ExtractedSurvey Extracted(string id, string? first = "Juan") =>
        new(id, "TAB-3FBB-0001", "2026-08-14", first, "Dela Cruz", "1980-03-04",
            "MALE", "NCR", null, "City Of Manila", "Ermita", "1000", "12 Mabini St");

    [Fact]
    public async Task insert_starts_as_received_and_stores_raw_json()
    {
        await _store.UpsertAsync(Extracted("r1"), """{"recordId":"r1"}""");
        using var ctx = _factory.CreateDbContext();
        var r = await ctx.Surveys.SingleAsync(x => x.RecordId == "r1");
        Assert.Equal(WorklistStatus.Received, r.Status);
        Assert.Equal("""{"recordId":"r1"}""", r.RawJson);
        Assert.Equal("Juan", r.FirstName);
    }

    [Fact]
    public async Task resend_updates_data_but_preserves_status_and_received_time()
    {
        await _store.UpsertAsync(Extracted("r1"), "v1");
        DateTime firstReceived;
        using (var ctx = _factory.CreateDbContext())
        {
            var r = await ctx.Surveys.SingleAsync(x => x.RecordId == "r1");
            firstReceived = r.ReceivedAtUtc;
            r.Status = WorklistStatus.Completed;
            await ctx.SaveChangesAsync();
        }

        await _store.UpsertAsync(Extracted("r1", first: "Maria"), "v2");

        using (var ctx = _factory.CreateDbContext())
        {
            var r = await ctx.Surveys.SingleAsync(x => x.RecordId == "r1");
            Assert.Equal("Maria", r.FirstName);        // data updated
            Assert.Equal("v2", r.RawJson);
            Assert.Equal(WorklistStatus.Completed, r.Status); // status preserved
            Assert.Equal(firstReceived, r.ReceivedAtUtc);     // audit preserved
            Assert.True(r.UpdatedAtUtc >= firstReceived);
        }
    }
}
