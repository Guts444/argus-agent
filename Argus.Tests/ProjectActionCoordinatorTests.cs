using Argus.AI.Services;
using Argus.Core.Graph;
using Argus.Core.Models;
using Argus.Core.Services;

namespace Argus.Tests;

public sealed class ProjectActionCoordinatorTests
{
    [Fact]
    public async Task CreateTaskRequiresReviewAndUsesAuditedTools()
    {
        var project = Project();
        var graph = new StubGraphService(new GraphSnapshot([project], []));
        var tools = new StubToolService();
        var review = new StubReviewService(
            new ProjectActionReviewDecision(true, "Implement action review"));
        var coordinator = CreateCoordinator(graph, tools, review);
        var action = Action(
            project,
            ProjectActionCommand.CreateTask,
            ProjectActionSource.RuleBased);

        var result = await coordinator.ExecuteAsync(action);

        Assert.True(result.Succeeded);
        Assert.Equal(ProjectActionCompletion.GraphChanged, result.Completion);
        Assert.Equal(1, review.RequestCount);
        Assert.Collection(
            tools.Requests,
            request =>
            {
                Assert.Equal("CreateNode", request.ToolName);
                Assert.Equal("approved", request.ApprovalStatus);
                Assert.Contains("Implement action review", request.ArgumentsJson);
            },
            request =>
            {
                Assert.Equal("CreateEdge", request.ToolName);
                Assert.Equal("approved", request.ApprovalStatus);
                Assert.Contains("\"relationshipType\":\"belongs_to\"", request.ArgumentsJson);
            });
    }

    [Fact]
    public async Task DeclinedMutationDoesNotExecuteTools()
    {
        var project = Project();
        var tools = new StubToolService();
        var coordinator = CreateCoordinator(
            new StubGraphService(new GraphSnapshot([project], [])),
            tools,
            new StubReviewService(new ProjectActionReviewDecision(false)));

        var result = await coordinator.ExecuteAsync(
            Action(project, ProjectActionCommand.CreateTask));

        Assert.True(result.Cancelled);
        Assert.Empty(tools.Requests);
    }

    [Fact]
    public async Task CreateTaskRollsBackNodeWhenProjectLinkFails()
    {
        var project = Project();
        var tools = new StubToolService { FailToolName = "CreateEdge" };
        var coordinator = CreateCoordinator(
            new StubGraphService(new GraphSnapshot([project], [])),
            tools,
            new StubReviewService(
                new ProjectActionReviewDecision(true, "Implement review")));

        var result = await coordinator.ExecuteAsync(
            Action(project, ProjectActionCommand.CreateTask));

        Assert.False(result.Succeeded);
        Assert.Equal(
            ["CreateNode", "CreateEdge", "DeleteNode"],
            tools.Requests.Select(request => request.ToolName));
    }

    [Fact]
    public async Task ResolveBlockerDeletesOnlyTheValidatedRelationship()
    {
        var project = Project();
        var task = new Node
        {
            Title = "Implement workflow",
            Type = "Task",
            Status = "Active"
        };
        var blocker = new Node
        {
            Title = "Decide review semantics",
            Type = "Decision",
            Status = "Active"
        };
        var membership = Edge(task, project, "belongs_to");
        var blockedBy = Edge(task, blocker, "blocked_by");
        var tools = new StubToolService();
        var coordinator = CreateCoordinator(
            new StubGraphService(
                new GraphSnapshot(
                    [project, task, blocker],
                    [membership, blockedBy])),
            tools,
            new StubReviewService(new ProjectActionReviewDecision(true)));
        var action = Action(project, ProjectActionCommand.ResolveBlocker) with
        {
            SubjectNodeId = task.Id,
            TargetNodeId = blocker.Id,
            TargetEdgeId = blockedBy.Id
        };

        var result = await coordinator.ExecuteAsync(action);

        Assert.True(result.Succeeded);
        var request = Assert.Single(tools.Requests);
        Assert.Equal("DeleteEdge", request.ToolName);
        Assert.Contains(blockedBy.Id.ToString(), request.ArgumentsJson);
        Assert.DoesNotContain("DeleteNode", tools.Requests.Select(item => item.ToolName));
    }

    [Fact]
    public async Task LlmNavigationProposalAlsoRequiresReview()
    {
        var project = Project();
        var review = new StubReviewService(new ProjectActionReviewDecision(false));
        var coordinator = CreateCoordinator(
            new StubGraphService(new GraphSnapshot([project], [])),
            new StubToolService(),
            review);
        var action = Action(
            project,
            ProjectActionCommand.OpenProject,
            ProjectActionSource.LlmProposal);

        var result = await coordinator.ExecuteAsync(action);

        Assert.True(action.IsProposal);
        Assert.True(action.RequiresReview);
        Assert.True(result.Cancelled);
        Assert.Equal(1, review.RequestCount);
    }

    [Fact]
    public void RecommendationIdentityDoesNotContainDisplayTextOrLocalPath()
    {
        var project = Project();
        var first = Action(project, ProjectActionCommand.OpenProject) with
        {
            Title = "Private title",
            Explanation = "Private explanation",
            ProjectPath = @"D:\Private\Project",
            RecommendationCode = "missing-readme"
        };
        var second = first with
        {
            Title = "Changed display text",
            Explanation = "Changed private detail",
            ProjectPath = @"C:\Different\Path"
        };

        Assert.Equal(first.RecommendationKey, second.RecommendationKey);
        Assert.DoesNotContain("Private", first.RecommendationKey);
    }

    private static ProjectActionCoordinator CreateCoordinator(
        IGraphService graph,
        IToolService tools,
        IProjectActionReviewService review) =>
        new(
            graph,
            tools,
            review,
            new StubDispositionService(),
            new StubDiagnosticLog());

    private static Node Project() =>
        new()
        {
            Title = "Argus",
            Type = "Project",
            Status = "Active"
        };

    private static ProjectAction Action(
        Node project,
        ProjectActionCommand command,
        ProjectActionSource source = ProjectActionSource.RuleBased) =>
        new(
            project.Id,
            project.Title,
            ProjectActionCategory.Planning,
            ProjectActionUrgency.High,
            "Recommended action",
            "Useful explanation",
            command,
            Source: source,
            RecommendationCode: "test");

    private static Edge Edge(Node source, Node target, string relationship) =>
        new()
        {
            SourceNodeId = source.Id,
            TargetNodeId = target.Id,
            RelationshipType = relationship
        };

    private sealed class StubReviewService(
        ProjectActionReviewDecision decision) : IProjectActionReviewService
    {
        public int RequestCount { get; private set; }

        public Task<ProjectActionReviewDecision> RequestReviewAsync(
            ProjectAction action,
            CancellationToken cancellationToken = default)
        {
            RequestCount++;
            return Task.FromResult(decision);
        }
    }

    private sealed class StubToolService : IToolService
    {
        private readonly Guid createdNodeId = Guid.NewGuid();
        public List<ToolExecutionRequest> Requests { get; } = [];
        public string? FailToolName { get; init; }

        public Task<ToolExecutionResult> ExecuteToolAsync(
            ToolExecutionRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            if (request.ToolName.Equals(
                    FailToolName,
                    StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new ToolExecutionResult(
                    request.ExecutionId ?? Guid.NewGuid(),
                    false,
                    false,
                    """{"error":"simulated failure"}""",
                    "simulated failure",
                    1));
            }

            var json = request.ToolName == "CreateNode"
                ? $"{{\"success\":true,\"node\":{{\"Id\":\"{createdNodeId}\"}}}}"
                : """{"success":true}""";
            return Task.FromResult(new ToolExecutionResult(
                request.ExecutionId ?? Guid.NewGuid(),
                true,
                false,
                json,
                null,
                1));
        }

        public Task<IReadOnlyList<string>> ListToolsAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public ToolDefinition? GetToolDefinition(string toolName) => null;

        public ToolArgumentValidationResult ValidateArguments(
            string toolName,
            string argumentsJson) =>
            new(true, argumentsJson, []);

        public Task<string> ExecuteToolAsync(
            string toolName,
            string argumentsJson,
            CancellationToken cancellationToken = default) =>
            Task.FromResult("{}");
    }

    private sealed class StubGraphService(GraphSnapshot graph) : IGraphService
    {
        public Task<GraphSnapshot> GetGraphAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(graph);

        public Task<DashboardSnapshot> GetDashboardAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Node>> SearchNodesAsync(
            string query,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Node> CreateNodeAsync(
            Node node,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Node> UpdateNodeAsync(
            Node node,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteNodeAsync(
            Guid nodeId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Edge> CreateEdgeAsync(
            Guid sourceNodeId,
            Guid targetNodeId,
            string relationshipType,
            double strength,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteEdgeAsync(
            Guid edgeId,
            CancellationToken cancellationToken = default) =>
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

    private sealed class StubDispositionService : IProjectActionDispositionService
    {
        public Task<CoherentDashboard> ApplyAsync(
            CoherentDashboard dashboard,
            DateTimeOffset? now = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(dashboard);

        public Task SnoozeAsync(
            ProjectAction action,
            DateTimeOffset snoozedUntil,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task DismissAsync(
            ProjectAction action,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task ClearAsync(
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class StubDiagnosticLog : IDiagnosticLog
    {
        public string DiagnosticsDirectory => string.Empty;

        public IDisposable BeginOperation(string component, string operation) =>
            new NoopDisposable();

        public void Write(
            DiagnosticSeverity severity,
            string component,
            string eventName,
            string? detail = null,
            Exception? exception = null)
        {
        }

        private sealed class NoopDisposable : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}
