using Argus.Core.Models;
using Argus.Core.Services;
using Argus.Data;
using Argus.Data.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Argus.Tests;

public sealed class ProjectActionDispositionServiceTests
{
    [Fact]
    public async Task DismissedRecommendationPersistsAcrossDatabaseRestart()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "ArgusTests",
            $"{Guid.NewGuid():N}.db");
        var (dashboard, action) = Dashboard();

        using (var provider = CreateProvider(path))
        {
            await provider.GetRequiredService<ArgusDatabaseInitializer>().InitializeAsync();
            var dispositions =
                provider.GetRequiredService<IProjectActionDispositionService>();
            await dispositions.DismissAsync(action);

            Assert.Empty((await dispositions.ApplyAsync(dashboard)).GlobalNextActions);
        }

        using (var provider = CreateProvider(path))
        {
            await provider.GetRequiredService<ArgusDatabaseInitializer>().InitializeAsync();
            var dispositions =
                provider.GetRequiredService<IProjectActionDispositionService>();

            Assert.Empty((await dispositions.ApplyAsync(dashboard)).GlobalNextActions);
            await dispositions.ClearAsync();
            Assert.Single((await dispositions.ApplyAsync(dashboard)).GlobalNextActions);
        }
    }

    [Fact]
    public async Task ExpiredSnoozeDoesNotHideRecommendation()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "ArgusTests",
            $"{Guid.NewGuid():N}.db");
        var (dashboard, action) = Dashboard();
        using var provider = CreateProvider(path);
        await provider.GetRequiredService<ArgusDatabaseInitializer>().InitializeAsync();
        var dispositions =
            provider.GetRequiredService<IProjectActionDispositionService>();

        await dispositions.SnoozeAsync(
            action,
            DateTimeOffset.UtcNow.AddMinutes(-1));

        Assert.Single((await dispositions.ApplyAsync(dashboard)).GlobalNextActions);
    }

    [Fact]
    public async Task FilteringKeepsGeneratedProposalsAheadOfRuleActions()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "ArgusTests",
            $"{Guid.NewGuid():N}.db");
        var (dashboard, hiddenAction) = Dashboard();
        var card = dashboard.ProjectCards.Single();
        var proposals = Enumerable.Range(1, 2)
            .Select(index => new ProjectAction(
                card.ProjectNode.Id,
                card.ProjectNode.Title,
                ProjectActionCategory.Planning,
                ProjectActionUrgency.Low,
                $"Proposal {index}",
                "Generated explanation.",
                ProjectActionCommand.CreateTask,
                Source: ProjectActionSource.LlmProposal,
                RecommendationCode: $"proposal-{index}"))
            .ToArray();
        var withProposals = dashboard with
        {
            ProjectCards =
            [
                card with
                {
                    Actions = card.Actions.Concat(proposals).ToArray()
                }
            ],
            GlobalNextActions = card.Actions.Concat(proposals).ToArray()
        };
        using var provider = CreateProvider(path);
        await provider.GetRequiredService<ArgusDatabaseInitializer>().InitializeAsync();
        var dispositions =
            provider.GetRequiredService<IProjectActionDispositionService>();
        await dispositions.DismissAsync(hiddenAction);

        var filtered = await dispositions.ApplyAsync(withProposals);

        Assert.Equal(proposals, filtered.GlobalNextActions);
    }

    private static ServiceProvider CreateProvider(string path)
    {
        var services = new ServiceCollection();
        services.AddArgusData(path);
        return services.BuildServiceProvider();
    }

    private static (CoherentDashboard Dashboard, ProjectAction Action) Dashboard()
    {
        var project = new Node
        {
            Title = "Argus",
            Type = "Project",
            Status = "Active"
        };
        var action = new ProjectAction(
            project.Id,
            project.Title,
            ProjectActionCategory.Planning,
            ProjectActionUrgency.High,
            "Create the next task",
            "No open task exists.",
            ProjectActionCommand.CreateTask,
            RecommendationCode: "missing-open-task");
        var card = new ProjectDashboardCard(
            project,
            0,
            0,
            0,
            [],
            [action],
            null,
            null,
            false);
        return (
            new CoherentDashboard([card], [], [action], 0, 0),
            action);
    }
}
