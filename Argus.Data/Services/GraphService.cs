using Argus.Core.Graph;
using Argus.Core.Models;
using Argus.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Argus.Data.Services;

public sealed class GraphService(IDbContextFactory<ArgusDbContext> dbContextFactory) : IGraphService
{
    private readonly ForceDirectedGraphLayout layout = new();

    public async Task<GraphSnapshot> GetGraphAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var nodes = await db.Nodes
            .AsNoTracking()
            .Where(node => !node.IsArchived)
            .OrderByDescending(node => node.Importance)
            .ThenBy(node => node.Title)
            .ToListAsync(cancellationToken);

        var nodeIds = nodes.Select(node => node.Id).ToHashSet();
        var edges = await db.Edges
            .AsNoTracking()
            .Where(edge => nodeIds.Contains(edge.SourceNodeId) && nodeIds.Contains(edge.TargetNodeId))
            .ToListAsync(cancellationToken);

        if (nodes.Any(node => !node.PositionX.HasValue || !node.PositionY.HasValue))
        {
            layout.Apply(nodes, edges);
            foreach (var node in nodes)
            {
                await SaveNodePositionAsync(node.Id, node.PositionX ?? 0, node.PositionY ?? 0, cancellationToken);
            }
        }

        return new GraphSnapshot(nodes, edges);
    }

    public async Task<IReadOnlyList<Node>> SearchNodesAsync(string query, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var trimmed = query.Trim();
        if (trimmed.Length == 0)
        {
            return await db.Nodes
                .AsNoTracking()
                .Where(node => !node.IsArchived)
                .OrderByDescending(node => node.LastTouchedAt)
                .Take(25)
                .ToListAsync(cancellationToken);
        }

        var pattern = $"%{trimmed}%";
        var ftsQuery = BuildFtsQuery(trimmed);
        if (ftsQuery.Length > 0)
        {
            var ftsResults = await SearchNodesWithFtsAsync(db, ftsQuery, cancellationToken);
            if (ftsResults.Count > 0)
            {
                return ftsResults;
            }
        }

        return await db.Nodes
            .AsNoTracking()
            .Where(node => !node.IsArchived &&
                (EF.Functions.Like(node.Title, pattern) ||
                 EF.Functions.Like(node.Type, pattern) ||
                 EF.Functions.Like(node.Summary ?? string.Empty, pattern) ||
                 EF.Functions.Like(node.Body ?? string.Empty, pattern)))
            .OrderByDescending(node => node.Importance)
            .ThenByDescending(node => node.LastTouchedAt)
            .Take(50)
            .ToListAsync(cancellationToken);
    }

    private static async Task<IReadOnlyList<Node>> SearchNodesWithFtsAsync(ArgusDbContext db, string ftsQuery, CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        var shouldClose = connection.State != System.Data.ConnectionState.Open;
        if (shouldClose)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT n.Id, n.Title, n.Type, n.Summary, n.Body, n.Status, n.Importance,
                       n.CreatedAt, n.UpdatedAt, n.LastTouchedAt, n.ColorKey, n.IconKey,
                       n.IsArchived, n.PositionX, n.PositionY
                FROM NodeSearch s
                JOIN Nodes n ON n.Id = s.NodeId
                WHERE s MATCH $query AND n.IsArchived = 0
                ORDER BY bm25(s), n.Importance DESC, n.LastTouchedAt DESC
                LIMIT 50;
                """;
            command.Parameters.Add(new SqliteParameter("$query", ftsQuery));

            var results = new List<Node>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                results.Add(new Node
                {
                    Id = reader.GetGuid(0),
                    Title = reader.GetString(1),
                    Type = reader.GetString(2),
                    Summary = reader.IsDBNull(3) ? null : reader.GetString(3),
                    Body = reader.IsDBNull(4) ? null : reader.GetString(4),
                    Status = reader.GetString(5),
                    Importance = reader.GetInt32(6),
                    CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(7)),
                    UpdatedAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(8)),
                    LastTouchedAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(9)),
                    ColorKey = reader.GetString(10),
                    IconKey = reader.GetString(11),
                    IsArchived = reader.GetBoolean(12),
                    PositionX = reader.IsDBNull(13) ? null : reader.GetDouble(13),
                    PositionY = reader.IsDBNull(14) ? null : reader.GetDouble(14)
                });
            }

            return results;
        }
        catch (SqliteException)
        {
            return Array.Empty<Node>();
        }
        finally
        {
            if (shouldClose)
            {
                await connection.CloseAsync();
            }
        }
    }

    private static string BuildFtsQuery(string query)
    {
        var tokens = query
            .Split(query.Where(character => !char.IsLetterOrDigit(character)).Distinct().ToArray(), StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(token => token.Length > 1)
            .Select(token => $"{token}*")
            .Take(8);

        return string.Join(" ", tokens);
    }

    public async Task<Node> CreateNodeAsync(Node node, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        node.Id = node.Id == Guid.Empty ? Guid.NewGuid() : node.Id;
        node.CreatedAt = now;
        node.UpdatedAt = now;
        node.LastTouchedAt = now;

        var count = await db.Nodes.CountAsync(cancellationToken);
        node.PositionX ??= 180 + count % 6 * 140;
        node.PositionY ??= 150 + count / 6 * 100;

        db.Nodes.Add(node);
        await db.SaveChangesAsync(cancellationToken);
        return node;
    }

    public async Task<Node> UpdateNodeAsync(Node node, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var existing = await db.Nodes.FirstAsync(existingNode => existingNode.Id == node.Id, cancellationToken);
        existing.Title = node.Title;
        existing.Type = node.Type;
        existing.Summary = node.Summary;
        existing.Body = node.Body;
        existing.Status = node.Status;
        existing.Importance = node.Importance;
        existing.ColorKey = node.ColorKey;
        existing.IconKey = node.IconKey;
        existing.IsArchived = node.IsArchived;
        existing.PositionX = node.PositionX;
        existing.PositionY = node.PositionY;
        existing.UpdatedAt = DateTimeOffset.UtcNow;
        existing.LastTouchedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return existing;
    }

    public async Task DeleteNodeAsync(Guid nodeId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var node = await db.Nodes.FirstOrDefaultAsync(existing => existing.Id == nodeId, cancellationToken);
        if (node is null)
        {
            return;
        }

        var edges = db.Edges.Where(edge => edge.SourceNodeId == nodeId || edge.TargetNodeId == nodeId);
        var tags = db.NodeTags.Where(tag => tag.NodeId == nodeId);
        await db.Messages.Where(message => message.LinkedNodeId == nodeId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(message => message.LinkedNodeId, _ => null), cancellationToken);
        await db.Memories.Where(memory => memory.LinkedNodeId == nodeId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(memory => memory.LinkedNodeId, _ => null), cancellationToken);

        db.Edges.RemoveRange(edges);
        db.NodeTags.RemoveRange(tags);
        db.Nodes.Remove(node);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<Edge> CreateEdgeAsync(Guid sourceNodeId, Guid targetNodeId, string relationshipType, double strength, CancellationToken cancellationToken = default)
    {
        if (sourceNodeId == targetNodeId)
        {
            throw new InvalidOperationException("An edge must connect two different nodes.");
        }

        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var edge = new Edge
        {
            SourceNodeId = sourceNodeId,
            TargetNodeId = targetNodeId,
            RelationshipType = string.IsNullOrWhiteSpace(relationshipType) ? "related_to" : relationshipType.Trim(),
            Strength = Math.Clamp(strength, 0.1, 1.0),
            CreatedAt = now,
            UpdatedAt = now
        };

        db.Edges.Add(edge);
        await db.SaveChangesAsync(cancellationToken);
        return edge;
    }

    public async Task DeleteEdgeAsync(Guid edgeId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var edge = await db.Edges.FirstOrDefaultAsync(existing => existing.Id == edgeId, cancellationToken);
        if (edge is null)
        {
            return;
        }

        db.Edges.Remove(edge);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SaveNodePositionAsync(Guid nodeId, double x, double y, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await db.Nodes
            .Where(node => node.Id == nodeId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(node => node.PositionX, x)
                .SetProperty(node => node.PositionY, y)
                .SetProperty(node => node.UpdatedAt, DateTimeOffset.UtcNow)
                .SetProperty(node => node.LastTouchedAt, DateTimeOffset.UtcNow), cancellationToken);
    }

    public async Task<IReadOnlyList<NodeConnection>> GetConnectionsAsync(Guid nodeId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var outgoing = await db.Edges
            .AsNoTracking()
            .Where(edge => edge.SourceNodeId == nodeId)
            .Join(db.Nodes.AsNoTracking(), edge => edge.TargetNodeId, node => node.Id,
                (edge, node) => new NodeConnection(edge.Id, node.Id, node.Title, node.Type, edge.RelationshipType, edge.Strength, true))
            .ToListAsync(cancellationToken);

        var incoming = await db.Edges
            .AsNoTracking()
            .Where(edge => edge.TargetNodeId == nodeId)
            .Join(db.Nodes.AsNoTracking(), edge => edge.SourceNodeId, node => node.Id,
                (edge, node) => new NodeConnection(edge.Id, node.Id, node.Title, node.Type, edge.RelationshipType, edge.Strength, false))
            .ToListAsync(cancellationToken);

        return outgoing.Concat(incoming)
            .OrderByDescending(connection => connection.Strength)
            .ThenBy(connection => connection.NodeTitle)
            .ToList();
    }

    public async Task<DashboardSnapshot> GetDashboardAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        var activeProjects = await db.Nodes.AsNoTracking()
            .Where(node => !node.IsArchived && node.Type == "Project")
            .OrderByDescending(node => node.Importance)
            .ThenByDescending(node => node.LastTouchedAt)
            .Take(6)
            .ToListAsync(cancellationToken);

        var recentNodes = await db.Nodes.AsNoTracking()
            .Where(node => !node.IsArchived)
            .OrderByDescending(node => node.LastTouchedAt)
            .Take(8)
            .ToListAsync(cancellationToken);

        var forgottenIdeas = await db.Nodes.AsNoTracking()
            .Where(node => !node.IsArchived && node.Type == "Idea")
            .OrderBy(node => node.LastTouchedAt)
            .Take(5)
            .ToListAsync(cancellationToken);

        var openTasks = await db.Nodes.AsNoTracking()
            .Where(node => !node.IsArchived && node.Type == "Task" && node.Status != "Done")
            .OrderByDescending(node => node.Importance)
            .Take(6)
            .ToListAsync(cancellationToken);

        var recentConversations = await db.Conversations.AsNoTracking()
            .OrderByDescending(conversation => conversation.UpdatedAt)
            .Take(5)
            .ToListAsync(cancellationToken);

        var outgoingCounts = await db.Edges.AsNoTracking()
            .GroupBy(edge => edge.SourceNodeId)
            .Select(group => new { NodeId = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);
        var incomingCounts = await db.Edges.AsNoTracking()
            .GroupBy(edge => edge.TargetNodeId)
            .Select(group => new { NodeId = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);

        var degreeCounts = outgoingCounts
            .Concat(incomingCounts)
            .GroupBy(item => item.NodeId)
            .Select(group => new { NodeId = group.Key, Count = group.Sum(item => item.Count) })
            .OrderByDescending(item => item.Count)
            .Take(6)
            .ToList();

        var connectedIds = degreeCounts.Select(item => item.NodeId).ToHashSet();
        var connectedNodes = await db.Nodes.AsNoTracking()
            .Where(node => connectedIds.Contains(node.Id))
            .ToListAsync(cancellationToken);
        var mostConnected = connectedNodes
            .OrderByDescending(node => degreeCounts.Find(item => item.NodeId == node.Id)?.Count ?? 0)
            .ToList();

        var revisitMemories = await db.Memories.AsNoTracking()
            .OrderByDescending(memory => memory.Importance)
            .ThenBy(memory => memory.LastRetrievedAt ?? memory.CreatedAt)
            .Take(5)
            .ToListAsync(cancellationToken);

        return new DashboardSnapshot(activeProjects, recentNodes, forgottenIdeas, openTasks, recentConversations, mostConnected, revisitMemories);
    }
}
