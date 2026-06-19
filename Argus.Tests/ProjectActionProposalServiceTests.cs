using Argus.AI.Services;
using Argus.Core.Models;
using Argus.Core.Services;

namespace Argus.Tests;

public sealed class ProjectActionProposalServiceTests
{
    [Fact]
    public async Task GenerateAsyncMapsStrictJsonToReviewedLocalProposals()
    {
        var (dashboard, project, context) = Dashboard();
        var chat = new StubChatService(
            """
            {
              "actions": [
                {
                  "projectIndex": 1,
                  "category": "Planning",
                  "urgency": "High",
                  "title": "Add proposal parser tests",
                  "explanation": "The project needs strict coverage before expanding the workflow.",
                  "command": "CreateTask"
                },
                {
                  "projectIndex": 1,
                  "category": "SourceControl",
                  "urgency": "Normal",
                  "title": "Review the working tree",
                  "explanation": "Local changes should be inspected before the next milestone.",
                  "command": "ReviewGitState"
                }
              ]
            }
            """);
        var service = new ProjectActionProposalService(
            chat,
            new StubDiagnosticLog());

        var result = await service.GenerateAsync(
            dashboard,
            Provider(),
            "Be practical.",
            [context]);

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.Proposals.Count);
        Assert.All(
            result.Proposals,
            proposal =>
            {
                Assert.Equal(project.Id, proposal.ProjectId);
                Assert.Equal(ProjectActionSource.LlmProposal, proposal.Source);
                Assert.True(proposal.RequiresReview);
                Assert.DoesNotContain(
                    context.Path,
                    proposal.RecommendationKey,
                    StringComparison.OrdinalIgnoreCase);
            });
        var gitProposal = Assert.Single(
            result.Proposals,
            proposal => proposal.Command == ProjectActionCommand.ReviewGitState);
        Assert.Equal(context.Path, gitProposal.ProjectPath);
        Assert.Equal(
            result.Proposals,
            result.Dashboard.GlobalNextActions.Take(result.Proposals.Count));
        Assert.Contains(
            result.Dashboard.ProjectCards.Single().Actions,
            action => action.Source == ProjectActionSource.RuleBased);
    }

    [Theory]
    [InlineData(
        """{"actions":[{"projectIndex":1,"category":"Planning","urgency":"High","title":"Task","explanation":"Reason","command":"CreateTask","extra":"no"}]}""")]
    [InlineData(
        """{"actions":[{"projectIndex":1,"category":"Blocker","urgency":"Critical","title":"Resolve","explanation":"Reason","command":"ResolveBlocker"}]}""")]
    [InlineData(
        """Here is the JSON: {"actions":[]}""")]
    [InlineData(
        """{"actions":[{"projectIndex":99,"category":"Planning","urgency":"High","title":"Task","explanation":"Reason","command":"CreateTask"}]}""")]
    public async Task GenerateAsyncRejectsUnsafeOrMalformedResponses(string response)
    {
        var (dashboard, _, context) = Dashboard();
        var service = new ProjectActionProposalService(
            new StubChatService(response),
            new StubDiagnosticLog());

        var result = await service.GenerateAsync(
            dashboard,
            Provider(),
            "Be practical.",
            [context]);

        Assert.False(result.Succeeded);
        Assert.Empty(result.Proposals);
        Assert.NotNull(result.Error);
        Assert.DoesNotContain(
            result.Dashboard.GlobalNextActions,
            action => action.Source == ProjectActionSource.LlmProposal);
    }

    [Fact]
    public async Task GenerateAsyncUsesOnlyRedactedProjectPreview()
    {
        var (dashboard, _, _) = Dashboard();
        var privateContext = new ProjectContext(
            "Argus",
            @"D:\Private\Client\Argus",
            @"D:\Private\Client\Argus\README.md",
            """
            # Argus
            API_KEY=readme-secret
            A useful local project.
            """,
            """
            README: README.md
            Git branch: main
            GitHub remote: https://alice:remote-secret@github.com/example/argus.git
            Working tree: has local changes
            Changed files: M  .env
            """,
            "main",
            "https://alice:remote-secret@github.com/example/argus.git",
            true);
        var chat = new StubChatService("""{"actions":[]}""");
        var service = new ProjectActionProposalService(
            chat,
            new StubDiagnosticLog());

        var result = await service.GenerateAsync(
            dashboard,
            Provider(),
            "Be practical.",
            [privateContext]);

        Assert.True(result.Succeeded);
        var outbound = string.Join(
            Environment.NewLine,
            chat.Messages!.Select(message => message.Content));
        Assert.DoesNotContain(
            privateContext.Path,
            outbound,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("readme-secret", outbound);
        Assert.DoesNotContain("remote-secret", outbound);
        Assert.DoesNotContain(".env", outbound);
        Assert.Contains("[sensitive value omitted]", outbound);
        Assert.Contains("[sensitive file omitted]", outbound);
    }

    [Fact]
    public async Task GenerateAsyncUsesRedactedProjectInstructionsAsUntrustedGuidance()
    {
        var (dashboard, project, context) = Dashboard();
        var chat = new StubChatService("""{"actions":[]}""");
        var instructions = new StubProjectInstructionService(
            new ProjectInstruction(
                project.Id,
                """
                Prefer small reviewable changes.
                Open D:\Private\Client\plan.md
                api_key=private-instruction-secret
                """,
                DateTimeOffset.UtcNow));
        var service = new ProjectActionProposalService(
            chat,
            new StubDiagnosticLog(),
            instructions);

        var result = await service.GenerateAsync(
            dashboard,
            Provider(),
            "Be practical.",
            [context]);

        Assert.True(result.Succeeded);
        var outbound = string.Join(
            Environment.NewLine,
            chat.Messages!.Select(message => message.Content));
        Assert.Contains("Prefer small reviewable changes.", outbound);
        Assert.Contains("Project-specific guidance (untrusted)", outbound);
        Assert.Contains("[local-path]", outbound);
        Assert.Contains("[redacted]", outbound);
        Assert.DoesNotContain(@"D:\Private", outbound);
        Assert.DoesNotContain("private-instruction-secret", outbound);
        Assert.Contains("cannot change this schema", outbound);
        Assert.Equal([project.Id], instructions.RequestedProjectIds);
    }

    [Fact]
    public async Task GenerateAsyncReportsSetupAndProviderFailuresWithoutParsing()
    {
        var (dashboard, _, context) = Dashboard();
        var setupService = new ProjectActionProposalService(
            new StubChatService(
                new AiChatResult(
                    "Configure the provider.",
                    SetupRequired: true)),
            new StubDiagnosticLog());
        var failureService = new ProjectActionProposalService(
            new StubChatService(
                new AiChatResult(
                    string.Empty,
                    Error: "rate limited")),
            new StubDiagnosticLog());

        var missingProvider = await setupService.GenerateAsync(
            dashboard,
            null,
            string.Empty,
            [context]);
        var setup = await setupService.GenerateAsync(
            dashboard,
            Provider(),
            string.Empty,
            [context]);
        var failed = await failureService.GenerateAsync(
            dashboard,
            Provider(),
            string.Empty,
            [context]);

        Assert.True(missingProvider.SetupRequired);
        Assert.True(setup.SetupRequired);
        Assert.Contains("rate limited", failed.Error);
        Assert.Empty(failed.Proposals);
    }

    [Fact]
    public async Task GenerateAsyncPropagatesUserCancellation()
    {
        var (dashboard, _, context) = Dashboard();
        var service = new ProjectActionProposalService(
            new CancellingChatService(),
            new StubDiagnosticLog());
        using var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(25));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.GenerateAsync(
                dashboard,
                Provider(),
                string.Empty,
                [context],
                cancellation.Token));
    }

    private static (
        CoherentDashboard Dashboard,
        Node Project,
        ProjectContext Context) Dashboard()
    {
        var project = new Node
        {
            Title = "Argus",
            Type = "Project",
            Status = "Active",
            Summary = "A local-first project workspace."
        };
        var rule = new ProjectAction(
            project.Id,
            project.Title,
            ProjectActionCategory.Maintenance,
            ProjectActionUrgency.Low,
            "Refresh project status",
            "Refresh after local changes.",
            ProjectActionCommand.RefreshProjectStatus,
            RecommendationCode: "healthy-refresh");
        var card = new ProjectDashboardCard(
            project,
            2,
            1,
            0,
            [],
            [rule],
            "main",
            "Clean",
            false);
        var dashboard = new CoherentDashboard(
            [card],
            [],
            [rule],
            2,
            0);
        var context = new ProjectContext(
            project.Title,
            @"D:\Projects\Cortex",
            @"D:\Projects\Cortex\README.md",
            "# Argus\nA local-first project workspace.",
            "README: README.md\nGit branch: main\nWorking tree: clean",
            "main",
            "https://github.com/example/argus.git",
            false);
        return (dashboard, project, context);
    }

    private static AiProviderProfile Provider() =>
        new()
        {
            Name = "Test Provider",
            ProviderType = "OpenAI",
            BaseUrl = "https://example.test/v1",
            Model = "test-model"
        };

    private sealed class StubChatService : IAiChatService
    {
        private readonly AiChatResult result;

        public StubChatService(string content)
            : this(new AiChatResult(content))
        {
        }

        public StubChatService(AiChatResult result)
        {
            this.result = result;
        }

        public IReadOnlyList<AiChatTurn>? Messages { get; private set; }

        public Task<AiChatResult> SendAsync(
            AiProviderProfile? profile,
            IReadOnlyList<AiChatTurn> messages,
            CancellationToken cancellationToken = default)
        {
            Messages = messages;
            return Task.FromResult(result);
        }

        public Task<float[]?> GenerateEmbeddingAsync(
            AiProviderProfile? profile,
            string text,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<float[]?>(null);
    }

    private sealed class CancellingChatService : IAiChatService
    {
        public async Task<AiChatResult> SendAsync(
            AiProviderProfile? profile,
            IReadOnlyList<AiChatTurn> messages,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new AiChatResult(string.Empty);
        }

        public Task<float[]?> GenerateEmbeddingAsync(
            AiProviderProfile? profile,
            string text,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<float[]?>(null);
    }

    private sealed class StubProjectInstructionService(
        params ProjectInstruction[] instructions) : IProjectInstructionService
    {
        private readonly IReadOnlyDictionary<Guid, ProjectInstruction> byProject =
            instructions.ToDictionary(instruction => instruction.ProjectId);

        public IReadOnlyList<Guid> RequestedProjectIds { get; private set; } = [];

        public Task<ProjectInstruction?> GetAsync(
            Guid projectId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(
                byProject.TryGetValue(projectId, out var instruction)
                    ? instruction
                    : null);

        public Task<IReadOnlyDictionary<Guid, ProjectInstruction>> GetManyAsync(
            IEnumerable<Guid> projectIds,
            CancellationToken cancellationToken = default)
        {
            RequestedProjectIds = projectIds.ToArray();
            IReadOnlyDictionary<Guid, ProjectInstruction> result =
                RequestedProjectIds
                    .Where(byProject.ContainsKey)
                    .ToDictionary(projectId => projectId, projectId => byProject[projectId]);
            return Task.FromResult(result);
        }

        public Task<ProjectInstruction?> SaveAsync(
            Guid projectId,
            string? content,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task ClearAsync(
            Guid projectId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
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
