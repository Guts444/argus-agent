namespace Argus.Core.Models;

public sealed class ToolExecutionAudit
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ExecutionId { get; set; }
    public Guid? AgentRunId { get; set; }
    public Guid? ConversationId { get; set; }
    public string ToolName { get; set; } = string.Empty;
    public string RiskLevel { get; set; } = string.Empty;
    public string ApprovalStatus { get; set; } = string.Empty;
    public string Outcome { get; set; } = string.Empty;
    public string ArgumentsSummary { get; set; } = string.Empty;
    public string ResultSummary { get; set; } = string.Empty;
    public string? Error { get; set; }
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset CompletedAt { get; set; } = DateTimeOffset.UtcNow;
    public long DurationMilliseconds { get; set; }
}
