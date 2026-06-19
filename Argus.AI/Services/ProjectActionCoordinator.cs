using System.Text.Json;
using Argus.Core.Models;
using Argus.Core.Services;

namespace Argus.AI.Services;

public sealed class ProjectActionCoordinator(
    IGraphService graphService,
    IToolService toolService,
    IProjectActionReviewService reviewService,
    IProjectActionDispositionService dispositionService,
    IDiagnosticLog diagnosticLog) : IProjectActionCoordinator
{
    private readonly SemaphoreSlim executionGate = new(1, 1);

    public async Task<ProjectActionExecutionResult> ExecuteAsync(
        ProjectAction action,
        CancellationToken cancellationToken = default)
    {
        if (!await executionGate.WaitAsync(0, cancellationToken))
        {
            return Failure("Another project action is already running.");
        }

        using var operation = diagnosticLog.BeginOperation(
            "project_action",
            action.Command.ToString().ToLowerInvariant());
        try
        {
            ProjectActionReviewDecision review = new(true);
            if (action.RequiresReview)
            {
                review = await reviewService.RequestReviewAsync(action, cancellationToken);
                if (!review.Approved)
                {
                    diagnosticLog.Write(
                        DiagnosticSeverity.Information,
                        "project_action",
                        "review.cancelled",
                        $"command={action.Command} source={action.Source}");
                    return new ProjectActionExecutionResult(
                        Succeeded: false,
                        Cancelled: true,
                        ProjectActionCompletion.None,
                        review.Reason ?? "Project action cancelled.");
                }
            }

            var result = action.Command switch
            {
                ProjectActionCommand.CreateTask =>
                    await CreateTaskAsync(action, review, cancellationToken),
                ProjectActionCommand.OpenProject =>
                    Navigation(
                        ProjectActionCompletion.FocusProject,
                        $"Focused {action.ProjectTitle}.",
                        action.ProjectId,
                        action.ProjectPath),
                ProjectActionCommand.ReviewGitState =>
                    Navigation(
                        ProjectActionCompletion.ReviewGitState,
                        $"Opened {action.ProjectTitle} for Git review.",
                        action.ProjectId,
                        action.ProjectPath),
                ProjectActionCommand.OpenBlocker =>
                    Navigation(
                        ProjectActionCompletion.FocusNode,
                        $"Opened blocker for {action.ProjectTitle}.",
                        action.TargetNodeId ?? action.ProjectId),
                ProjectActionCommand.ResolveBlocker =>
                    await ResolveBlockerAsync(action, cancellationToken),
                ProjectActionCommand.RefreshProjectStatus =>
                    new ProjectActionExecutionResult(
                        true,
                        false,
                        ProjectActionCompletion.RefreshDashboard,
                        "Refreshing project status."),
                _ => Failure("This project action is not executable.")
            };

            diagnosticLog.Write(
                result.Succeeded
                    ? DiagnosticSeverity.Information
                    : DiagnosticSeverity.Warning,
                "project_action",
                result.Succeeded ? "execution.succeeded" : "execution.failed",
                $"command={action.Command} completion={result.Completion}");
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            diagnosticLog.Write(
                DiagnosticSeverity.Information,
                "project_action",
                "execution.cancelled",
                $"command={action.Command}");
            throw;
        }
        catch (Exception ex)
        {
            diagnosticLog.Write(
                DiagnosticSeverity.Error,
                "project_action",
                "execution.failed",
                $"command={action.Command}",
                ex);
            return Failure(
                "The project action failed. Open diagnostics for local details.");
        }
        finally
        {
            executionGate.Release();
        }
    }

    public Task<CoherentDashboard> ApplyDispositionsAsync(
        CoherentDashboard dashboard,
        CancellationToken cancellationToken = default) =>
        dispositionService.ApplyAsync(
            dashboard,
            cancellationToken: cancellationToken);

    public Task SnoozeAsync(
        ProjectAction action,
        TimeSpan duration,
        CancellationToken cancellationToken = default) =>
        dispositionService.SnoozeAsync(
            action,
            DateTimeOffset.UtcNow.Add(duration),
            cancellationToken);

    public Task DismissAsync(
        ProjectAction action,
        CancellationToken cancellationToken = default) =>
        dispositionService.DismissAsync(action, cancellationToken);

    public Task RestoreRecommendationsAsync(
        CancellationToken cancellationToken = default) =>
        dispositionService.ClearAsync(cancellationToken);

    private async Task<ProjectActionExecutionResult> CreateTaskAsync(
        ProjectAction action,
        ProjectActionReviewDecision review,
        CancellationToken cancellationToken)
    {
        var graph = await graphService.GetGraphAsync(cancellationToken);
        var project = graph.Nodes.FirstOrDefault(node =>
            node.Id == action.ProjectId &&
            !node.IsArchived &&
            node.Type.Equals("Project", StringComparison.OrdinalIgnoreCase));
        if (project is null)
        {
            return Failure("The project no longer exists. Refresh and try again.");
        }

        var taskTitle = string.IsNullOrWhiteSpace(review.TaskTitle)
            ? $"Next step for {action.ProjectTitle}"
            : review.TaskTitle.Trim();
        if (taskTitle.Length > 220)
        {
            return Failure("The task title must be 220 characters or fewer.");
        }

        var createNode = await ExecuteApprovedAsync(
            "CreateNode",
            JsonSerializer.Serialize(new
            {
                title = taskTitle,
                type = "Task",
                summary = action.Explanation,
                status = "Active",
                importance = action.Urgency >= ProjectActionUrgency.High ? 4 : 3,
                colorKey = "cyan",
                iconKey = "task"
            }),
            cancellationToken);
        if (!createNode.Succeeded ||
            !TryReadCreatedNodeId(createNode.ResultJson, out var taskId))
        {
            return Failure("The task could not be created. Open diagnostics for details.");
        }

        var createEdge = await ExecuteApprovedAsync(
            "CreateEdge",
            JsonSerializer.Serialize(new
            {
                sourceNodeId = taskId,
                targetNodeId = action.ProjectId,
                relationshipType = "belongs_to",
                strength = 1.0
            }),
            cancellationToken);
        if (!createEdge.Succeeded)
        {
            var rollback = await ExecuteApprovedAsync(
                "DeleteNode",
                JsonSerializer.Serialize(new { nodeId = taskId }),
                CancellationToken.None);
            diagnosticLog.Write(
                rollback.Succeeded
                    ? DiagnosticSeverity.Warning
                    : DiagnosticSeverity.Critical,
                "project_action",
                rollback.Succeeded
                    ? "task_link.rollback_succeeded"
                    : "task_link.rollback_failed");
            return Failure("The task could not be linked to its project.");
        }

        return new ProjectActionExecutionResult(
            true,
            false,
            ProjectActionCompletion.GraphChanged,
            $"Created a task for {action.ProjectTitle}.",
            CreatedNodeId: taskId,
            FocusNodeId: taskId);
    }

    private async Task<ProjectActionExecutionResult> ResolveBlockerAsync(
        ProjectAction action,
        CancellationToken cancellationToken)
    {
        if (action.TargetEdgeId is null ||
            action.SubjectNodeId is null ||
            action.TargetNodeId is null)
        {
            return Failure("The blocker recommendation is stale. Refresh and try again.");
        }

        var graph = await graphService.GetGraphAsync(cancellationToken);
        var edge = graph.Edges.FirstOrDefault(candidate =>
            candidate.Id == action.TargetEdgeId &&
            candidate.SourceNodeId == action.SubjectNodeId &&
            candidate.TargetNodeId == action.TargetNodeId &&
            candidate.RelationshipType.Equals(
                "blocked_by",
                StringComparison.OrdinalIgnoreCase));
        var subjectExists = graph.Nodes.Any(node => node.Id == action.SubjectNodeId);
        var blockerExists = graph.Nodes.Any(node => node.Id == action.TargetNodeId);
        var belongsToProject =
            action.SubjectNodeId == action.ProjectId ||
            graph.Edges.Any(candidate =>
                candidate.SourceNodeId == action.SubjectNodeId &&
                candidate.TargetNodeId == action.ProjectId &&
                candidate.RelationshipType.Equals(
                    "belongs_to",
                    StringComparison.OrdinalIgnoreCase));
        if (edge is null || !subjectExists || !blockerExists || !belongsToProject)
        {
            return Failure("The blocker changed since this recommendation was created. Refresh and try again.");
        }

        var deleteEdge = await ExecuteApprovedAsync(
            "DeleteEdge",
            JsonSerializer.Serialize(new { edgeId = edge.Id }),
            cancellationToken);
        if (!deleteEdge.Succeeded)
        {
            return Failure("The blocker relationship could not be removed.");
        }

        return new ProjectActionExecutionResult(
            true,
            false,
            ProjectActionCompletion.GraphChanged,
            $"Removed the blocker relationship for {action.ProjectTitle}.",
            FocusNodeId: action.SubjectNodeId);
    }

    private Task<ToolExecutionResult> ExecuteApprovedAsync(
        string toolName,
        string argumentsJson,
        CancellationToken cancellationToken) =>
        toolService.ExecuteToolAsync(
            new ToolExecutionRequest(
                toolName,
                argumentsJson,
                ApprovalStatus: "approved",
                ExecutionId: Guid.NewGuid()),
            cancellationToken);

    private static bool TryReadCreatedNodeId(string resultJson, out Guid nodeId)
    {
        nodeId = Guid.Empty;
        try
        {
            using var document = JsonDocument.Parse(resultJson);
            return document.RootElement.TryGetProperty("node", out var node) &&
                node.TryGetProperty("Id", out var id) &&
                id.TryGetGuid(out nodeId);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static ProjectActionExecutionResult Navigation(
        ProjectActionCompletion completion,
        string message,
        Guid focusNodeId,
        string? projectPath = null) =>
        new(
            true,
            false,
            completion,
            message,
            FocusNodeId: focusNodeId,
            ProjectPath: projectPath);

    private static ProjectActionExecutionResult Failure(string message) =>
        new(
            Succeeded: false,
            Cancelled: false,
            ProjectActionCompletion.None,
            message);
}
