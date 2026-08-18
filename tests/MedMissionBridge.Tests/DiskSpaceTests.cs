using MedMissionBridge.Deployment;

namespace MedMissionBridge.Tests;

/// <summary>
/// A site's images run about 72 MB a study — roughly 10 GB across a 150-patient day —
/// and the PACS server that travels with the team is often unusable on the site network,
/// so the laptop holds everything. The operator has to learn the disk is nearly full
/// before the day starts, not while a queue is waiting.
/// </summary>
public class DiskSpaceTests
{
    private const long GB = 1024L * 1024 * 1024;

    [Fact]
    public void a_day_of_work_left_is_not_worth_mentioning()
    {
        Assert.Null(DiskSpace.Describe(freeBytes: 200 * GB, totalBytes: 500 * GB));
    }

    [Fact]
    public void enough_for_today_but_not_tomorrow_is_a_warning()
    {
        var line = DiskSpace.Describe(freeBytes: 14 * GB, totalBytes: 500 * GB)!;

        Assert.Equal(DiagnosticSeverity.Warning, line.Severity);
        Assert.Contains("14", line.Message);
        // The number that decides whether to act is days of screening, not gigabytes.
        Assert.Contains("day", line.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void not_enough_for_one_day_says_so_plainly()
    {
        var line = DiskSpace.Describe(freeBytes: 4 * GB, totalBytes: 500 * GB)!;

        Assert.Equal(DiagnosticSeverity.Warning, line.Severity);
        // Says outright that today does not fit, and what to do before opening.
        Assert.Contains("less than one screening day", line.Message);
        Assert.Contains("before opening", line.Message);
    }

    [Fact]
    public void an_unreadable_drive_says_nothing_rather_than_guessing()
    {
        // A wrong reassurance is worse than silence here.
        Assert.Null(DiskSpace.Describe(freeBytes: 0, totalBytes: 0));
        Assert.Null(DiskSpace.Describe(freeBytes: -1, totalBytes: 500 * GB));
    }

    [Fact]
    public void the_estimate_uses_what_the_field_studies_actually_weigh()
    {
        // 72 MB a study is measured, not assumed: docs/field-data-findings.md.
        Assert.Equal(72L * 1024 * 1024, DiskSpace.BytesPerStudy);
        Assert.Equal(150, DiskSpace.StudiesPerDay);
    }
}
