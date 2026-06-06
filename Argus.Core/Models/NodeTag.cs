namespace Argus.Core.Models;

public sealed class NodeTag
{
    public Guid NodeId { get; set; }
    public Guid TagId { get; set; }

    public Node? Node { get; set; }
    public Tag? Tag { get; set; }
}
