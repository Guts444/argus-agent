namespace Argus.Data.Services;

public sealed class DatabaseStartupService(
    DatabaseBackupService backupService,
    ArgusDatabaseInitializer initializer)
{
    public async Task<DatabaseStartupResult> StartAsync(
        IProgress<DatabaseInitializationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report(new DatabaseInitializationProgress(
            "Protecting your workspace",
            "Creating a consistent local database safety backup."));
        var backup = await backupService.CreateStartupBackupAsync(cancellationToken);

        progress?.Report(new DatabaseInitializationProgress(
            "Preparing Argus",
            backup.Message));
        await initializer.InitializeAsync(cancellationToken, progress);

        progress?.Report(new DatabaseInitializationProgress(
            "Workspace ready",
            "Local data checks and upgrades completed successfully."));
        return new DatabaseStartupResult(backup);
    }
}
