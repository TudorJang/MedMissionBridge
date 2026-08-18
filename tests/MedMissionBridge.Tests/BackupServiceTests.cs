using MedMissionBridge.Data;
using MedMissionBridge.Deployment;
using Microsoft.Data.Sqlite;

namespace MedMissionBridge.Tests;

public class BackupServiceTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), $"bridge-backup-{Guid.NewGuid():N}");

    private string DbPath => Path.Combine(_dir, "bridge.db");
    private string BackupDir => Path.Combine(_dir, "backups");

    public BackupServiceTests()
    {
        Directory.CreateDirectory(_dir);
        using var connection = new SqliteConnection($"Data Source={DbPath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "CREATE TABLE Surveys (RecordId TEXT PRIMARY KEY, RawJson TEXT NOT NULL);"
            + "INSERT INTO Surveys VALUES ('r-1', '{\"recordId\":\"r-1\"}');";
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static DateTime At(int hour, int minute) => new(2026, 8, 18, hour, minute, 0, DateTimeKind.Local);

    [Fact]
    public void a_backup_is_a_readable_database_holding_the_same_rows()
    {
        // The operator copies this file to a USB drive and it is the only copy of a
        // day's surveys, so "a file appeared" is not enough — it has to open.
        var path = BackupService.Create(DbPath, BackupDir, At(18, 30));

        using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT RecordId FROM Surveys";
        Assert.Equal("r-1", command.ExecuteScalar() as string);
    }

    [Fact]
    public void backups_are_named_by_the_moment_they_were_taken()
    {
        var path = BackupService.Create(DbPath, BackupDir, At(18, 30));

        // Sorting by name has to sort by time: the operator picks a file by eye.
        Assert.Equal("bridge-20260818-183000.db", Path.GetFileName(path));
    }

    [Fact]
    public void a_second_backup_in_the_same_minute_does_not_overwrite_the_first()
    {
        var first = BackupService.Create(DbPath, BackupDir, At(18, 30));
        var second = BackupService.Create(DbPath, BackupDir, At(18, 30));

        Assert.NotEqual(first, second);
        Assert.Equal(2, Directory.GetFiles(BackupDir, "bridge-*.db").Length);
    }

    [Fact]
    public void old_backups_are_pruned_so_the_laptop_disk_cannot_fill()
    {
        for (var i = 0; i < 8; i++) BackupService.Create(DbPath, BackupDir, At(10, i));

        BackupService.Prune(BackupDir, keep: 3);

        var remaining = Directory.GetFiles(BackupDir, "bridge-*.db")
            .Select(Path.GetFileName).OrderBy(n => n).ToList();
        Assert.Equal(3, remaining.Count);
        // The newest survive — an old backup is worth less than today's patients.
        Assert.Equal(["bridge-20260818-100500.db", "bridge-20260818-100600.db", "bridge-20260818-100700.db"], remaining);
    }

    [Fact]
    public void pruning_leaves_anything_that_is_not_ours_alone()
    {
        // Operators copy files into this folder by hand; deleting one would be
        // deleting patient data we did not create.
        BackupService.Create(DbPath, BackupDir, At(10, 0));
        var operatorCopy = Path.Combine(BackupDir, "before-reinstall.db");
        File.Copy(DbPath, operatorCopy);

        BackupService.Prune(BackupDir, keep: 0);

        Assert.True(File.Exists(operatorCopy));
        Assert.Empty(Directory.GetFiles(BackupDir, "bridge-*.db"));
    }

    [Fact]
    public void pruning_a_folder_that_was_never_used_is_not_an_error()
    {
        BackupService.Prune(Path.Combine(_dir, "never-created"), keep: 5);
    }
}
