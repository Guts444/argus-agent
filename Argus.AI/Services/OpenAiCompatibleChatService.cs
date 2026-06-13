using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Argus.Core.Models;
using Argus.Core.Services;

namespace Argus.AI.Services;

public sealed class OpenAiCompatibleChatService(
    HttpClient httpClient,
    ISecretStore secretStore,
    IOpenAiCodexService? openAiCodexService = null,
    ISettingsService? settingsService = null) : IAiChatService, IAiProviderAdapter
{
    public string Id => "openai-compatible";

    public bool CanHandle(AiProviderProfile profile) =>
        !AiModelCatalog.IsOpenAiCodexProvider(profile.ProviderType);

    public AiProviderCapabilities GetCapabilities(AiProviderProfile profile)
    {
        if (IsTrustedLocalEndpoint(profile.BaseUrl))
        {
            return new(
                Id,
                AiProviderKind.Local,
                AiAuthenticationMode.LocalOptional,
                SupportsDynamicModels: true,
                SupportsEmbeddings: true,
                SupportsThinkingToggle: false,
                ReasoningAlwaysEnabled: false,
                "Local endpoints do not require an API key.",
                "Reasoning controls depend on the selected local model and server.");
        }

        if (IsDeepSeekProfile(profile))
        {
            return new(
                Id,
                AiProviderKind.DeepSeek,
                AiAuthenticationMode.ApiKey,
                SupportsDynamicModels: false,
                SupportsEmbeddings: false,
                SupportsThinkingToggle: true,
                ReasoningAlwaysEnabled: false,
                "DeepSeek uses an API key stored in Windows Credential Locker.",
                "Thinking can be disabled. When enabled, DeepSeek V4 accepts high or max reasoning effort.");
        }

        if (IsOpenAIProfile(profile))
        {
            return new(
                Id,
                AiProviderKind.OpenAi,
                AiAuthenticationMode.ApiKey,
                SupportsDynamicModels: true,
                SupportsEmbeddings: true,
                SupportsThinkingToggle: false,
                ReasoningAlwaysEnabled: false,
                "OpenAI API access uses API billing and is separate from ChatGPT subscription access.",
                "Reasoning effort is available only for models that expose it. Choose none when the model allows reasoning to be omitted.");
        }

        if (IsOpenRouterProfile(profile))
        {
            return new(
                Id,
                AiProviderKind.OpenRouter,
                AiAuthenticationMode.ApiKey,
                SupportsDynamicModels: true,
                SupportsEmbeddings: false,
                SupportsThinkingToggle: true,
                ReasoningAlwaysEnabled: false,
                "OpenRouter uses an API key stored in Windows Credential Locker.",
                "Reasoning controls vary by routed model and are sent only when enabled.");
        }

        if (IsAnthropicProfile(profile))
        {
            return new(
                Id,
                AiProviderKind.Anthropic,
                AiAuthenticationMode.ApiKey,
                SupportsDynamicModels: false,
                SupportsEmbeddings: false,
                SupportsThinkingToggle: false,
                ReasoningAlwaysEnabled: false,
                "Anthropic access requires a Claude Console API key. Claude account OAuth is not offered.",
                "Anthropic reasoning controls are not exposed until the native Messages API adapter is completed.");
        }

        return new(
            Id,
            AiProviderKind.OpenAiCompatible,
            AiAuthenticationMode.ApiKey,
            SupportsDynamicModels: true,
            SupportsEmbeddings: true,
            SupportsThinkingToggle: false,
            ReasoningAlwaysEnabled: false,
            "The provider API key is stored in Windows Credential Locker.",
            "Reasoning controls depend on the selected OpenAI-compatible server.");
    }

    public IReadOnlyList<AiModelMetadata> GetFallbackModels(AiProviderProfile profile)
    {
        var knownModels = AiModelCatalog.GetKnownModels(profile.ProviderType, profile.BaseUrl);
        if (knownModels.Count > 0)
        {
            return knownModels;
        }

        return string.IsNullOrWhiteSpace(profile.Model)
            ? []
            : [new AiModelMetadata(profile.Model, profile.Model)];
    }

    public async Task<IReadOnlyList<AiModelMetadata>> ListModelsAsync(
        AiProviderProfile profile,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        if (IsDeepSeekProfile(profile) || IsAnthropicProfile(profile))
        {
            return GetFallbackModels(profile);
        }

        try
        {
            if (IsOpenRouterProfile(profile))
            {
                return await LoadOpenRouterModelsAsync(profile, forceRefresh, cancellationToken);
            }

            if (IsOpenAIProfile(profile))
            {
                return await LoadOpenAiModelsAsync(profile, forceRefresh, cancellationToken);
            }

            return await LoadOpenAiCompatibleModelsAsync(profile, forceRefresh, cancellationToken);
        }
        catch
        {
            return GetFallbackModels(profile);
        }
    }

    public async Task<AiProviderConnectionStatus> GetConnectionStatusAsync(
        AiProviderProfile profile,
        CancellationToken cancellationToken = default)
    {
        var capabilities = GetCapabilities(profile);
        if (capabilities.AuthenticationMode == AiAuthenticationMode.LocalOptional)
        {
            return new(true, capabilities.AuthenticationHelpText);
        }

        var storedKey = await secretStore.GetSecretAsync(profile.ApiKeyStorageKey, cancellationToken);
        if (!string.IsNullOrWhiteSpace(storedKey))
        {
            return new(true, capabilities.AuthenticationHelpText);
        }

        if (!string.IsNullOrWhiteSpace(GetEnvironmentApiKey(profile)))
        {
            return new(
                true,
                $"{profile.Name} is using an API key from the environment.",
                UsesEnvironmentCredential: true);
        }

        return new(
            false,
            $"Add an API key for {profile.Name}. It will be stored in Windows Credential Locker.");
    }

    public async Task<AiChatResult> SendAsync(AiProviderProfile? profile, IReadOnlyList<AiChatTurn> messages, CancellationToken cancellationToken = default)
    {
        if (profile is null)
        {
            return new AiChatResult("Configure an AI provider in Settings before chatting.", SetupRequired: true);
        }

        if (AiModelCatalog.IsOpenAiCodexProvider(profile.ProviderType))
        {
            return openAiCodexService is null
                ? new AiChatResult(
                    "The OpenAI Codex account provider is unavailable.",
                    SetupRequired: true,
                    Error: "Install the standalone Codex CLI and restart Argus.")
                : await openAiCodexService.SendAsync(profile, messages, cancellationToken);
        }

        var localNoKeyAllowed = IsTrustedLocalEndpoint(profile.BaseUrl);
        var apiKey = await secretStore.GetSecretAsync(profile.ApiKeyStorageKey, cancellationToken)
            ?? GetEnvironmentApiKey(profile);

        if (string.IsNullOrWhiteSpace(apiKey) && !localNoKeyAllowed)
        {
            return new AiChatResult($"Add an API key for {profile.Name} in Settings before chatting.", SetupRequired: true);
        }

        try
        {
            var isAnthropic = IsAnthropicProfile(profile);
            using var request = new HttpRequestMessage(HttpMethod.Post, BuildChatUrl(profile.BaseUrl, isAnthropic));
            if (isAnthropic)
            {
                if (!string.IsNullOrWhiteSpace(apiKey))
                {
                    request.Headers.TryAddWithoutValidation("x-api-key", apiKey);
                }
                request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(apiKey))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                }
            }

            var isDeepSeek = IsDeepSeekProfile(profile);
            var isOpenAI = IsOpenAIProfile(profile);
            var isOpenRouter = IsOpenRouterProfile(profile);
            AddOpenAiRoutingHeaders(request, profile);
            AddOpenRouterHeaders(request, profile);
            var thinkingEnabled = isDeepSeek && string.Equals(profile.ThinkingMode, "enabled", StringComparison.OrdinalIgnoreCase);
            var openAiReasoningEnabled = isOpenAI &&
                !string.Equals(profile.ReasoningEffort, "none", StringComparison.OrdinalIgnoreCase);
            var openRouterReasoningEnabled = isOpenRouter && string.Equals(profile.ThinkingMode, "enabled", StringComparison.OrdinalIgnoreCase);
            var payload = new Dictionary<string, object?>
            {
                ["model"] = profile.Model,
                ["messages"] = messages.Select(message => new { role = message.Role, content = message.Content }).ToArray(),
                ["stream"] = false
            };

            if (isAnthropic)
            {
                payload["max_tokens"] = 4096;
                payload["messages"] = messages
                    .Where(message => message.Role is "user" or "assistant")
                    .Select(message => new { role = message.Role, content = message.Content })
                    .ToArray();
                var systemPrompt = string.Join(
                    "\n\n",
                    messages
                        .Where(message => message.Role == "system")
                        .Select(message => message.Content)
                        .Where(content => !string.IsNullOrWhiteSpace(content)));
                if (!string.IsNullOrWhiteSpace(systemPrompt))
                {
                    payload["system"] = systemPrompt;
                }
            }

            if (isDeepSeek)
            {
                payload["thinking"] = new { type = thinkingEnabled ? "enabled" : "disabled" };
                if (thinkingEnabled)
                {
                    payload["reasoning_effort"] = NormalizeReasoningEffort(profile.ReasoningEffort);
                }
            }
            else if (openAiReasoningEnabled)
            {
                payload["reasoning_effort"] = NormalizeOpenAiReasoningEffort(profile.ReasoningEffort);
            }
            else if (openRouterReasoningEnabled)
            {
                payload["reasoning"] = new
                {
                    effort = NormalizeOpenRouterReasoningEffort(profile.ReasoningEffort),
                    exclude = false
                };
            }

            if (!isAnthropic && !thinkingEnabled && !openAiReasoningEnabled && !openRouterReasoningEnabled)
            {
                payload["temperature"] = 0.4;
            }

            request.Content = JsonContent.Create(payload);

            using var response = await httpClient.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new AiChatResult(string.Empty, Error: $"Provider returned {(int)response.StatusCode}: {body}");
            }

            using var document = JsonDocument.Parse(body);
            string? content = string.Empty;
            string? reasoning = null;

            if (isAnthropic)
            {
                if (document.RootElement.TryGetProperty("content", out var contentArray) &&
                    contentArray.ValueKind == JsonValueKind.Array)
                {
                    content = string.Join(
                        "\n",
                        contentArray.EnumerateArray()
                            .Where(block => block.TryGetProperty("type", out var type) && type.GetString() == "text")
                            .Select(block => block.TryGetProperty("text", out var text) ? text.GetString() : null)
                            .Where(text => !string.IsNullOrWhiteSpace(text)));
                }
            }
            else
            {
                var message = document.RootElement
                    .GetProperty("choices")[0]
                    .GetProperty("message");
                content = message.TryGetProperty("content", out var contentElement)
                    ? contentElement.GetString()
                    : string.Empty;
                reasoning = message.TryGetProperty("reasoning_content", out var reasoningElement)
                    ? reasoningElement.GetString()
                    : message.TryGetProperty("reasoning", out var openRouterReasoningElement)
                        ? openRouterReasoningElement.GetString()
                    : null;
            }

            int? promptTokens = null;
            int? completionTokens = null;
            int? totalTokens = null;
            if (document.RootElement.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                if (isAnthropic)
                {
                    promptTokens = usage.TryGetProperty("input_tokens", out var inputElement) && inputElement.TryGetInt32(out var inputVal) ? inputVal : null;
                    completionTokens = usage.TryGetProperty("output_tokens", out var outputElement) && outputElement.TryGetInt32(out var outputVal) ? outputVal : null;
                    totalTokens = (promptTokens ?? 0) + (completionTokens ?? 0);
                }
                else
                {
                    promptTokens = usage.TryGetProperty("prompt_tokens", out var promptElement) && promptElement.TryGetInt32(out var prompt) ? prompt : null;
                    completionTokens = usage.TryGetProperty("completion_tokens", out var completionElement) && completionElement.TryGetInt32(out var completion) ? completion : null;
                    totalTokens = usage.TryGetProperty("total_tokens", out var totalElement) && totalElement.TryGetInt32(out var total) ? total : null;
                }
            }

            var model = document.RootElement.TryGetProperty("model", out var modelElement)
                ? modelElement.GetString()
                : profile.Model;

            return new AiChatResult(
                content ?? string.Empty,
                ReasoningContent: reasoning,
                PromptTokens: promptTokens,
                CompletionTokens: completionTokens,
                TotalTokens: totalTokens,
                Model: model);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return new AiChatResult(string.Empty, Error: ex.Message);
        }
    }

    private async Task<IReadOnlyList<AiModelMetadata>> LoadOpenRouterModelsAsync(
        AiProviderProfile profile,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        const string cacheKey = "ModelCatalog:OpenRouter";
        var cached = forceRefresh || settingsService is null
            ? null
            : await settingsService.GetSettingAsync(cacheKey, null, cancellationToken);
        if (!string.IsNullOrWhiteSpace(cached))
        {
            return FilterOpenRouterModels(AiModelCatalog.ParseOpenRouterModels(cached));
        }

        var apiKey = await ResolveApiKeyAsync(profile, cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://openrouter.ai/api/v1/models");
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }
        AddOpenRouterHeaders(request, profile);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return GetFallbackModels(profile);
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (settingsService is not null)
        {
            await settingsService.SaveSettingAsync(cacheKey, body, cancellationToken);
        }

        var models = FilterOpenRouterModels(AiModelCatalog.ParseOpenRouterModels(body));
        return models.Count > 0 ? models : GetFallbackModels(profile);
    }

    private static IReadOnlyList<AiModelMetadata> FilterOpenRouterModels(
        IReadOnlyList<AiModelMetadata> models)
    {
        return models
            .Where(model =>
                model.Id.StartsWith("deepseek/", StringComparison.OrdinalIgnoreCase) ||
                model.Id.StartsWith("openai/", StringComparison.OrdinalIgnoreCase) ||
                model.Id.StartsWith("anthropic/", StringComparison.OrdinalIgnoreCase) ||
                model.Id.StartsWith("google/", StringComparison.OrdinalIgnoreCase) ||
                model.Id.StartsWith("qwen/", StringComparison.OrdinalIgnoreCase) ||
                model.Id.Equals("openrouter/auto", StringComparison.OrdinalIgnoreCase))
            .Take(250)
            .ToList();
    }

    private async Task<IReadOnlyList<AiModelMetadata>> LoadOpenAiModelsAsync(
        AiProviderProfile profile,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        const string cacheKey = "ModelCatalog:OpenAI";
        var cached = forceRefresh || settingsService is null
            ? null
            : await settingsService.GetSettingAsync(cacheKey, null, cancellationToken);
        if (!string.IsNullOrWhiteSpace(cached))
        {
            var cachedModels = AiModelCatalog.ParseOpenAiModels(cached);
            if (cachedModels.Count > 0)
            {
                return cachedModels;
            }
        }

        var apiKey = await ResolveApiKeyAsync(profile, cancellationToken);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return GetFallbackModels(profile);
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, BuildModelsUrl(profile.BaseUrl));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        AddOpenAiRoutingHeaders(request, profile);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return GetFallbackModels(profile);
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (settingsService is not null)
        {
            await settingsService.SaveSettingAsync(cacheKey, body, cancellationToken);
        }

        var models = AiModelCatalog.ParseOpenAiModels(body);
        return models.Count > 0 ? models : GetFallbackModels(profile);
    }

    private async Task<IReadOnlyList<AiModelMetadata>> LoadOpenAiCompatibleModelsAsync(
        AiProviderProfile profile,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        if (!forceRefresh && !string.IsNullOrWhiteSpace(profile.Model))
        {
            return GetFallbackModels(profile);
        }

        var apiKey = await ResolveApiKeyAsync(profile, cancellationToken);
        if (string.IsNullOrWhiteSpace(apiKey) && !IsTrustedLocalEndpoint(profile.BaseUrl))
        {
            return GetFallbackModels(profile);
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, BuildModelsUrl(profile.BaseUrl));
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return GetFallbackModels(profile);
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var models = AiModelCatalog.ParseOpenAiModels(body, isLocalOrCustom: true);
        return models.Count > 0 ? models : GetFallbackModels(profile);
    }

    private async Task<string?> ResolveApiKeyAsync(
        AiProviderProfile profile,
        CancellationToken cancellationToken)
    {
        return await secretStore.GetSecretAsync(profile.ApiKeyStorageKey, cancellationToken)
            ?? GetEnvironmentApiKey(profile);
    }

    private static Uri BuildModelsUrl(string baseUrl)
    {
        var trimmed = baseUrl.Trim().TrimEnd('/');
        if (trimmed.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^"/chat/completions".Length];
        }

        if (trimmed.EndsWith("/models", StringComparison.OrdinalIgnoreCase))
        {
            return new Uri(trimmed);
        }

        return new Uri($"{trimmed}/models");
    }

    private static Uri BuildChatUrl(string baseUrl, bool isAnthropic)
    {
        var trimmed = baseUrl.Trim().TrimEnd('/');
        if (isAnthropic || trimmed.Contains("api.anthropic.com", StringComparison.OrdinalIgnoreCase))
        {
            if (trimmed.EndsWith("/messages", StringComparison.OrdinalIgnoreCase))
            {
                return new Uri(trimmed);
            }
            return new Uri($"{trimmed}/messages");
        }
        if (trimmed.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            return new Uri(trimmed);
        }

        return new Uri($"{trimmed}/chat/completions");
    }

    private static bool IsTrustedLocalEndpoint(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }

        return uri.Scheme is "http" or "https" &&
            (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
             uri.Host == "127.0.0.1" ||
             uri.Host == "::1");
    }

    private static bool IsDeepSeekProfile(AiProviderProfile profile)
    {
        return profile.ProviderType.Equals("DeepSeek", StringComparison.OrdinalIgnoreCase) ||
            profile.BaseUrl.Contains("api.deepseek.com", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsOpenAIProfile(AiProviderProfile profile)
    {
        return profile.ProviderType.Equals("OpenAI", StringComparison.OrdinalIgnoreCase) ||
            profile.BaseUrl.Contains("api.openai.com", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAnthropicProfile(AiProviderProfile profile)
    {
        return profile.ProviderType.Equals("Anthropic", StringComparison.OrdinalIgnoreCase) ||
            profile.BaseUrl.Contains("api.anthropic.com", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsOpenRouterProfile(AiProviderProfile profile)
    {
        return profile.ProviderType.Equals("OpenRouter", StringComparison.OrdinalIgnoreCase) ||
            profile.BaseUrl.Contains("openrouter.ai", StringComparison.OrdinalIgnoreCase);
    }

    private static void AddOpenAiRoutingHeaders(HttpRequestMessage request, AiProviderProfile profile)
    {
        if (!IsOpenAIProfile(profile))
        {
            return;
        }

        var organizationId = FirstNonEmpty(profile.OrganizationId, "OPENAI_ORG_ID", "OPENAI_ORGANIZATION");
        if (!string.IsNullOrWhiteSpace(organizationId))
        {
            request.Headers.TryAddWithoutValidation("OpenAI-Organization", organizationId);
        }

        var projectId = FirstNonEmpty(profile.ProjectId, "OPENAI_PROJECT_ID", "OPENAI_PROJECT");
        if (!string.IsNullOrWhiteSpace(projectId))
        {
            request.Headers.TryAddWithoutValidation("OpenAI-Project", projectId);
        }

        request.Headers.TryAddWithoutValidation("X-Client-Request-Id", $"argus-{Guid.NewGuid():N}");
    }

    private static void AddOpenRouterHeaders(HttpRequestMessage request, AiProviderProfile profile)
    {
        if (!IsOpenRouterProfile(profile))
        {
            return;
        }

        request.Headers.TryAddWithoutValidation("HTTP-Referer", "https://github.com/argus-local/argus");
        request.Headers.TryAddWithoutValidation("X-Title", "Argus");
    }

    private static string? FirstNonEmpty(string configuredValue, params string[] environmentNames)
    {
        if (!string.IsNullOrWhiteSpace(configuredValue))
        {
            return configuredValue.Trim();
        }

        return environmentNames
            .Select(Environment.GetEnvironmentVariable)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }

    private static string? GetEnvironmentApiKey(AiProviderProfile profile)
    {
        var candidates = new List<string>();
        if (IsDeepSeekProfile(profile))
        {
            candidates.Add("ARGUS_DEEPSEEK_API_KEY");
            candidates.Add("DEEPSEEK_API_KEY");
        }
        else if (IsOpenAIProfile(profile))
        {
            candidates.Add("ARGUS_OPENAI_API_KEY");
            candidates.Add("OPENAI_API_KEY");
        }
        else if (IsOpenRouterProfile(profile))
        {
            candidates.Add("ARGUS_OPENROUTER_API_KEY");
            candidates.Add("OPENROUTER_API_KEY");
        }
        else if (IsAnthropicProfile(profile))
        {
            candidates.Add("ARGUS_ANTHROPIC_API_KEY");
            candidates.Add("ANTHROPIC_API_KEY");
        }

        if (!string.IsNullOrWhiteSpace(profile.ApiKeyStorageKey))
        {
            candidates.Add($"ARGUS_{NormalizeEnvironmentName(profile.ApiKeyStorageKey)}");
        }

        candidates.Add($"ARGUS_{NormalizeEnvironmentName(profile.Name)}_API_KEY");
        return candidates
            .Select(Environment.GetEnvironmentVariable)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }

    private static string NormalizeEnvironmentName(string value)
    {
        return new string(value.Select(character => char.IsLetterOrDigit(character) ? char.ToUpperInvariant(character) : '_').ToArray());
    }

    private static string NormalizeReasoningEffort(string effort)
    {
        return effort.Trim().ToLowerInvariant() switch
        {
            "max" or "xhigh" => "max",
            _ => "high"
        };
    }

    private static string NormalizeOpenAiReasoningEffort(string effort)
    {
        return effort.Trim().ToLowerInvariant() switch
        {
            "none" => "none",
            "low" => "low",
            "high" => "high",
            "xhigh" or "max" => "xhigh",
            _ => "medium"
        };
    }

    private static string NormalizeOpenRouterReasoningEffort(string effort)
    {
        return effort.Trim().ToLowerInvariant() switch
        {
            "none" => "none",
            "minimal" => "minimal",
            "low" => "low",
            "medium" => "medium",
            "xhigh" or "max" => "xhigh",
            _ => "high"
        };
    }

    public async Task<float[]?> GenerateEmbeddingAsync(AiProviderProfile? profile, string text, CancellationToken cancellationToken = default)
    {
        if (profile is null || AiModelCatalog.IsOpenAiCodexProvider(profile.ProviderType))
        {
            return null;
        }

        var localNoKeyAllowed = IsTrustedLocalEndpoint(profile.BaseUrl);
        var apiKey = await secretStore.GetSecretAsync(profile.ApiKeyStorageKey, cancellationToken)
            ?? GetEnvironmentApiKey(profile);

        if (string.IsNullOrWhiteSpace(apiKey) && !localNoKeyAllowed)
        {
            return null;
        }

        try
        {
            var trimmed = profile.BaseUrl.Trim().TrimEnd('/');
            if (trimmed.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            {
                trimmed = trimmed[..^"/chat/completions".Length];
            }

            Uri embeddingUrl;
            if (trimmed.EndsWith("/embeddings", StringComparison.OrdinalIgnoreCase))
            {
                embeddingUrl = new Uri(trimmed);
            }
            else
            {
                embeddingUrl = new Uri($"{trimmed}/embeddings");
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, embeddingUrl);
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            }

            AddOpenAiRoutingHeaders(request, profile);

            var embeddingModel = IsOpenAIProfile(profile)
                ? "text-embedding-3-small"
                : (profile.Model.Contains("embedding") ? profile.Model : "text-embedding-3-small");

            var payload = new Dictionary<string, object>
            {
                ["model"] = embeddingModel,
                ["input"] = text
            };

            request.Content = JsonContent.Create(payload);

            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(body);
            var dataArray = document.RootElement.GetProperty("data");
            if (dataArray.GetArrayLength() > 0)
            {
                var embeddingElement = dataArray[0].GetProperty("embedding");
                var length = embeddingElement.GetArrayLength();
                var embedding = new float[length];
                for (int i = 0; i < length; i++)
                {
                    embedding[i] = embeddingElement[i].GetSingle();
                }
                return embedding;
            }
        }
        catch
        {
            // Fail silently and return null to fallback to text search
        }

        return null;
    }
}
