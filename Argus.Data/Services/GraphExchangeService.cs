using System.Text.Json;
using Argus.Core.Graph;
using Argus.Core.Models;
using Argus.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Argus.Data.Services;

public sealed class GraphExchangeService(IDbContextFactory<ArgusDbContext> dbContextFactory, IGraphService graphService) : IGraphExchangeService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public async Task<string> ExportJsonAsync(bool includeArchived = false, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var nodes = await db.Nodes
            .AsNoTracking()
            .Where(node => includeArchived || !node.IsArchived)
            .OrderBy(node => node.Title)
            .ToListAsync(cancellationToken);
        var nodeIds = nodes.Select(node => node.Id).ToHashSet();

        var edges = await db.Edges
            .AsNoTracking()
            .Where(edge => nodeIds.Contains(edge.SourceNodeId) && nodeIds.Contains(edge.TargetNodeId))
            .OrderBy(edge => edge.RelationshipType)
            .ToListAsync(cancellationToken);

        var tags = await db.NodeTags
            .AsNoTracking()
            .Where(nodeTag => nodeIds.Contains(nodeTag.NodeId))
            .Join(db.Tags.AsNoTracking(), nodeTag => nodeTag.TagId, tag => tag.Id, (_, tag) => tag)
            .Distinct()
            .OrderBy(tag => tag.Name)
            .ToListAsync(cancellationToken);

        var tagById = tags.ToDictionary(tag => tag.Id);
        var nodeTags = await db.NodeTags
            .AsNoTracking()
            .Where(nodeTag => nodeIds.Contains(nodeTag.NodeId))
            .ToListAsync(cancellationToken);
        var assignments = nodeTags
            .GroupBy(nodeTag => nodeTag.NodeId)
            .Select(group => new NodeTagAssignment(
                group.Key,
                group.Select(nodeTag => tagById[nodeTag.TagId]).OrderBy(tag => tag.Name).ToList()))
            .OrderBy(assignment => assignment.NodeId)
            .ToList();

        var document = new GraphExportDocument(1, DateTimeOffset.UtcNow, nodes, edges, tags, assignments);
        return JsonSerializer.Serialize(document, JsonOptions);
    }

    public async Task<GraphSnapshot> ImportJsonAsync(string json, GraphImportMode mode = GraphImportMode.Merge, CancellationToken cancellationToken = default)
    {
        var document = JsonSerializer.Deserialize<GraphExportDocument>(json, JsonOptions)
            ?? throw new InvalidOperationException("Graph import JSON is empty or invalid.");
        if (document.Version != 1)
        {
            throw new InvalidOperationException($"Graph export version {document.Version} is not supported.");
        }

        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        if (mode == GraphImportMode.Replace)
        {
            db.NodeTags.RemoveRange(db.NodeTags);
            db.Edges.RemoveRange(db.Edges);
            db.Tags.RemoveRange(db.Tags);
            db.Nodes.RemoveRange(db.Nodes);
            await db.SaveChangesAsync(cancellationToken);
        }

        foreach (var node in document.Nodes)
        {
            var existing = await db.Nodes.FirstOrDefaultAsync(current => current.Id == node.Id, cancellationToken);
            if (existing is null)
            {
                db.Nodes.Add(CloneNode(node));
            }
            else
            {
                CopyNode(node, existing);
            }
        }

        foreach (var edge in document.Edges)
        {
            var sourceExists = document.Nodes.Any(node => node.Id == edge.SourceNodeId) ||
                await db.Nodes.AnyAsync(node => node.Id == edge.SourceNodeId, cancellationToken);
            var targetExists = document.Nodes.Any(node => node.Id == edge.TargetNodeId) ||
                await db.Nodes.AnyAsync(node => node.Id == edge.TargetNodeId, cancellationToken);
            if (!sourceExists || !targetExists)
            {
                continue;
            }

            var existing = await db.Edges.FirstOrDefaultAsync(current => current.Id == edge.Id, cancellationToken);
            if (existing is null)
            {
                db.Edges.Add(CloneEdge(edge));
            }
            else
            {
                existing.SourceNodeId = edge.SourceNodeId;
                existing.TargetNodeId = edge.TargetNodeId;
                existing.RelationshipType = string.IsNullOrWhiteSpace(edge.RelationshipType) ? "related_to" : edge.RelationshipType.Trim();
                existing.Strength = Math.Clamp(edge.Strength, 0.1, 1.0);
                existing.CreatedAt = edge.CreatedAt;
                existing.UpdatedAt = edge.UpdatedAt;
            }
        }

        await db.SaveChangesAsync(cancellationToken);

        foreach (var tag in document.Tags)
        {
            await TagService.UpsertTagAsync(db, tag.Name, tag.ColorKey, cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);

        foreach (var assignment in document.NodeTags)
        {
            var nodeExists = await db.Nodes.AnyAsync(node => node.Id == assignment.NodeId, cancellationToken);
            if (!nodeExists)
            {
                continue;
            }

            var desiredTags = new List<Tag>();
            foreach (var tag in assignment.Tags)
            {
                desiredTags.Add(await TagService.UpsertTagAsync(db, tag.Name, tag.ColorKey, cancellationToken));
            }

            var desiredIds = desiredTags.Select(tag => tag.Id).ToHashSet();
            var existingAssignments = await db.NodeTags
                .Where(nodeTag => nodeTag.NodeId == assignment.NodeId)
                .ToListAsync(cancellationToken);
            db.NodeTags.RemoveRange(existingAssignments.Where(nodeTag => !desiredIds.Contains(nodeTag.TagId)));

            foreach (var tag in desiredTags)
            {
                if (existingAssignments.All(nodeTag => nodeTag.TagId != tag.Id))
                {
                    db.NodeTags.Add(new NodeTag { NodeId = assignment.NodeId, TagId = tag.Id });
                }
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return await graphService.GetGraphAsync(cancellationToken);
    }

    private static Node CloneNode(Node node)
    {
        var clone = new Node();
        CopyNode(node, clone);
        return clone;
    }

    private static void CopyNode(Node source, Node target)
    {
        target.Id = source.Id == Guid.Empty ? Guid.NewGuid() : source.Id;
        target.Title = source.Title.Trim();
        target.Type = string.IsNullOrWhiteSpace(source.Type) ? "Note" : source.Type.Trim();
        target.Summary = source.Summary;
        target.Body = source.Body;
        target.Status = string.IsNullOrWhiteSpace(source.Status) ? "Active" : source.Status.Trim();
        target.Importance = Math.Clamp(source.Importance, 1, 5);
        target.CreatedAt = source.CreatedAt == default ? DateTimeOffset.UtcNow : source.CreatedAt;
        target.UpdatedAt = source.UpdatedAt == default ? DateTimeOffset.UtcNow : source.UpdatedAt;
        target.LastTouchedAt = source.LastTouchedAt == default ? DateTimeOffset.UtcNow : source.LastTouchedAt;
        target.ColorKey = string.IsNullOrWhiteSpace(source.ColorKey) ? "cyan" : source.ColorKey.Trim();
        target.IconKey = string.IsNullOrWhiteSpace(source.IconKey) ? "node" : source.IconKey.Trim();
        target.IsArchived = source.IsArchived;
        target.PositionX = source.PositionX;
        target.PositionY = source.PositionY;
    }

    private static Edge CloneEdge(Edge edge)
    {
        return new Edge
        {
            Id = edge.Id == Guid.Empty ? Guid.NewGuid() : edge.Id,
            SourceNodeId = edge.SourceNodeId,
            TargetNodeId = edge.TargetNodeId,
            RelationshipType = string.IsNullOrWhiteSpace(edge.RelationshipType) ? "related_to" : edge.RelationshipType.Trim(),
            Strength = Math.Clamp(edge.Strength, 0.1, 1.0),
            CreatedAt = edge.CreatedAt == default ? DateTimeOffset.UtcNow : edge.CreatedAt,
            UpdatedAt = edge.UpdatedAt == default ? DateTimeOffset.UtcNow : edge.UpdatedAt
        };
    }
}
