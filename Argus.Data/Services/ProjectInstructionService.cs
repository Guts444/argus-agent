using Argus.Core.Models;
using Argus.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Argus.Data.Services;

public sealed class ProjectInstructionService(
    IDbContextFactory<ArgusDbContext> dbContextFactory,
    IDiagnosticLog? diagnosticLog = null) : IProjectInstructionService
{
    private const string SettingsKeyPrefix = "ProjectInstruction:v1:";
    private const int BatchSize = 500;
    private readonly SemaphoreSlim writeGate = new(1, 1);

    public async Task<ProjectInstruction?> GetAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        ValidateProjectId(projectId);
        cancellationToken.ThrowIfCancellationRequested();

        await using var db =
            await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var setting = await db.AppSettings
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate => candidate.Key == BuildKey(projectId),
                cancellationToken);
        var instruction = ToInstruction(projectId, setting);
        WriteDiagnostic(
            "get.completed",
            projectId,
            instruction?.Content.Length ?? 0,
            instruction is not null);
        return instruction;
    }

    public async Task<IReadOnlyDictionary<Guid, ProjectInstruction>> GetManyAsync(
        IEnumerable<Guid> projectIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(projectIds);
        cancellationToken.ThrowIfCancellationRequested();

        var ids = projectIds.Distinct().ToArray();
        foreach (var projectId in ids)
        {
            ValidateProjectId(projectId);
        }

        if (ids.Length == 0)
        {
            return new Dictionary<Guid, ProjectInstruction>();
        }

        var results = new Dictionary<Guid, ProjectInstruction>(ids.Length);
        await using var db =
            await dbContextFactory.CreateDbContextAsync(cancellationToken);
        foreach (var idBatch in ids.Chunk(BatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var idsByKey = idBatch.ToDictionary(BuildKey, id => id);
            var keys = idsByKey.Keys.ToArray();
            var settings = await db.AppSettings
                .AsNoTracking()
                .Where(setting => keys.Contains(setting.Key))
                .ToListAsync(cancellationToken);
            foreach (var setting in settings)
            {
                var projectId = idsByKey[setting.Key];
                var instruction = ToInstruction(projectId, setting);
                if (instruction is not null)
                {
                    results[projectId] = instruction;
                }
            }
        }

        diagnosticLog?.Write(
            DiagnosticSeverity.Information,
            nameof(ProjectInstructionService),
            "get_many.completed",
            $"requested_count={ids.Length} found_count={results.Count}");
        return results;
    }

    public async Task<ProjectInstruction?> SaveAsync(
        Guid projectId,
        string? content,
        CancellationToken cancellationToken = default)
    {
        ValidateProjectId(projectId);
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = ProjectInstructionPolicy.Normalize(content);
        if (normalized.Length == 0)
        {
            await ClearAsync(projectId, cancellationToken);
            return null;
        }

        await writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var db =
                await dbContextFactory.CreateDbContextAsync(cancellationToken);
            var key = BuildKey(projectId);
            var setting = await db.AppSettings.SingleOrDefaultAsync(
                candidate => candidate.Key == key,
                cancellationToken);
            var updatedAt = DateTimeOffset.UtcNow;
            if (setting is null)
            {
                setting = new AppSetting
                {
                    Key = key,
                    Value = normalized,
                    UpdatedAt = updatedAt
                };
                db.AppSettings.Add(setting);
            }
            else
            {
                setting.Value = normalized;
                setting.UpdatedAt = updatedAt;
            }

            await db.SaveChangesAsync(cancellationToken);
            WriteDiagnostic(
                "save.completed",
                projectId,
                normalized.Length,
                exists: true);
            return new ProjectInstruction(projectId, normalized, updatedAt);
        }
        finally
        {
            writeGate.Release();
        }
    }

    public async Task ClearAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        ValidateProjectId(projectId);
        cancellationToken.ThrowIfCancellationRequested();

        await writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var db =
                await dbContextFactory.CreateDbContextAsync(cancellationToken);
            var setting = await db.AppSettings.SingleOrDefaultAsync(
                candidate => candidate.Key == BuildKey(projectId),
                cancellationToken);
            if (setting is not null)
            {
                db.AppSettings.Remove(setting);
                await db.SaveChangesAsync(cancellationToken);
            }

            WriteDiagnostic(
                "clear.completed",
                projectId,
                contentLength: 0,
                exists: false);
        }
        finally
        {
            writeGate.Release();
        }
    }

    private static ProjectInstruction? ToInstruction(
        Guid projectId,
        AppSetting? setting)
    {
        if (setting is null)
        {
            return null;
        }

        var normalized = ProjectInstructionPolicy.Normalize(setting.Value);
        return normalized.Length == 0
            ? null
            : new ProjectInstruction(projectId, normalized, setting.UpdatedAt);
    }

    private static string BuildKey(Guid projectId) =>
        $"{SettingsKeyPrefix}{projectId:N}";

    private static void ValidateProjectId(Guid projectId)
    {
        if (projectId == Guid.Empty)
        {
            throw new ArgumentException(
                "A graph project identifier is required.",
                nameof(projectId));
        }
    }

    private void WriteDiagnostic(
        string eventName,
        Guid projectId,
        int contentLength,
        bool exists)
    {
        diagnosticLog?.Write(
            DiagnosticSeverity.Information,
            nameof(ProjectInstructionService),
            eventName,
            $"project_id={projectId:N} content_length={contentLength} exists={exists}");
    }
}
