using System.Text.Json;
using Argus.Core.Services;

namespace Argus.Data.Services;

public sealed class ProjectActionDispositionService(
    ISettingsService settingsService) : IProjectActionDispositionService
{
    private const string SettingsKey = "ProjectActionDispositions:v1";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<CoherentDashboard> ApplyAsync(
        CoherentDashboard dashboard,
        DateTimeOffset? now = null,
        CancellationToken cancellationToken = default)
    {
        var activeKeys = (await LoadAsync(now ?? DateTimeOffset.UtcNow, cancellationToken))
            .Select(disposition => disposition.RecommendationKey)
            .ToHashSet(StringComparer.Ordinal);
        if (activeKeys.Count == 0)
        {
            return dashboard;
        }

        var cards = dashboard.ProjectCards
            .Select(card => card with
            {
                Actions = card.Actions
                    .Where(action => !activeKeys.Contains(action.RecommendationKey))
                    .ToArray()
            })
            .ToArray();
        var globalActions = cards
            .SelectMany(card => card.Actions)
            .DistinctBy(action => action.RecommendationKey)
            .OrderByDescending(action => action.IsProposal)
            .ThenByDescending(action => action.Urgency)
            .ThenBy(action => action.ProjectTitle, StringComparer.OrdinalIgnoreCase)
            .ThenBy(action => action.Title, StringComparer.OrdinalIgnoreCase)
            .Take(cards.Any(card => card.Actions.Any(action => action.IsProposal))
                ? 10
                : 6)
            .ToArray();

        return dashboard with
        {
            ProjectCards = cards,
            GlobalNextActions = globalActions
        };
    }

    public Task SnoozeAsync(
        ProjectAction action,
        DateTimeOffset snoozedUntil,
        CancellationToken cancellationToken = default) =>
        UpsertAsync(
            new ProjectActionDisposition(
                action.RecommendationKey,
                ProjectActionDispositionKind.Snoozed,
                snoozedUntil,
                DateTimeOffset.UtcNow),
            cancellationToken);

    public Task DismissAsync(
        ProjectAction action,
        CancellationToken cancellationToken = default) =>
        UpsertAsync(
            new ProjectActionDisposition(
                action.RecommendationKey,
                ProjectActionDispositionKind.Dismissed,
                null,
                DateTimeOffset.UtcNow),
            cancellationToken);

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            await settingsService.SaveSettingAsync(SettingsKey, "[]", cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task UpsertAsync(
        ProjectActionDisposition disposition,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var dispositions = await ReadAsync(cancellationToken);
            dispositions.RemoveAll(existing =>
                existing.RecommendationKey.Equals(
                    disposition.RecommendationKey,
                    StringComparison.Ordinal));
            dispositions.Add(disposition);
            await SaveAsync(dispositions, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<IReadOnlyList<ProjectActionDisposition>> LoadAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var dispositions = await ReadAsync(cancellationToken);
            var active = dispositions
                .Where(disposition => disposition.IsActive(now))
                .ToArray();
            if (active.Length != dispositions.Count)
            {
                await SaveAsync(active, cancellationToken);
            }

            return active;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<List<ProjectActionDisposition>> ReadAsync(
        CancellationToken cancellationToken)
    {
        var json = await settingsService.GetSettingAsync(
            SettingsKey,
            "[]",
            cancellationToken);
        try
        {
            return JsonSerializer.Deserialize<List<ProjectActionDisposition>>(
                    json ?? "[]",
                    JsonOptions) ??
                [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private Task SaveAsync(
        IEnumerable<ProjectActionDisposition> dispositions,
        CancellationToken cancellationToken) =>
        settingsService.SaveSettingAsync(
            SettingsKey,
            JsonSerializer.Serialize(dispositions, JsonOptions),
            cancellationToken);
}
