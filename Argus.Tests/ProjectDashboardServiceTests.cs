using Argus.AI.Services;
using Argus.Core.Graph;
using Argus.Core.Models;
using Argus.Core.Services;

namespace Argus.Tests;

public sealed class ProjectDashboardServiceTests
{
    [Fact]
    public async Task BuildAsyncCombinesGraphCountsBlockersAndRepoHealth()
    {
        var project = new Node
        {
            Title = "Argus",
            Type = "Project",
            Summary = "A local-first AI workspace for Windows."
        };
        var task = new Node
        {
            Title = "Ship v0.3.3",
            Type = "Task",
            Status = "Active"
        };
        var decision = new Node
        {
            Title = "Keep release notes out of the repository",
            Type = "Decision"
        };
        var blocker = new Node
        {
            Title = "Fix dashboard layout",
            Type = "Task",
            Status = "Active"
        };
        var graph = new GraphSnapshot(
            [project, task, decision, blocker],
            [
                Link(task, project, "belongs_to"),
                Link(decision, project, "documents"),
                Link(task, blocker, "blocked_by")
            ]);
        var dashboard = new DashboardSnapshot(
            [project],
            [],
            [],
            [task, blocker],
            [],
            [],
            []);
        var projectContext = new ProjectContext(
            "Argus",
            @"D:\Projects\Cortex",
            @"D:\Projects\Cortex\README.md",
            "Argus README",
            "Uncommitted changes on main",
            "main",
            "https://github.com/Guts444/argus-agent.git",
            true);
        var service = new ProjectDashboardService(
            new StubGraphService(graph, dashboard),
            new StubProjectContextService([projectContext]));

        var result = await service.BuildAsync();

        var card = Assert.Single(result.ProjectCards);
        Assert.Equal(1, card.OpenTaskCount);
        Assert.Equal(1, card.DecisionCount);
        Assert.Equal(1, card.BlockerCount);
        Assert.True(card.HasRepoWarning);
        Assert.Equal("Uncommitted \u00B7 main", card.RepoHealthDisplay);
        Assert.Contains(
            card.BlockerDescriptions,
            text => text.Contains("Fix dashboard layout", StringComparison.Ordinal));
        Assert.Contains(
            card.NextActions,
            text => text.Contains("Resolve blockers", StringComparison.Ordinal));
        Assert.Contains(
            card.NextActions,
            text => text.Contains("commit local changes", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            result.GlobalNextActions,
            text => text.Contains("active blockers", StringComparison.Ordinal));
        Assert.Contains(
            result.GlobalNextActions,
            text => text.Contains("repo warnings", StringComparison.Ordinal));
        Assert.Equal("2 priorities", result.GlobalNextActionCountText);
    }

    private static Edge Link(Node source, Node target, string relationshipType)
    {
        return new Edge
        {
            SourceNodeId = source.Id,
            TargetNodeId = target.Id,
            RelationshipType = relationshipType
        };
    }

    private sealed class StubGraphService(
        GraphSnapshot graph,
        DashboardSnapshot dashboard) : IGraphService
    {
        public Task<GraphSnapshot> GetGraphAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(graph);

        public Task<DashboardSnapshot> GetDashboardAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(dashboard);

        public Task<IReadOnlyList<Node>> SearchNodesAsync(
            string query,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Node> CreateNodeAsync(Node node, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Node> UpdateNodeAsync(Node node, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteNodeAsync(Guid nodeId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Edge> CreateEdgeAsync(
            Guid sourceNodeId,
            Guid targetNodeId,
            string relationshipType,
            double strength,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteEdgeAsync(Guid edgeId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task SaveNodePositionAsync(
            Guid nodeId,
            double x,
            double y,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<NodeConnection>> GetConnectionsAsync(
            Guid nodeId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class StubProjectContextService(
        IReadOnlyList<ProjectContext> contexts) : IProjectContextService
    {
        public Task<IReadOnlyList<ProjectContext>> ScanProjectsAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(contexts);

        public Task<ProjectContext?> GetProjectContextAsync(
            string nodeTitle,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(contexts.FirstOrDefault(
                context => context.Name.Equals(nodeTitle, StringComparison.OrdinalIgnoreCase)));
    }
}
