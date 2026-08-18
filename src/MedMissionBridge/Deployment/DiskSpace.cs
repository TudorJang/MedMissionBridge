namespace MedMissionBridge.Deployment;

/// <summary>
/// How much screening the laptop still has room for. The bridge's own database is a few
/// megabytes and would never fill a disk; the X-ray images are what does, and the console
/// writes those to the same drive. A PACS server travels with the team but is often
/// unusable on the site network, so on those days nothing leaves the laptop.
/// </summary>
public static class DiskSpace
{
    /// <summary>Measured across a full day of field studies — see docs/field-data-findings.md.</summary>
    public const long BytesPerStudy = 72L * 1024 * 1024;

    public const int StudiesPerDay = 150;

    private const long BytesPerDay = BytesPerStudy * StudiesPerDay;

    /// <summary>Below this the operator should act before opening rather than during.</summary>
    private const int WarnBelowDays = 2;

    public static Diagnostic? Describe(long freeBytes, long totalBytes)
    {
        // Nothing sensible to say about a drive we could not read; a wrong reassurance
        // is worse than silence.
        if (freeBytes <= 0 || totalBytes <= 0) return null;

        var days = (double)freeBytes / BytesPerDay;
        if (days >= WarnBelowDays) return null;

        var freeGb = freeBytes / 1024d / 1024 / 1024;
        var message = days < 1
            ? $"Only {freeGb:0.#} GB free — less than one screening day of X-ray images "
              + $"({StudiesPerDay} studies is about {BytesPerDay / 1024d / 1024 / 1024:0} GB). "
              + "Move images off this laptop before opening."
            : $"{freeGb:0.#} GB free — about {days:0.#} screening days of X-ray images left. "
              + "Move images off after today, or the drive fills mid-queue.";

        return new Diagnostic(DiagnosticSeverity.Warning, message);
    }

    /// <summary>Null when the drive cannot be inspected — a removed or unreadable volume
    /// must not take the health page down with it.</summary>
    public static Diagnostic? ForPath(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root)) return null;
            var drive = new DriveInfo(root);
            return drive.IsReady ? Describe(drive.AvailableFreeSpace, drive.TotalSize) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
