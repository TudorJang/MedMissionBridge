using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace MedMissionBridge.Data;

public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<BridgeDbContext>
{
    public BridgeDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<BridgeDbContext>()
            .UseSqlite("Data Source=design-time.db").Options);
}
