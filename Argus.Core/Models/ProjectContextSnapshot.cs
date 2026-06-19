namespace Argus.Core.Models;

public sealed record ProjectContextSnapshot(
    IReadOnlyList<ProjectContext> Projects,
    DateTimeOffset CapturedAt,
    string? Error = null)
{
    public bool HasError => !string.IsNullOrWhiteSpace(Error);
}
