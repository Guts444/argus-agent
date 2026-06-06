namespace Argus.Core.Models;

public sealed class Edge
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SourceNodeId { get; set; }
    public Guid TargetNodeId { get; set; }
    public string RelationshipType { get; set; } = "related_to";
    public double Strength { get; set; } = 0.6;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public Node? SourceNode { get; set; }
    public Node? TargetNode { get; set; }
}
