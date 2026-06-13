using Microsoft.Data.Sqlite;

namespace Argus.Data.Services;

public sealed class DatabaseBackupService(ArgusDatabaseLocation location)
{
    private const int RetainedBackupCount = 5;

    public string DatabasePath => location.DatabasePath;
    public string DataDirectory => location.DataDirectory;
    public string BackupDirectory => location.BackupDirectory;

    public string? GetLatestBackupPath()
    {
        if (!Directory.Exists(BackupDirectory))
        {
            return null;
        }

        return Directory
            .EnumerateFiles(BackupDirectory, "argus-prestartup-*.db", SearchOption.TopDirectoryOnly)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    public async Task<DatabaseBackupResult> CreateStartupBackupAsync(
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(DatabasePath) || new FileInfo(DatabasePath).Length == 0)
        {
            return new DatabaseBackupResult(
                false,
                null,
                "No existing database needed a safety backup.");
        }

        Directory.CreateDirectory(BackupDirectory);
        var backupPath = Path.Combine(
            BackupDirectory,
            $"argus-prestartup-{DateTimeOffset.Now:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}.db");

        await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var source = new SqliteConnection(
                new SqliteConnectionStringBuilder
                {
                    DataSource = DatabasePath,
                    Mode = SqliteOpenMode.ReadOnly
                }.ToString());
            using var destination = new SqliteConnection(
                new SqliteConnectionStringBuilder
                {
                    DataSource = backupPath,
                    Mode = SqliteOpenMode.ReadWriteCreate
                }.ToString());
            source.Open();
            destination.Open();
            source.BackupDatabase(destination);
        }, cancellationToken);

        await ValidateDatabaseAsync(backupPath, cancellationToken);
        PruneOldBackups();
        return new DatabaseBackupResult(
            true,
            backupPath,
            $"Safety backup created at {Path.GetFileName(backupPath)}.");
    }

    public async Task<DatabaseRestoreResult> RestoreLatestBackupAsync(
        CancellationToken cancellationToken = default)
    {
        var backupPath = GetLatestBackupPath();
        if (string.IsNullOrWhiteSpace(backupPath))
        {
            return new DatabaseRestoreResult(false, null, "No startup backup is available to restore.");
        }

        await ValidateDatabaseAsync(backupPath, cancellationToken);

        var failedCopyPath = Path.Combine(
            BackupDirectory,
            $"argus-before-restore-{DateTimeOffset.Now:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}.db");
        var restoreTempPath = DatabasePath + $".restore-{Guid.NewGuid():N}.tmp";

        await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(DataDirectory);
            Directory.CreateDirectory(BackupDirectory);
            SqliteConnection.ClearAllPools();

            if (File.Exists(DatabasePath))
            {
                File.Copy(DatabasePath, failedCopyPath, overwrite: false);
                CopyIfExists(DatabasePath + "-wal", failedCopyPath + "-wal");
                CopyIfExists(DatabasePath + "-shm", failedCopyPath + "-shm");
            }

            File.Copy(backupPath, restoreTempPath, overwrite: true);
            File.Move(restoreTempPath, DatabasePath, overwrite: true);
            DeleteIfExists(DatabasePath + "-wal");
            DeleteIfExists(DatabasePath + "-shm");
        }, cancellationToken);

        await ValidateDatabaseAsync(DatabasePath, cancellationToken);
        return new DatabaseRestoreResult(
            true,
            backupPath,
            $"Restored {Path.GetFileName(backupPath)}. The replaced database was preserved in Backups.");
    }

    private static async Task ValidateDatabaseAsync(
        string databasePath,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadOnly
            }.ToString());
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA quick_check;";
        var result = Convert.ToString(await command.ExecuteScalarAsync(cancellationToken));
        if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"SQLite integrity check failed for {Path.GetFileName(databasePath)}: {result ?? "unknown error"}.");
        }
    }

    private void PruneOldBackups()
    {
        var staleBackups = Directory
            .EnumerateFiles(BackupDirectory, "argus-prestartup-*.db", SearchOption.TopDirectoryOnly)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .Skip(RetainedBackupCount)
            .ToList();
        foreach (var staleBackup in staleBackups)
        {
            try
            {
                File.Delete(staleBackup);
            }
            catch (IOException)
            {
                // A locked stale backup should not prevent the protected database from opening.
            }
            catch (UnauthorizedAccessException)
            {
                // Retention cleanup is best effort; backup creation and validation already succeeded.
            }
        }
    }

    public IReadOnlyList<BackupFileInfo> GetBackups()
    {
        if (!Directory.Exists(BackupDirectory))
        {
            return Array.Empty<BackupFileInfo>();
        }

        var list = new List<BackupFileInfo>();
        var files = Directory.EnumerateFiles(BackupDirectory, "argus-*.db", SearchOption.TopDirectoryOnly);
        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            var isPrestartup = name.StartsWith("argus-prestartup-");
            var isManual = name.StartsWith("argus-manual-");
            var isBeforeRestore = name.StartsWith("argus-before-restore-");

            if (isPrestartup || isManual || isBeforeRestore)
            {
                var info = new FileInfo(file);
                list.Add(new BackupFileInfo(
                    file,
                    name,
                    info.LastWriteTimeUtc,
                    info.Length,
                    isManual));
            }
        }

        return list.OrderByDescending(b => b.CreatedAt).ToList();
    }

    public async Task<DatabaseBackupResult> CreateManualBackupAsync(
        string? customName = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(DatabasePath) || new FileInfo(DatabasePath).Length == 0)
        {
            return new DatabaseBackupResult(
                false,
                null,
                "No existing database needed a safety backup.");
        }

        Directory.CreateDirectory(BackupDirectory);
        var cleanName = string.IsNullOrWhiteSpace(customName) ? "backup" : string.Concat(customName.Split(Path.GetInvalidFileNameChars()));
        var backupPath = Path.Combine(
            BackupDirectory,
            $"argus-manual-{cleanName}-{DateTimeOffset.Now:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}.db");

        await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var source = new SqliteConnection(
                new SqliteConnectionStringBuilder
                {
                    DataSource = DatabasePath,
                    Mode = SqliteOpenMode.ReadOnly
                }.ToString());
            using var destination = new SqliteConnection(
                new SqliteConnectionStringBuilder
                {
                    DataSource = backupPath,
                    Mode = SqliteOpenMode.ReadWriteCreate
                }.ToString());
            source.Open();
            destination.Open();
            source.BackupDatabase(destination);
        }, cancellationToken);

        await ValidateDatabaseAsync(backupPath, cancellationToken);
        return new DatabaseBackupResult(
            true,
            backupPath,
            $"Manual backup created at {Path.GetFileName(backupPath)}.");
    }

    public async Task<DatabaseRestoreResult> RestoreBackupAsync(
        string backupPath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(backupPath) || !File.Exists(backupPath))
        {
            return new DatabaseRestoreResult(false, null, "The specified backup file does not exist.");
        }

        await ValidateDatabaseAsync(backupPath, cancellationToken);

        var failedCopyPath = Path.Combine(
            BackupDirectory,
            $"argus-before-restore-{DateTimeOffset.Now:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}.db");
        var restoreTempPath = DatabasePath + $".restore-{Guid.NewGuid():N}.tmp";

        await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(DataDirectory);
            Directory.CreateDirectory(BackupDirectory);
            SqliteConnection.ClearAllPools();

            if (File.Exists(DatabasePath))
            {
                File.Copy(DatabasePath, failedCopyPath, overwrite: false);
                CopyIfExists(DatabasePath + "-wal", failedCopyPath + "-wal");
                CopyIfExists(DatabasePath + "-shm", failedCopyPath + "-shm");
            }

            File.Copy(backupPath, restoreTempPath, overwrite: true);
            File.Move(restoreTempPath, DatabasePath, overwrite: true);
            DeleteIfExists(DatabasePath + "-wal");
            DeleteIfExists(DatabasePath + "-shm");
        }, cancellationToken);

        await ValidateDatabaseAsync(DatabasePath, cancellationToken);
        return new DatabaseRestoreResult(
            true,
            backupPath,
            $"Restored {Path.GetFileName(backupPath)}. The replaced database was preserved in Backups.");
    }

    public void DeleteBackup(string backupPath)
    {
        if (File.Exists(backupPath))
        {
            File.Delete(backupPath);
        }
    }

    public long GetDatabaseSizeInBytes()
    {
        if (File.Exists(DatabasePath))
        {
            return new FileInfo(DatabasePath).Length;
        }
        return 0;
    }

    public async Task<string> RunIntegrityCheckAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(DatabasePath))
        {
            return "Database file does not exist.";
        }

        try
        {
            await using var connection = new SqliteConnection(
                new SqliteConnectionStringBuilder
                {
                    DataSource = DatabasePath,
                    Mode = SqliteOpenMode.ReadOnly
                }.ToString());
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA quick_check;";
            var result = Convert.ToString(await command.ExecuteScalarAsync(cancellationToken));
            return result ?? "unknown error";
        }
        catch (Exception ex)
        {
            return $"Check failed: {ex.Message}";
        }
    }

    private static void CopyIfExists(string sourcePath, string destinationPath)
    {
        if (File.Exists(sourcePath))
        {
            File.Copy(sourcePath, destinationPath, overwrite: false);
        }
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
