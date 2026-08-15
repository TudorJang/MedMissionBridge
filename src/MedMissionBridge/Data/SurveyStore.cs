using MedMissionBridge.Ingest;
using Microsoft.EntityFrameworkCore;

namespace MedMissionBridge.Data;

public class SurveyStore(IDbContextFactory<BridgeDbContext> factory)
{
    public async Task UpsertAsync(ExtractedSurvey e, string rawJson)
    {
        await using (var ctx = await factory.CreateDbContextAsync())
        {
            var now = DateTime.UtcNow;
            var existing = await ctx.Surveys.FindAsync(e.RecordId);
            if (existing is not null)
            {
                ApplyUpdate(existing, e, rawJson, now);
                await ctx.SaveChangesAsync();
                return;
            }

            ctx.Surveys.Add(new SurveyRecord
            {
                RecordId = e.RecordId, ReceivedAtUtc = now, UpdatedAtUtc = now,
                Status = WorklistStatus.Received, RawJson = rawJson,
                No = e.No, Date = e.Date, FirstName = e.FirstName, LastName = e.LastName,
                BirthDate = e.BirthDate, Gender = e.Gender, Region = e.Region,
                Province = e.Province, City = e.City, Barangay = e.Barangay,
                Zip = e.Zip, Address = e.Address,
            });

            try
            {
                await ctx.SaveChangesAsync();
                return;
            }
            catch (DbUpdateException)
            {
                // Two near-simultaneous first-sends of the same RecordId (tablet
                // double-submit before the first response returns) both see
                // "no existing row" and both try to insert; the loser hits the
                // RecordId primary-key constraint here. Fall through and retry
                // as an update against a fresh context — the winner's row must
                // exist by now.
            }
        }

        await using var retryCtx = await factory.CreateDbContextAsync();
        var now2 = DateTime.UtcNow;
        var winner = await retryCtx.Surveys.SingleAsync(x => x.RecordId == e.RecordId);
        ApplyUpdate(winner, e, rawJson, now2);
        await retryCtx.SaveChangesAsync();
    }

    // A tablet edit-and-resend refreshes the survey but must not change
    // Status or ReceivedAtUtc: a completed study must not silently
    // reappear on the modality worklist (spec section 5).
    private static void ApplyUpdate(SurveyRecord existing, ExtractedSurvey e, string rawJson, DateTime now)
    {
        existing.UpdatedAtUtc = now; existing.RawJson = rawJson;
        existing.No = e.No; existing.Date = e.Date;
        existing.FirstName = e.FirstName; existing.LastName = e.LastName;
        existing.BirthDate = e.BirthDate; existing.Gender = e.Gender;
        existing.Region = e.Region; existing.Province = e.Province;
        existing.City = e.City; existing.Barangay = e.Barangay;
        existing.Zip = e.Zip; existing.Address = e.Address;
    }
}
