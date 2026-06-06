using System.Text.Json;
using Argus.Core.Models;
using Argus.Core.Services;

namespace Argus.AI.Services;

/// <summary>
/// Persists feed configuration using Argus's ISettingsService (SQLite-backed).
/// </summary>
public class FeedConfigService : IFeedConfigService
{
    private readonly ISettingsService _settings;
    private const string ConfigKey = "feed_config";

    public FeedConfigService(ISettingsService settings)
    {
        _settings = settings;
    }

    public async Task<FeedConfig> LoadConfigAsync(CancellationToken ct = default)
    {
        var json = await _settings.GetSettingAsync(ConfigKey, null, ct);
        if (string.IsNullOrEmpty(json))
            return new FeedConfig();

        try
        {
            return JsonSerializer.Deserialize<FeedConfig>(json) ?? new FeedConfig();
        }
        catch
        {
            return new FeedConfig();
        }
    }

    public async Task SaveConfigAsync(FeedConfig config, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(config);
        await _settings.SaveSettingAsync(ConfigKey, json, ct);
    }
}
