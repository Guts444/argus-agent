using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Argus.Core.Models;
using Argus.Core.Services;

namespace Argus.AI.Services;

public sealed class OpenAiCompatibleChatService(HttpClient httpClient, ISecretStore secretStore) : IAiChatService
{
    public async Task<AiChatResult> SendAsync(AiProviderProfile? profile, IReadOnlyList<AiChatTurn> messages, CancellationToken cancellationToken = default)
    {
        if (profile is null)
        {
            return new AiChatResult("Configure an AI provider in Settings before chatting.", SetupRequired: true);
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
            using var request = new HttpRequestMessage(HttpMethod.Post, BuildChatUrl(profile.BaseUrl));
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            }

            var isDeepSeek = IsDeepSeekProfile(profile);
            var isOpenAI = IsOpenAIProfile(profile);
            var isOpenRouter = IsOpenRouterProfile(profile);
            AddOpenAiRoutingHeaders(request, profile);
            AddOpenRouterHeaders(request, profile);
            var thinkingEnabled = isDeepSeek && string.Equals(profile.ThinkingMode, "enabled", StringComparison.OrdinalIgnoreCase);
            var openAiReasoningEnabled = isOpenAI && string.Equals(profile.ThinkingMode, "enabled", StringComparison.OrdinalIgnoreCase);
            var openRouterReasoningEnabled = isOpenRouter && string.Equals(profile.ThinkingMode, "enabled", StringComparison.OrdinalIgnoreCase);
            var payload = new Dictionary<string, object?>
            {
                ["model"] = profile.Model,
                ["messages"] = messages.Select(message => new { role = message.Role, content = message.Content }).ToArray(),
                ["stream"] = false
            };

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

            if (!thinkingEnabled && !openAiReasoningEnabled && !openRouterReasoningEnabled)
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
            var message = document.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message");
            var content = message.TryGetProperty("content", out var contentElement)
                ? contentElement.GetString()
                : string.Empty;
            var reasoning = message.TryGetProperty("reasoning_content", out var reasoningElement)
                ? reasoningElement.GetString()
                : message.TryGetProperty("reasoning", out var openRouterReasoningElement)
                    ? openRouterReasoningElement.GetString()
                : null;

            int? promptTokens = null;
            int? completionTokens = null;
            int? totalTokens = null;
            if (document.RootElement.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                promptTokens = usage.TryGetProperty("prompt_tokens", out var promptElement) && promptElement.TryGetInt32(out var prompt)
                    ? prompt
                    : null;
                completionTokens = usage.TryGetProperty("completion_tokens", out var completionElement) && completionElement.TryGetInt32(out var completion)
                    ? completion
                    : null;
                totalTokens = usage.TryGetProperty("total_tokens", out var totalElement) && totalElement.TryGetInt32(out var total)
                    ? total
                    : null;
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

    private static Uri BuildChatUrl(string baseUrl)
    {
        var trimmed = baseUrl.Trim().TrimEnd('/');
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
        if (profile is null)
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
