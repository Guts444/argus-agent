namespace Argus.Core.Models;

public sealed class Memory
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Text { get; set; } = string.Empty;
    public string Source { get; set; } = "manual";
    public int Importance { get; set; } = 3;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastRetrievedAt { get; set; }
    public Guid? LinkedNodeId { get; set; }
    public string? EmbeddingJson { get; set; }

    public Node? LinkedNode { get; set; }
}
