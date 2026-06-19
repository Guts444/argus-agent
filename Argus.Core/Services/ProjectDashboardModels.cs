using System.Security.Cryptography;
using System.Text;
using Argus.Core.Graph;
using Argus.Core.Models;

namespace Argus.Core.Services;

public enum ProjectActionCategory
{
    Planning,
    Navigation,
    SourceControl,
    Blocker,
    Maintenance
}

public enum ProjectActionUrgency
{
    Low,
    Normal,
    High,
    Critical
}

public enum ProjectActionCommand
{
    CreateTask,
    OpenProject,
    ReviewGitState,
    OpenBlocker,
    ResolveBlocker,
    SnoozeRecommendation,
    DismissRecommendation,
    RefreshProjectStatus
}

public enum ProjectActionSource
{
    RuleBased,
    LlmProposal
}

public sealed record ProjectAction(
    Guid ProjectId,
    string ProjectTitle,
    ProjectActionCategory Category,
    ProjectActionUrgency Urgency,
    string Title,
    string Explanation,
    ProjectActionCommand Command,
    Guid? TargetNodeId = null,
    Guid? SubjectNodeId = null,
    Guid? TargetEdgeId = null,
    string? ProjectPath = null,
    ProjectActionSource Source = ProjectActionSource.RuleBased,
    string RecommendationCode = "")
{
    public bool MutatesGraph =>
        Command is ProjectActionCommand.CreateTask or
            ProjectActionCommand.ResolveBlocker;
    public bool RequiresReview => MutatesGraph || Source == ProjectActionSource.LlmProposal;
    public bool IsProposal => Source == ProjectActionSource.LlmProposal;
    public string CategoryText => Category.ToString();
    public string UrgencyText => Urgency.ToString().ToUpperInvariant();
    public string SourceLabel => IsProposal ? "PROPOSAL" : "RULE";
    public string RecommendationKey => BuildRecommendationKey();
    public string CommandLabel => Command switch
    {
        ProjectActionCommand.CreateTask => "Create task",
        ProjectActionCommand.OpenProject => "Open project",
        ProjectActionCommand.ReviewGitState => "Review Git",
        ProjectActionCommand.OpenBlocker => "Open blocker",
        ProjectActionCommand.ResolveBlocker => "Resolve blocker",
        ProjectActionCommand.SnoozeRecommendation => "Snooze",
        ProjectActionCommand.DismissRecommendation => "Dismiss",
        ProjectActionCommand.RefreshProjectStatus => "Refresh",
        _ => "Open"
    };

    private string BuildRecommendationKey()
    {
        var identity = string.Join(
            "|",
            ProjectId.ToString("N"),
            Command,
            Category,
            Source,
            SubjectNodeId?.ToString("N") ?? string.Empty,
            TargetNodeId?.ToString("N") ?? string.Empty,
            TargetEdgeId?.ToString("N") ?? string.Empty,
            RecommendationCode);
        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }
}

public enum ProjectActionDispositionKind
{
    Snoozed,
    Dismissed
}

public sealed record ProjectActionDisposition(
    string RecommendationKey,
    ProjectActionDispositionKind Kind,
    DateTimeOffset? SnoozedUntil,
    DateTimeOffset UpdatedAt)
{
    public bool IsActive(DateTimeOffset now) =>
        Kind == ProjectActionDispositionKind.Dismissed ||
        SnoozedUntil is null ||
        SnoozedUntil > now;
}

public sealed record ProjectActionReviewDecision(
    bool Approved,
    string? TaskTitle = null,
    string? Reason = null);

public enum ProjectActionCompletion
{
    None,
    GraphChanged,
    FocusProject,
    ReviewGitState,
    FocusNode,
    RefreshDashboard
}

public sealed record ProjectActionExecutionResult(
    bool Succeeded,
    bool Cancelled,
    ProjectActionCompletion Completion,
    string Message,
    Guid? CreatedNodeId = null,
    Guid? FocusNodeId = null,
    string? ProjectPath = null);

public sealed record ProjectActionProposalGenerationResult(
    CoherentDashboard Dashboard,
    IReadOnlyList<ProjectAction> Proposals,
    string? Error = null,
    bool SetupRequired = false)
{
    public bool Succeeded => Error is null && !SetupRequired;
}

public sealed record ProjectDashboardCard(
    Node ProjectNode,
    int OpenTaskCount,
    int DecisionCount,
    int BlockerCount,
    IReadOnlyList<string> BlockerDescriptions,
    IReadOnlyList<ProjectAction> Actions,
    string? RepoHealthLabel,
    string? RepoHealthDetail,
    bool HasRepoWarning)
{
    public string OpenTaskCountText => $"{OpenTaskCount} tasks";
    public string DecisionCountText => $"{DecisionCount} decisions";
    public string BlockerCountText => $"{BlockerCount} blocked";
    public string FirstNextAction => Actions.Count > 0 ? Actions[0].Title : "";
    public ProjectAction? FirstAction => Actions.FirstOrDefault();
    public bool HasActions => Actions.Count > 0;
    public string RepoHealthDisplay => RepoHealthLabel ?? "";
    public bool HasRepoHealth => !string.IsNullOrWhiteSpace(RepoHealthLabel);
    public bool HasBlockers => BlockerCount > 0;
}

/// <summary>
/// Aggregated project intelligence for the dashboard.
/// </summary>
public sealed record CoherentDashboard(
    IReadOnlyList<ProjectDashboardCard> ProjectCards,
    IReadOnlyList<string> GlobalBlockers,
    IReadOnlyList<ProjectAction> GlobalNextActions,
    int TotalOpenTasks,
    int TotalBlockers)
{
    public string TotalOpenTasksText => $"{TotalOpenTasks} tasks";
    public string TotalBlockersText => $"{TotalBlockers} blocked";
    public string GlobalNextActionCountText =>
        GlobalNextActions.Count == 1 ? "1 priority" : $"{GlobalNextActions.Count} priorities";
    public bool HasGlobalBlockers => GlobalBlockers.Count > 0;
    public bool HasGlobalNextActions => GlobalNextActions.Count > 0;
}

/// <summary>
/// Builds a coherent project dashboard that combines graph data,
/// local project context, and optional LLM enrichment.
/// </summary>
public interface IProjectDashboardService
{
    /// <summary>
    /// Build the dashboard from graph + project context data.
    /// Fast — no LLM calls.
    /// </summary>
    Task<CoherentDashboard> BuildAsync(CancellationToken cancellationToken = default);
    Task<CoherentDashboard> BuildAsync(
        ProjectContextSnapshot projectSnapshot,
        CancellationToken cancellationToken = default);
}

public interface IProjectActionProposalService
{
    Task<ProjectActionProposalGenerationResult> GenerateAsync(
        CoherentDashboard dashboard,
        AiProviderProfile? provider,
        string soulText,
        IReadOnlyList<ProjectContext> projectContexts,
        CancellationToken cancellationToken = default);
    CoherentDashboard Merge(
        CoherentDashboard dashboard,
        IReadOnlyList<ProjectAction> proposals);
}

public interface IProjectActionDispositionService
{
    Task<CoherentDashboard> ApplyAsync(
        CoherentDashboard dashboard,
        DateTimeOffset? now = null,
        CancellationToken cancellationToken = default);
    Task SnoozeAsync(
        ProjectAction action,
        DateTimeOffset snoozedUntil,
        CancellationToken cancellationToken = default);
    Task DismissAsync(
        ProjectAction action,
        CancellationToken cancellationToken = default);
    Task ClearAsync(CancellationToken cancellationToken = default);
}

public interface IProjectActionReviewService
{
    Task<ProjectActionReviewDecision> RequestReviewAsync(
        ProjectAction action,
        CancellationToken cancellationToken = default);
}

public interface IProjectActionCoordinator
{
    Task<ProjectActionExecutionResult> ExecuteAsync(
        ProjectAction action,
        CancellationToken cancellationToken = default);
    Task<CoherentDashboard> ApplyDispositionsAsync(
        CoherentDashboard dashboard,
        CancellationToken cancellationToken = default);
    Task SnoozeAsync(
        ProjectAction action,
        TimeSpan duration,
        CancellationToken cancellationToken = default);
    Task DismissAsync(
        ProjectAction action,
        CancellationToken cancellationToken = default);
    Task RestoreRecommendationsAsync(
        CancellationToken cancellationToken = default);
}
