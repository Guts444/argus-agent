namespace Argus.Core.Models;

public sealed class Node
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = string.Empty;
    public string Type { get; set; } = "Note";
    public string? Summary { get; set; }
    public string? Body { get; set; }
    public string Status { get; set; } = "Active";
    public int Importance { get; set; } = 3;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastTouchedAt { get; set; } = DateTimeOffset.UtcNow;
    public string ColorKey { get; set; } = "cyan";
    public string IconKey { get; set; } = "node";
    public bool IsArchived { get; set; }
    public double? PositionX { get; set; }
    public double? PositionY { get; set; }

    public ICollection<Edge> OutgoingEdges { get; set; } = new List<Edge>();
    public ICollection<Edge> IncomingEdges { get; set; } = new List<Edge>();
    public ICollection<NodeTag> NodeTags { get; set; } = new List<NodeTag>();
}
