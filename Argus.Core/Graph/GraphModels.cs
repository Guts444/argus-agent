using Argus.Core.Models;

namespace Argus.Core.Graph;

public sealed record GraphSnapshot(IReadOnlyList<Node> Nodes, IReadOnlyList<Edge> Edges);

public sealed record NodeTagAssignment(Guid NodeId, IReadOnlyList<Tag> Tags);

public sealed record GraphExportDocument(
    int Version,
    DateTimeOffset ExportedAt,
    IReadOnlyList<Node> Nodes,
    IReadOnlyList<Edge> Edges,
    IReadOnlyList<Tag> Tags,
    IReadOnlyList<NodeTagAssignment> NodeTags);

public enum GraphImportMode
{
    Merge,
    Replace
}

public sealed record NodeConnection(
    Guid EdgeId,
    Guid NodeId,
    string NodeTitle,
    string NodeType,
    string RelationshipType,
    double Strength,
    bool IsOutgoing);

public sealed record DashboardSnapshot(
    IReadOnlyList<Node> ActiveProjects,
    IReadOnlyList<Node> RecentNodes,
    IReadOnlyList<Node> ForgottenIdeas,
    IReadOnlyList<Node> OpenTasks,
    IReadOnlyList<Conversation> RecentConversations,
    IReadOnlyList<Node> MostConnectedNodes,
    IReadOnlyList<Memory> RevisitMemories);
