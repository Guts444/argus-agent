namespace Argus.Core.Models;

public sealed class MemoryRecallFeedback
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid MemoryId { get; set; }
    public string Query { get; set; } = string.Empty;
    public string Rating { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public Memory? Memory { get; set; }
}
