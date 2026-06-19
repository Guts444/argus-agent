namespace Argus.Core.Models;

public sealed record ProjectInstruction(
    Guid ProjectId,
    string Content,
    DateTimeOffset UpdatedAt);
