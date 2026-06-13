using Argus.Core.Models;
using Argus.Core.Services;

namespace Argus.AI.Services;

public sealed class CodexProviderAdapter(IOpenAiCodexService codexService) : IAiProviderAdapter
{
    public string Id => "codex-app-server";

    public bool CanHandle(AiProviderProfile profile) =>
        AiModelCatalog.IsOpenAiCodexProvider(profile.ProviderType);

    public AiProviderCapabilities GetCapabilities(AiProviderProfile profile) =>
        new(
            Id,
            AiProviderKind.OpenAiCodex,
            AiAuthenticationMode.CodexAccount,
            SupportsDynamicModels: true,
            SupportsEmbeddings: false,
            SupportsThinkingToggle: false,
            ReasoningAlwaysEnabled: true,
            AuthenticationHelpText:
                "Uses the official standalone Codex CLI for ChatGPT sign-in. Codex owns and refreshes the OAuth tokens; Argus does not store them.",
            ReasoningHelpText:
                "Codex reasoning is always enabled for these models. Choose the effort level used for the next turn.");

    public IReadOnlyList<AiModelMetadata> GetFallbackModels(AiProviderProfile profile) =>
        AiModelCatalog.CodexModels;

    public async Task<IReadOnlyList<AiModelMetadata>> ListModelsAsync(
        AiProviderProfile profile,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        var models = await codexService.ListModelsAsync(cancellationToken);
        return models.Count > 0 ? models : GetFallbackModels(profile);
    }

    public async Task<AiProviderConnectionStatus> GetConnectionStatusAsync(
        AiProviderProfile profile,
        CancellationToken cancellationToken = default)
    {
        var account = await codexService.GetAccountAsync(cancellationToken);
        return new AiProviderConnectionStatus(account.IsAuthenticated, account.Status);
    }

    public Task<AiChatResult> SendAsync(
        AiProviderProfile profile,
        IReadOnlyList<AiChatTurn> messages,
        CancellationToken cancellationToken = default) =>
        codexService.SendAsync(profile, messages, cancellationToken);

    public Task<float[]?> GenerateEmbeddingAsync(
        AiProviderProfile profile,
        string text,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<float[]?>(null);
}
