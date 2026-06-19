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
        ProjectContextSnapshot projectSnapshot;
        try
        {
            projectSnapshot = await projectContextService.GetSnapshotAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            projectSnapshot = new ProjectContextSnapshot(
                [],
                DateTimeOffset.UtcNow,
                "Project status is unavailable.");
        }

        return await BuildAsync(projectSnapshot, cancellationToken);
    }

    public async Task<CoherentDashboard> BuildAsync(
        ProjectContextSnapshot projectSnapshot,
        CancellationToken cancellationToken = default)
    {
        var graph = await graphService.GetGraphAsync(cancellationToken);
        var snapshot = await graphService.GetDashboardAsync(cancellationToken);

        var projects = snapshot.ActiveProjects;
        var projectCards = new List<ProjectDashboardCard>();
        var globalBlockers = new List<string>();

        // blocked_by is directional: the source item is blocked by the target item.
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
            if (node.Type == "Task" &&
                !node.Status.Equals("Completed", StringComparison.OrdinalIgnoreCase) &&
                !node.Status.Equals("Archived", StringComparison.OrdinalIgnoreCase))
            {
                // Project membership is canonical task -> project via belongs_to.
                foreach (var edge in graph.Edges.Where(e =>
                    e.SourceNodeId == node.Id &&
                    projectIds.Contains(e.TargetNodeId) &&
                    e.RelationshipType.Equals("belongs_to", StringComparison.OrdinalIgnoreCase)))
                {
                    var projectId = edge.TargetNodeId;
                    if (!tasksByProject.ContainsKey(projectId))
                        tasksByProject[projectId] = new List<Node>();
                    if (tasksByProject[projectId].All(task => task.Id != node.Id))
                        tasksByProject[projectId].Add(node);
                }
            }

            if (node.Type == "Decision")
            {
                foreach (var edge in graph.Edges.Where(e =>
                    e.SourceNodeId == node.Id &&
                    projectIds.Contains(e.TargetNodeId) &&
                    e.RelationshipType.Equals("belongs_to", StringComparison.OrdinalIgnoreCase)))
                {
                    var projectId = edge.TargetNodeId;
                    if (!decisionsByProject.ContainsKey(projectId))
                        decisionsByProject[projectId] = new List<Node>();
                    if (decisionsByProject[projectId].All(decision => decision.Id != node.Id))
                        decisionsByProject[projectId].Add(node);
                }
            }
        }

        var contexts = projectSnapshot.Projects;

        foreach (var project in projects)
        {
            tasksByProject.TryGetValue(project.Id, out var tasks);
            decisionsByProject.TryGetValue(project.Id, out var decisions);

            var taskCount = tasks?.Count ?? 0;
            var decisionCount = decisions?.Count ?? 0;

            // Blocker detection
            var blockerDescriptions = new List<string>();
            var blockerTargets = new List<Guid>();
            var blockerSubjects = new List<Guid>();
            var blockerEdges = new List<Guid>();
            blockedBy.TryGetValue(project.Id, out var projectBlockers);
            if (projectBlockers is { Count: > 0 })
            {
                foreach (var edge in projectBlockers)
                {
                    var blocker = graph.Nodes.FirstOrDefault(n => n.Id == edge.TargetNodeId);
                    if (blocker is not null)
                    {
                        blockerDescriptions.Add($"{blocker.Title} blocks {project.Title}");
                        blockerTargets.Add(blocker.Id);
                        blockerSubjects.Add(project.Id);
                        blockerEdges.Add(edge.Id);
                    }
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
                            {
                                blockerDescriptions.Add($"{blocker.Title} blocks task '{task.Title}'");
                                blockerTargets.Add(blocker.Id);
                                blockerSubjects.Add(task.Id);
                                blockerEdges.Add(edge.Id);
                            }
                        }
                    }
                }
            }

            // Repo health
            string? repoHealthLabel = null;
            string? repoHealthDetail = null;
            var hasRepoWarning = false;

            var context = contexts.FirstOrDefault(c =>
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

            var actions = new List<ProjectAction>();
            if (taskCount == 0)
            {
                actions.Add(new ProjectAction(
                    project.Id,
                    project.Title,
                    ProjectActionCategory.Planning,
                    ProjectActionUrgency.High,
                    "Create the next task",
                    "This project has no open tasks. Define one concrete next step.",
                    ProjectActionCommand.CreateTask,
                    RecommendationCode: "missing-open-task"));
            }

            if (decisionCount == 0)
            {
                actions.Add(new ProjectAction(
                    project.Id,
                    project.Title,
                    ProjectActionCategory.Navigation,
                    ProjectActionUrgency.Low,
                    "Review project decisions",
                    "No decisions are connected to this project yet.",
                    ProjectActionCommand.OpenProject,
                    ProjectPath: context?.Path,
                    RecommendationCode: "missing-decision"));
            }

            if (blockerDescriptions.Count > 0)
            {
                actions.Add(new ProjectAction(
                    project.Id,
                    project.Title,
                    ProjectActionCategory.Blocker,
                    ProjectActionUrgency.Critical,
                    "Resolve the active blocker",
                    $"{blockerDescriptions[0]}. Removing the relationship will not complete or delete either item.",
                    ProjectActionCommand.ResolveBlocker,
                    TargetNodeId: blockerTargets.Count > 0
                        ? blockerTargets[0]
                        : null,
                    SubjectNodeId: blockerSubjects.Count > 0
                        ? blockerSubjects[0]
                        : null,
                    TargetEdgeId: blockerEdges.Count > 0
                        ? blockerEdges[0]
                        : null,
                    RecommendationCode: "active-blocker"));
            }

            if (context is { HasUncommittedChanges: true })
            {
                actions.Add(new ProjectAction(
                    project.Id,
                    project.Title,
                    ProjectActionCategory.SourceControl,
                    ProjectActionUrgency.High,
                    "Review Git state",
                    "The working tree has local changes that may need review or a commit.",
                    ProjectActionCommand.ReviewGitState,
                    ProjectPath: context.Path,
                    RecommendationCode: "uncommitted-changes"));
            }

            if (context is { ReadmePath: null })
            {
                actions.Add(new ProjectAction(
                    project.Id,
                    project.Title,
                    ProjectActionCategory.Maintenance,
                    ProjectActionUrgency.Normal,
                    "Open project and add a README",
                    "No README was found in the project root.",
                    ProjectActionCommand.OpenProject,
                    ProjectPath: context.Path,
                    RecommendationCode: "missing-readme"));
            }

            if (string.IsNullOrWhiteSpace(project.Summary) || project.Summary.Length < 30)
            {
                actions.Add(new ProjectAction(
                    project.Id,
                    project.Title,
                    ProjectActionCategory.Maintenance,
                    ProjectActionUrgency.Low,
                    "Review project summary",
                    "The project summary is missing or too short to explain its current state.",
                    ProjectActionCommand.OpenProject,
                    ProjectPath: context?.Path,
                    RecommendationCode: "missing-summary"));
            }

            if (actions.Count == 0)
            {
                actions.Add(new ProjectAction(
                    project.Id,
                    project.Title,
                    ProjectActionCategory.Maintenance,
                    ProjectActionUrgency.Low,
                    "Refresh project status",
                    "The project looks healthy. Refresh when local state changes.",
                    ProjectActionCommand.RefreshProjectStatus,
                    ProjectPath: context?.Path,
                    RecommendationCode: "healthy-refresh"));
            }

            // Collect global alerts
            if (blockerDescriptions.Count > 0)
                globalBlockers.AddRange(blockerDescriptions);

            projectCards.Add(new ProjectDashboardCard(
                project,
                taskCount,
                decisionCount,
                blockerDescriptions.Count,
                blockerDescriptions,
                actions
                    .OrderByDescending(action => action.Urgency)
                    .ThenBy(action => action.Title, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                repoHealthLabel,
                repoHealthDetail,
                hasRepoWarning));
        }

        var globalNextActions = projectCards
            .SelectMany(card => card.Actions)
            .OrderByDescending(action => action.Urgency)
            .ThenBy(action => action.ProjectTitle, StringComparer.OrdinalIgnoreCase)
            .ThenBy(action => action.Title, StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToArray();

        return new CoherentDashboard(
            projectCards,
            globalBlockers,
            globalNextActions,
            projectCards.Sum(c => c.OpenTaskCount),
            globalBlockers.Count);
    }

}
