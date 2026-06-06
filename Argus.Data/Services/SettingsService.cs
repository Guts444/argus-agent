using Argus.Core.Models;
using Argus.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Argus.Data.Services;

public sealed class SettingsService(IDbContextFactory<ArgusDbContext> dbContextFactory) : ISettingsService
{
    public async Task<IReadOnlyList<AiProviderProfile>> GetAiProviderProfilesAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await db.AiProviderProfiles
            .AsNoTracking()
            .OrderByDescending(profile => profile.IsDefault)
            .ThenBy(profile => profile.Name)
            .ToListAsync(cancellationToken);
    }

    public async Task<AiProviderProfile?> GetDefaultAiProviderProfileAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await db.AiProviderProfiles
            .AsNoTracking()
            .OrderByDescending(profile => profile.IsDefault)
            .ThenBy(profile => profile.Name)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<AiProviderProfile> SaveAiProviderProfileAsync(AiProviderProfile profile, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        if (profile.IsDefault)
        {
            await db.AiProviderProfiles
                .Where(existing => existing.Id != profile.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(existing => existing.IsDefault, false), cancellationToken);
        }

        var existingProfile = await db.AiProviderProfiles.FirstOrDefaultAsync(existing => existing.Id == profile.Id, cancellationToken);
        if (existingProfile is null)
        {
            db.AiProviderProfiles.Add(profile);
            await db.SaveChangesAsync(cancellationToken);
            return profile;
        }

        existingProfile.Name = profile.Name;
        existingProfile.ProviderType = profile.ProviderType;
        existingProfile.BaseUrl = profile.BaseUrl;
        existingProfile.Model = profile.Model;
        existingProfile.ApiKeyStorageKey = profile.ApiKeyStorageKey;
        existingProfile.ThinkingMode = profile.ThinkingMode;
        existingProfile.ReasoningEffort = profile.ReasoningEffort;
        existingProfile.OrganizationId = profile.OrganizationId;
        existingProfile.ProjectId = profile.ProjectId;
        existingProfile.IsDefault = profile.IsDefault;
        await db.SaveChangesAsync(cancellationToken);
        return existingProfile;
    }

    public async Task<string?> GetSettingAsync(string key, string? defaultValue = null, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var setting = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == key, cancellationToken);
        return setting?.Value ?? defaultValue;
    }

    public async Task SaveSettingAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var setting = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == key, cancellationToken);
        if (setting is null)
        {
            db.AppSettings.Add(new AppSetting
            {
                Key = key,
                Value = value,
                UpdatedAt = DateTimeOffset.UtcNow
            });
        }
        else
        {
            setting.Value = value;
            setting.UpdatedAt = DateTimeOffset.UtcNow;
        }
        await db.SaveChangesAsync(cancellationToken);
    }
}
