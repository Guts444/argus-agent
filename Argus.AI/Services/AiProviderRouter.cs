using Argus.Core.Models;
using Argus.Core.Services;

namespace Argus.AI.Services;

public sealed class AiProviderRouter(IEnumerable<IAiProviderAdapter> adapters)
    : IAiChatService, IAiProviderRegistry
{
    private readonly IReadOnlyList<IAiProviderAdapter> registeredAdapters = adapters.ToList();

    public AiProviderCapabilities GetCapabilities(AiProviderProfile profile) =>
        Resolve(profile).GetCapabilities(profile);

    public IReadOnlyList<AiModelMetadata> GetFallbackModels(AiProviderProfile profile) =>
        Resolve(profile).GetFallbackModels(profile);

    public Task<IReadOnlyList<AiModelMetadata>> ListModelsAsync(
        AiProviderProfile profile,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default) =>
        Resolve(profile).ListModelsAsync(profile, forceRefresh, cancellationToken);

    public Task<AiProviderConnectionStatus> GetConnectionStatusAsync(
        AiProviderProfile profile,
        CancellationToken cancellationToken = default) =>
        Resolve(profile).GetConnectionStatusAsync(profile, cancellationToken);

    public async Task<AiChatResult> SendAsync(
        AiProviderProfile? profile,
        IReadOnlyList<AiChatTurn> messages,
        CancellationToken cancellationToken = default)
    {
        if (profile is null)
        {
            return new AiChatResult(
                "Configure an AI provider in Settings before chatting.",
                SetupRequired: true);
        }

        try
        {
            return await Resolve(profile).SendAsync(profile, messages, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new AiChatResult(
                string.Empty,
                Error: $"{profile.Name} failed unexpectedly: {ex.Message}");
        }
    }

    public async Task<float[]?> GenerateEmbeddingAsync(
        AiProviderProfile? profile,
        string text,
        CancellationToken cancellationToken = default)
    {
        if (profile is null)
        {
            return null;
        }

        try
        {
            return await Resolve(profile).GenerateEmbeddingAsync(profile, text, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private IAiProviderAdapter Resolve(AiProviderProfile profile)
    {
        return registeredAdapters.FirstOrDefault(adapter => adapter.CanHandle(profile))
            ?? throw new InvalidOperationException(
                $"No provider adapter is registered for {profile.ProviderType}.");
    }
}
