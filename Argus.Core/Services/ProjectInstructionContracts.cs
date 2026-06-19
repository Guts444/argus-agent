using Argus.Core.Models;

namespace Argus.Core.Services;

public interface IProjectInstructionService
{
    Task<ProjectInstruction?> GetAsync(
        Guid projectId,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyDictionary<Guid, ProjectInstruction>> GetManyAsync(
        IEnumerable<Guid> projectIds,
        CancellationToken cancellationToken = default);
    Task<ProjectInstruction?> SaveAsync(
        Guid projectId,
        string? content,
        CancellationToken cancellationToken = default);
    Task ClearAsync(
        Guid projectId,
        CancellationToken cancellationToken = default);
}
