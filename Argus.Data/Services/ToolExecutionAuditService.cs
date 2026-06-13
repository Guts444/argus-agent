using Argus.Core.Models;
using Argus.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Argus.Data.Services;

public sealed class ToolExecutionAuditService(
    IDbContextFactory<ArgusDbContext> dbContextFactory) : IToolExecutionAuditService
{
    public async Task RecordAsync(
        ToolExecutionAudit audit,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var existing = await db.ToolExecutionAudits
            .SingleOrDefaultAsync(
                item => item.ExecutionId == audit.ExecutionId,
                cancellationToken);
        if (existing is null)
        {
            db.ToolExecutionAudits.Add(audit);
        }
        else
        {
            existing.AgentRunId = audit.AgentRunId;
            existing.ConversationId = audit.ConversationId;
            existing.ToolName = audit.ToolName;
            existing.RiskLevel = audit.RiskLevel;
            existing.ApprovalStatus = audit.ApprovalStatus;
            existing.Outcome = audit.Outcome;
            existing.ArgumentsSummary = audit.ArgumentsSummary;
            existing.ResultSummary = audit.ResultSummary;
            existing.Error = audit.Error;
            existing.StartedAt = audit.StartedAt;
            existing.CompletedAt = audit.CompletedAt;
            existing.DurationMilliseconds = audit.DurationMilliseconds;
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ToolExecutionAudit>> GetRecentAsync(
        int take = 100,
        CancellationToken cancellationToken = default)
    {
        var boundedTake = Math.Clamp(take, 1, 500);
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await db.ToolExecutionAudits
            .AsNoTracking()
            .OrderByDescending(audit => audit.StartedAt)
            .Take(boundedTake)
            .ToListAsync(cancellationToken);
    }

    public async Task<ToolExecutionAudit?> GetByExecutionIdAsync(Guid executionId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await db.ToolExecutionAudits
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.ExecutionId == executionId, cancellationToken);
    }

    public async Task<IReadOnlyList<ToolExecutionAudit>> GetFilteredAsync(
        string? toolName = null,
        string? riskLevel = null,
        string? approvalStatus = null,
        string? outcome = null,
        bool? onlyIncomplete = null,
        int take = 100,
        CancellationToken cancellationToken = default)
    {
        var boundedTake = Math.Clamp(take, 1, 500);
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var query = db.ToolExecutionAudits.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(toolName))
        {
            var lowerName = toolName.Trim().ToLower();
            query = query.Where(audit => audit.ToolName.ToLower().Contains(lowerName));
        }

        if (!string.IsNullOrWhiteSpace(riskLevel) && !riskLevel.Equals("All", StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(audit => audit.RiskLevel == riskLevel);
        }

        if (!string.IsNullOrWhiteSpace(approvalStatus) && !approvalStatus.Equals("All", StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(audit => audit.ApprovalStatus == approvalStatus);
        }

        if (!string.IsNullOrWhiteSpace(outcome) && !outcome.Equals("All", StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(audit => audit.Outcome == outcome);
        }

        if (onlyIncomplete == true)
        {
            query = query.Where(audit => audit.Outcome == "started");
        }

        return await query
            .OrderByDescending(audit => audit.StartedAt)
            .Take(boundedTake)
            .ToListAsync(cancellationToken);
    }

    public async Task<int> PruneOldAuditsAsync(DateTimeOffset before, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var oldAudits = db.ToolExecutionAudits.Where(audit => audit.StartedAt < before);
        var count = await oldAudits.CountAsync(cancellationToken);
        db.ToolExecutionAudits.RemoveRange(oldAudits);
        await db.SaveChangesAsync(cancellationToken);
        return count;
    }

    public async Task<int> ClearAllAuditsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var count = await db.ToolExecutionAudits.CountAsync(cancellationToken);
        db.ToolExecutionAudits.RemoveRange(db.ToolExecutionAudits);
        await db.SaveChangesAsync(cancellationToken);
        return count;
    }
}
