namespace Argus.Core.Models;

public sealed class Message
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ConversationId { get; set; }
    public string Role { get; set; } = "user";
    public string Content { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid? LinkedNodeId { get; set; }

    public Conversation? Conversation { get; set; }
    public Node? LinkedNode { get; set; }

    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public bool IsExpanded { get; set; }
}
