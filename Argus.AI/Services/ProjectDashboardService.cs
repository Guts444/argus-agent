using Argus.Core.Graph;
using Argus.Core.Models;
using Argus.Core.Services;

namespace Argus.AI.Services;

public sealed class ProjectDashboardService(
    IGraphService graphService,
    IProjectContextService projectContextService) : IProjectDashboardService
{
    public async Task<CoherentDashboard> BuildAsync(CancellationToken cancellationToken = default)
    {
        var graph = await graphService.GetGraphAsync(cancellationToken);
        var snapshot = await graphService.GetDashboardAsync(cancellationToken);

        var projects = snapshot.ActiveProjects;
        var projectCards = new List<ProjectDashboardCard>();
        var globalBlockers = new List<string>();
        var globalNextActions = new List<string>();

        // Build edge lookup: target → blocking sources
        var blockedBy = new Dictionary<Guid, List<Edge>>();
        foreach (var edge in graph.Edges)
        {
            if (edge.RelationshipType.Equals("blocked_by", StringComparison.OrdinalIgnoreCase))
            {
                if (!blockedBy.ContainsKey(edge.SourceNodeId))
                    blockedBy[edge.SourceNodeId] = new List<Edge>();
                blockedBy[edge.SourceNodeId].Add(edge);
            }
        }

        // Count tasks and decisions per project
        var projectIds = projects.Select(p => p.Id).ToHashSet();
        var tasksByProject = new Dictionary<Guid, List<Node>>();
        var decisionsByProject = new Dictionary<Guid, List<Node>>();

        foreach (var node in graph.Nodes.Where(n => !n.IsArchived))
        {
            if (node.Type == "Task" && node.Status != "Done")
            {
                // Find which project this task belongs to via edges
                foreach (var edge in graph.Edges.Where(e =>
                    (e.SourceNodeId == node.Id || e.TargetNodeId == node.Id) &&
                    projectIds.Contains(e.SourceNodeId == node.Id ? e.TargetNodeId : e.SourceNodeId)))
                {
                    var projectId = edge.SourceNodeId == node.Id ? edge.TargetNodeId : edge.SourceNodeId;
                    if (!tasksByProject.ContainsKey(projectId))
                        tasksByProject[projectId] = new List<Node>();
                    tasksByProject[projectId].Add(node);
                }
            }

            if (node.Type == "Decision")
            {
                foreach (var edge in graph.Edges.Where(e =>
                    (e.SourceNodeId == node.Id || e.TargetNodeId == node.Id) &&
                    projectIds.Contains(e.SourceNodeId == node.Id ? e.TargetNodeId : e.SourceNodeId)))
                {
                    var projectId = edge.SourceNodeId == node.Id ? edge.TargetNodeId : edge.SourceNodeId;
                    if (!decisionsByProject.ContainsKey(projectId))
                        decisionsByProject[projectId] = new List<Node>();
                    decisionsByProject[projectId].Add(node);
                }
            }
        }

        // Try to get project contexts for repo health
        IReadOnlyList<ProjectContext>? contexts = null;
        try
        {
            contexts = await projectContextService.ScanProjectsAsync(cancellationToken);
        }
        catch
        {
            // Project scanning may fail if no root path configured
        }

        foreach (var project in projects)
        {
            tasksByProject.TryGetValue(project.Id, out var tasks);
            decisionsByProject.TryGetValue(project.Id, out var decisions);

            var taskCount = tasks?.Count ?? 0;
            var decisionCount = decisions?.Count ?? 0;

            // Blocker detection
            var blockerDescriptions = new List<string>();
            blockedBy.TryGetValue(project.Id, out var projectBlockers);
            if (projectBlockers is { Count: > 0 })
            {
                foreach (var edge in projectBlockers)
                {
                    var blocker = graph.Nodes.FirstOrDefault(n => n.Id == edge.TargetNodeId);
                    if (blocker is not null)
                        blockerDescriptions.Add($"{blocker.Title} blocks {project.Title}");
                }
            }

            // Check tasks for blockers too
            if (tasks is { Count: > 0 })
            {
                foreach (var task in tasks)
                {
                    if (blockedBy.TryGetValue(task.Id, out var taskBlockers))
                    {
                        foreach (var edge in taskBlockers)
                        {
                            var blocker = graph.Nodes.FirstOrDefault(n => n.Id == edge.TargetNodeId);
                            if (blocker is not null)
                                blockerDescriptions.Add($"{blocker.Title} blocks task '{task.Title}'");
                        }
                    }
                }
            }

            // Repo health
            string? repoHealthLabel = null;
            string? repoHealthDetail = null;
            var hasRepoWarning = false;

            var context = contexts?.FirstOrDefault(c =>
                c.Name.Equals(project.Title, StringComparison.OrdinalIgnoreCase));
            if (context is not null)
            {
                var parts = new List<string>();
                if (context.HasUncommittedChanges)
                {
                    parts.Add("Uncommitted");
                    hasRepoWarning = true;
                }

                if (!string.IsNullOrWhiteSpace(context.GitBranch))
                    parts.Add(context.GitBranch);

                // Repo health is derived from uncommitted changes + stale state
                if (parts.Count > 0)
                    repoHealthLabel = string.Join(" · ", parts);
                else
                    repoHealthLabel = "Clean";
                repoHealthDetail = context.StateSummary;
            }

            // Next actions (rule-based)
            var nextActions = new List<string>();
            if (taskCount == 0)
                nextActions.Add("Create a task node for the next concrete step");
            if (decisionCount == 0)
                nextActions.Add("Record pending decisions as Decision nodes");
            if (blockerDescriptions.Count > 0)
                nextActions.Add("Resolve blockers before starting new work");
            if (context is { HasUncommittedChanges: true })
                nextActions.Add("Review and commit local changes");
            if (context is { ReadmePath: null })
                nextActions.Add("Add a README with project status and setup steps");
            if (string.IsNullOrWhiteSpace(project.Summary) || project.Summary.Length < 30)
                nextActions.Add("Update project summary with current state");

            if (nextActions.Count == 0)
                nextActions.Add("Project looks healthy — review open tasks");

            // Collect global alerts
            if (blockerDescriptions.Count > 0)
                globalBlockers.AddRange(blockerDescriptions);

            projectCards.Add(new ProjectDashboardCard(
                project,
                taskCount,
                decisionCount,
                blockerDescriptions.Count,
                blockerDescriptions,
                nextActions,
                repoHealthLabel,
                repoHealthDetail,
                hasRepoWarning));
        }

        // Global next actions: top priorities across all projects
        var projectsWithNoTasks = projectCards.Count(c => c.OpenTaskCount == 0);
        var projectsWithBlockers = projectCards.Count(c => c.BlockerCount > 0);
        var projectsWithWarnings = projectCards.Count(c => c.HasRepoWarning);

        if (projectsWithBlockers > 0)
            globalNextActions.Add($"{projectsWithBlockers} project(s) have active blockers — resolve them first");
        if (projectsWithNoTasks > 0)
            globalNextActions.Add($"{projectsWithNoTasks} project(s) have no open tasks — define next steps");
        if (projectsWithWarnings > 0)
            globalNextActions.Add($"{projectsWithWarnings} project(s) have repo warnings — check Git state");

        if (globalNextActions.Count == 0)
            globalNextActions.Add("All projects look healthy. Start a new initiative or review recent decisions.");

        return new CoherentDashboard(
            projectCards,
            globalBlockers,
            globalNextActions,
            projectCards.Sum(c => c.OpenTaskCount),
            globalBlockers.Count);
    }

    public async Task<CoherentDashboard> EnrichWithLLMAsync(
        CoherentDashboard dashboard,
        AiProviderProfile? provider,
        string soulText,
        IReadOnlyList<ProjectContext> projectContexts,
        CancellationToken cancellationToken = default)
    {
        // LLM enrichment is opt-in and gated on having a configured provider.
        // For v0.3, the rule-based dashboard is the default; LLM enrichment
        // is triggered explicitly by the user via "Summarize Project" or
        // a future "Generate Next Actions" dashboard button.
        return dashboard;
    }
}
