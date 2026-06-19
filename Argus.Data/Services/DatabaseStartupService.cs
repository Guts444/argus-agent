using Argus.Core.Services;

namespace Argus.Data.Services;

public sealed class DatabaseStartupService(
    DatabaseBackupService backupService,
    ArgusDatabaseInitializer initializer,
    IDiagnosticLog? diagnosticLog = null)
{
    public async Task<DatabaseStartupResult> StartAsync(
        IProgress<DatabaseInitializationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report(new DatabaseInitializationProgress(
            "Protecting your workspace",
            "Creating a consistent local database safety backup."));
        DatabaseBackupResult backup;
        using (diagnosticLog?.BeginOperation("startup", "database_backup"))
        {
            backup = await backupService.CreateStartupBackupAsync(cancellationToken);
        }

        progress?.Report(new DatabaseInitializationProgress(
            "Preparing Argus",
            backup.Message));
        using (diagnosticLog?.BeginOperation("startup", "database_initialize"))
        {
            await initializer.InitializeAsync(cancellationToken, progress);
        }

        progress?.Report(new DatabaseInitializationProgress(
            "Workspace ready",
            "Local data checks and upgrades completed successfully."));
        return new DatabaseStartupResult(backup);
    }
}
