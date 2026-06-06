namespace Argus.Core.Models;

public sealed class Tag
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string ColorKey { get; set; } = "cyan";

    public ICollection<NodeTag> NodeTags { get; set; } = new List<NodeTag>();
}
