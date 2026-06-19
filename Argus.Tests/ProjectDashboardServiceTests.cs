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
                Link(decision, project, "belongs_to"),
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
            card.Actions,
            action =>
                action.Command == ProjectActionCommand.ResolveBlocker &&
                action.ProjectId == project.Id &&
                action.Urgency == ProjectActionUrgency.Critical &&
                action.SubjectNodeId == task.Id &&
                action.TargetNodeId == blocker.Id &&
                action.TargetEdgeId.HasValue);
        Assert.Contains(
            card.Actions,
            action =>
                action.Command == ProjectActionCommand.ReviewGitState &&
                action.Category == ProjectActionCategory.SourceControl &&
                action.ProjectPath == projectContext.Path);
        Assert.Contains(
            result.GlobalNextActions,
            action => action.Command == ProjectActionCommand.ResolveBlocker);
        Assert.Contains(
            result.GlobalNextActions,
            action => action.Command == ProjectActionCommand.ReviewGitState);
        Assert.All(
            result.GlobalNextActions,
            action =>
            {
                Assert.Equal(project.Id, action.ProjectId);
                Assert.False(string.IsNullOrWhiteSpace(action.Explanation));
            });
        Assert.Equal(
            $"{result.GlobalNextActions.Count} priorities",
            result.GlobalNextActionCountText);
    }

    [Fact]
    public async Task BuildAsyncUsesCanonicalMembershipAndIgnoresCompletedTasks()
    {
        var project = new Node
        {
            Title = "Argus",
            Type = "Project",
            Summary = "A local-first AI workspace for Windows."
        };
        var activeTask = new Node
        {
            Title = "Implement review flow",
            Type = "Task",
            Status = "Active"
        };
        var completedTask = new Node
        {
            Title = "Ship v0.3.3",
            Type = "Task",
            Status = "Completed"
        };
        var unrelatedTask = new Node
        {
            Title = "Referenced but not owned",
            Type = "Task",
            Status = "Active"
        };
        var graph = new GraphSnapshot(
            [project, activeTask, completedTask, unrelatedTask],
            [
                Link(activeTask, project, "belongs_to"),
                Link(activeTask, project, "belongs_to"),
                Link(completedTask, project, "belongs_to"),
                Link(project, unrelatedTask, "related_to")
            ]);
        var dashboard = new DashboardSnapshot(
            [project],
            [],
            [],
            [activeTask, completedTask, unrelatedTask],
            [],
            [],
            []);
        var service = new ProjectDashboardService(
            new StubGraphService(graph, dashboard),
            new StubProjectContextService([]));

        var result = await service.BuildAsync();

        Assert.Equal(1, Assert.Single(result.ProjectCards).OpenTaskCount);
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
        public ProjectContextSnapshot? CurrentSnapshot { get; private set; } =
            new(contexts, DateTimeOffset.UtcNow);

        public bool IsRefreshing => false;

        public Task<ProjectContextSnapshot> GetSnapshotAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CurrentSnapshot!);

        public Task<ProjectContextSnapshot> RefreshSnapshotAsync(
            CancellationToken cancellationToken = default) =>
            GetSnapshotAsync(cancellationToken);

        public void CancelRefresh()
        {
        }

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
