using Microsoft.EntityFrameworkCore;

namespace MedMissionBridge.Data;

public class BridgeDbContext(DbContextOptions<BridgeDbContext> options) : DbContext(options)
{
    public DbSet<SurveyRecord> Surveys => Set<SurveyRecord>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        var e = mb.Entity<SurveyRecord>();
        e.HasKey(x => x.RecordId);
        e.Property(x => x.Status).HasConversion<string>();
        e.HasIndex(x => x.No);
        e.HasIndex(x => x.Status);
    }
}
