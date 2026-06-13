namespace Argus.Data.Services;

public sealed record ArgusDatabaseLocation(string DatabasePath)
{
    public string DataDirectory =>
        Path.GetDirectoryName(DatabasePath) ?? AppContext.BaseDirectory;

    public string BackupDirectory =>
        Path.Combine(DataDirectory, "Backups");
}

public sealed record DatabaseInitializationProgress(
    string Status,
    string Detail);

public sealed record DatabaseBackupResult(
    bool Created,
    string? BackupPath,
    string Message);

public sealed record DatabaseRestoreResult(
    bool Restored,
    string? BackupPath,
    string Message);

public sealed record DatabaseStartupResult(
    DatabaseBackupResult Backup);

public sealed record BackupFileInfo(
    string FilePath,
    string FileName,
    DateTimeOffset CreatedAt,
    long SizeBytes,
    bool IsManual)
{
    public string SizeDisplay
    {
        get
        {
            string[] suffixes = { "B", "KB", "MB", "GB" };
            int counter = 0;
            decimal number = SizeBytes;
            while (Math.Round(number / 1024) >= 1)
            {
                number /= 1024;
                counter++;
            }
            return string.Format("{0:n1} {1}", number, suffixes[counter]);
        }
    }
}
