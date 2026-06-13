using Argus.Core.Graph;
using Argus.Core.Models;

namespace Argus.Core.Services;

/// <summary>
/// Per-project dashboard card with repo health, task/decision counts,
/// blocker flags, and suggested next actions.
/// </summary>
public sealed record ProjectDashboardCard(
    Node ProjectNode,
    int OpenTaskCount,
    int DecisionCount,
    int BlockerCount,
    IReadOnlyList<string> BlockerDescriptions,
    IReadOnlyList<string> NextActions,
    string? RepoHealthLabel,
    string? RepoHealthDetail,
    bool HasRepoWarning)
{
    public string OpenTaskCountText => $"{OpenTaskCount} tasks";
    public string DecisionCountText => $"{DecisionCount} decisions";
    public string BlockerCountText => $"{BlockerCount} blocked";
    public string FirstNextAction => NextActions.Count > 0 ? NextActions[0] : "";
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
    IReadOnlyList<string> GlobalNextActions,
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

    /// <summary>
    /// Enrich the dashboard with LLM-extracted next actions and blocker analysis.
    /// Uses the selected provider and soul. Returns updated dashboard.
    /// </summary>
    Task<CoherentDashboard> EnrichWithLLMAsync(
        CoherentDashboard dashboard,
        AiProviderProfile? provider,
        string soulText,
        IReadOnlyList<ProjectContext> projectContexts,
        CancellationToken cancellationToken = default);
}
