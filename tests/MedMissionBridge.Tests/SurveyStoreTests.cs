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

    [Fact]
    public async Task concurrent_first_sends_of_the_same_record_do_not_throw()
    {
        await Task.WhenAll(
            _store.UpsertAsync(Extracted("race"), "v1"),
            _store.UpsertAsync(Extracted("race"), "v2"));

        using var ctx = _factory.CreateDbContext();
        var rows = await ctx.Surveys.Where(x => x.RecordId == "race").ToListAsync();
        Assert.Single(rows);
        Assert.Equal(WorklistStatus.Received, rows[0].Status);
    }

    [Fact]
    public async Task scheduled_excludes_completed_and_cancelled()
    {
        await _store.UpsertAsync(Extracted("a"), "{}");
        await _store.UpsertAsync(Extracted("b"), "{}");
        await _store.UpsertAsync(Extracted("c"), "{}");
        Assert.Equal(StatusChangeResult.Changed, await _store.TryChangeStatusAsync("b", WorklistStatus.InProgress));
        Assert.Equal(StatusChangeResult.Changed, await _store.TryChangeStatusAsync("c", WorklistStatus.Completed));

        var scheduled = await _store.GetScheduledAsync();
        Assert.Equal(new[] { "a", "b" }, scheduled.Select(r => r.RecordId).OrderBy(x => x));
    }

    [Fact]
    public async Task invalid_transition_is_rejected_and_unknown_id_reported()
    {
        await _store.UpsertAsync(Extracted("a"), "{}");
        Assert.Equal(StatusChangeResult.Changed, await _store.TryChangeStatusAsync("a", WorklistStatus.Completed));
        Assert.Equal(StatusChangeResult.InvalidTransition, await _store.TryChangeStatusAsync("a", WorklistStatus.Cancelled));
        Assert.Equal(StatusChangeResult.NotFound, await _store.TryChangeStatusAsync("nope", WorklistStatus.Completed));
    }

    [Fact]
    public async Task search_matches_name_no_and_city_case_insensitively()
    {
        await _store.UpsertAsync(Extracted("a"), "{}");
        Assert.Single(await _store.ListAsync("juan", null));
        Assert.Single(await _store.ListAsync("3fbb", null));
        Assert.Single(await _store.ListAsync("manila", null));
        Assert.Empty(await _store.ListAsync("zzz", null));
        Assert.Single(await _store.ListAsync(null, WorklistStatus.Received));
        Assert.Empty(await _store.ListAsync(null, WorklistStatus.Completed));
    }

    [Fact]
    public async Task accession_lookup_returns_latest_updated_match()
    {
        await _store.UpsertAsync(Extracted("a"), "{}");
        await Task.Delay(10);
        await _store.UpsertAsync(Extracted("b"), "{}"); // same No in the fixture
        var hit = await _store.GetByAccessionAsync("TAB-3FBB-0001");
        Assert.Equal("b", hit!.RecordId);
    }
}
