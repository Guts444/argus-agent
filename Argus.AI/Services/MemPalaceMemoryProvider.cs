using Argus.Core.Models;
using Argus.Core.Services;

namespace Argus.AI.Services;

public sealed class MemPalaceMemoryProvider : IMemoryProvider
{
    public Task<IReadOnlyList<Memory>> RecallAsync(string query, int take = 10, CancellationToken cancellationToken = default)
    {
        // Placeholder bridge: the app currently registers LocalMemoryService as the active provider.
        IReadOnlyList<Memory> memories = Array.Empty<Memory>();
        return Task.FromResult(memories);
    }
}
