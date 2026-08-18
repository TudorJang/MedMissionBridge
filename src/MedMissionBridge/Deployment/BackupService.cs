using System.Globalization;
using Microsoft.Data.Sqlite;

namespace MedMissionBridge.Deployment;

/// <summary>
/// Point-in-time copies of the survey database. Everything a site collects lives only
/// on that laptop, so a lost or broken laptop is lost patient data unless someone
/// carries a copy off. Telling the operator to copy the live database file is not
/// enough — a copy taken mid-write can be unreadable — so the bridge writes a
/// consistent snapshot with VACUUM INTO and the operator copies that.
/// </summary>
public static class BackupService
{
    public const string FilePrefix = "bridge-";
    public const int DefaultKeep = 14;

    public static string Create(string dbPath, string backupDir, DateTime takenAt)
    {
        Directory.CreateDirectory(backupDir);
        var stamp = takenAt.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var path = Path.Combine(backupDir, $"{FilePrefix}{stamp}.db");

        // Two backups within the same second are the operator pressing the button
        // twice; the second must not silently replace the first.
        var suffix = 2;
        while (File.Exists(path))
            path = Path.Combine(backupDir, $"{FilePrefix}{stamp}-{suffix++}.db");

        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();
        using var command = connection.CreateCommand();
        // VACUUM INTO takes a read lock and writes a defragmented, self-consistent
        // database, unlike copying the file out from under an open connection.
        command.CommandText = "VACUUM INTO $target";
        command.Parameters.AddWithValue("$target", path);
        command.ExecuteNonQuery();
        return path;
    }

    /// <summary>Deletes the oldest of our own backups, never anything else in the folder:
    /// operators put their own copies here and those are patient data too.</summary>
    public static void Prune(string backupDir, int keep = DefaultKeep)
    {
        if (!Directory.Exists(backupDir)) return;
        var ours = Directory.GetFiles(backupDir, $"{FilePrefix}*.db")
            .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
            .Skip(keep);
        foreach (var path in ours)
        {
            try { File.Delete(path); }
            catch (IOException) { /* held open by a copy in progress; next prune gets it */ }
        }
    }
}
