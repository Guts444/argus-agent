using Argus.Core.Models;
using Argus.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Argus.Data.Services;

public sealed class LocalDevelopmentSecretStore(IDbContextFactory<ArgusDbContext> dbContextFactory) : ISecretStore
{
    public async Task<string?> GetSecretAsync(string key, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await db.AppSettings
            .AsNoTracking()
            .Where(setting => setting.Key == SecretKey(key))
            .Select(setting => setting.Value)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task SetSecretAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var storedKey = SecretKey(key);
        var setting = await db.AppSettings.FirstOrDefaultAsync(existing => existing.Key == storedKey, cancellationToken);
        if (setting is null)
        {
            db.AppSettings.Add(new AppSetting { Key = storedKey, Value = value, UpdatedAt = DateTimeOffset.UtcNow });
        }
        else
        {
            setting.Value = value;
            setting.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task RemoveSecretAsync(string key, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var setting = await db.AppSettings.FirstOrDefaultAsync(existing => existing.Key == SecretKey(key), cancellationToken);
        if (setting is not null)
        {
            db.AppSettings.Remove(setting);
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private static string SecretKey(string key)
    {
        // Local dev fallback only. The WinUI app registers WindowsSecretStore instead.
        return $"secret:{key}";
    }
}
