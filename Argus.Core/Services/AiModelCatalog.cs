using System.Text.Json;

namespace Argus.Core.Services;

public sealed record AiModelMetadata(
    string Id,
    string Name,
    int? ContextWindowTokens = null,
    int? MaxOutputTokens = null,
    bool SupportsThinking = false,
    IReadOnlyList<string>? ReasoningEfforts = null);

public static class AiModelCatalog
{
    public static readonly IReadOnlyList<AiModelMetadata> DeepSeekModels =
    [
        new("deepseek-v4-pro", "DeepSeek V4 Pro", 1_048_576, 393_216, true, ["high", "max"]),
        new("deepseek-v4-flash", "DeepSeek V4 Flash", 1_048_576, 393_216, true, ["high", "max"])
    ];

    public static readonly IReadOnlyList<AiModelMetadata> OpenAiModels =
    [
        new("gpt-5.5", "GPT-5.5", 1_048_576, 131_072, true, ["none", "low", "medium", "high", "xhigh"]),
        new("gpt-5.4", "GPT-5.4", 1_048_576, 131_072, true, ["none", "low", "medium", "high", "xhigh"]),
        new("gpt-5.4-mini", "GPT-5.4 mini", 409_600, 131_072, true, ["none", "low", "medium", "high", "xhigh"]),
        new("gpt-5", "GPT-5", 400_000, 128_000, true, ["none", "low", "medium", "high", "xhigh"]),
        new("gpt-5-mini", "GPT-5 mini", 400_000, 128_000, true, ["none", "low", "medium", "high", "xhigh"]),
        new("gpt-5-nano", "GPT-5 nano", 400_000, 128_000, true, ["none", "low", "medium", "high", "xhigh"]),
        new("gpt-4.1", "GPT-4.1", 1_048_576, 32_768, false, ["none"])
    ];

    public static readonly IReadOnlyList<AiModelMetadata> AnthropicModels =
    [
        new("claude-3-5-sonnet-latest", "Claude 3.5 Sonnet (Latest)", 200_000, 8192),
        new("claude-3-5-haiku-latest", "Claude 3.5 Haiku (Latest)", 200_000, 8192),
        new("claude-3-opus-latest", "Claude 3 Opus (Latest)", 200_000, 4096)
    ];

    public static readonly IReadOnlyList<AiModelMetadata> OpenRouterFallbackModels =
    [
        new("deepseek/deepseek-v4-pro", "DeepSeek V4 Pro", 1_048_576, null, true),
        new("deepseek/deepseek-v4-flash", "DeepSeek V4 Flash", 1_048_576, null, true),
        new("openrouter/auto", "OpenRouter Auto", null, null, false)
    ];

    public static AiModelMetadata? FindKnownModel(string providerType, string baseUrl, string model)
    {
        var catalog = GetKnownModels(providerType, baseUrl);
        return catalog.FirstOrDefault(item => item.Id.Equals(model, StringComparison.OrdinalIgnoreCase));
    }

    public static IReadOnlyList<AiModelMetadata> GetKnownModels(string providerType, string baseUrl)
    {
        if (IsDeepSeekProvider(providerType, baseUrl))
        {
            return DeepSeekModels;
        }

        if (IsOpenRouterProvider(providerType, baseUrl))
        {
            return OpenRouterFallbackModels;
        }

        if (IsOpenAiProvider(providerType, baseUrl))
        {
            return OpenAiModels;
        }

        if (IsAnthropicProvider(providerType, baseUrl))
        {
            return AnthropicModels;
        }

        return [];
    }

    public static bool IsDeepSeekProvider(string providerType, string baseUrl)
    {
        return providerType.Equals("DeepSeek", StringComparison.OrdinalIgnoreCase) ||
            baseUrl.Contains("api.deepseek.com", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsOpenAiProvider(string providerType, string baseUrl)
    {
        return providerType.Equals("OpenAI", StringComparison.OrdinalIgnoreCase) ||
            baseUrl.Contains("api.openai.com", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsAnthropicProvider(string providerType, string baseUrl)
    {
        return providerType.Equals("Anthropic", StringComparison.OrdinalIgnoreCase) ||
            baseUrl.Contains("api.anthropic.com", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsOpenRouterProvider(string providerType, string baseUrl)
    {
        return providerType.Equals("OpenRouter", StringComparison.OrdinalIgnoreCase) ||
            baseUrl.Contains("openrouter.ai", StringComparison.OrdinalIgnoreCase);
    }

    public static int EstimateTokens(IEnumerable<string> textParts)
    {
        var chars = textParts.Where(part => !string.IsNullOrEmpty(part)).Sum(part => part.Length);
        return Math.Max(0, (int)Math.Ceiling(chars / 4.0));
    }

    public static string FormatTokenCount(int? tokens)
    {
        if (!tokens.HasValue)
        {
            return "unknown";
        }

        var value = tokens.Value;
        if (value >= 1_000_000)
        {
            return $"{value / 1_000_000d:0.#}M";
        }

        if (value >= 1_000)
        {
            return $"{value / 1_000d:0.#}K";
        }

        return value.ToString();
    }

    public static IReadOnlyList<AiModelMetadata> ParseOpenRouterModels(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var models = new List<AiModelMetadata>();
        foreach (var item in data.EnumerateArray())
        {
            var id = item.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            var name = item.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : id;
            int? contextLength = item.TryGetProperty("context_length", out var contextElement) && contextElement.TryGetInt32(out var context)
                ? context
                : null;
            int? maxOutput = null;
            if (item.TryGetProperty("top_provider", out var provider) &&
                provider.ValueKind == JsonValueKind.Object &&
                provider.TryGetProperty("max_completion_tokens", out var maxElement) &&
                maxElement.TryGetInt32(out var max))
            {
                maxOutput = max;
            }

            var supportsThinking = false;
            if (item.TryGetProperty("supported_parameters", out var supported) && supported.ValueKind == JsonValueKind.Array)
            {
                supportsThinking = supported.EnumerateArray()
                    .Select(value => value.GetString())
                    .Any(value => string.Equals(value, "reasoning", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(value, "include_reasoning", StringComparison.OrdinalIgnoreCase));
            }

            models.Add(new AiModelMetadata(id, name ?? id, contextLength, maxOutput, supportsThinking));
        }

        return models;
    }

    public static IReadOnlyList<AiModelMetadata> ParseOpenAiModels(string json, bool isLocalOrCustom = false)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var known = OpenAiModels.ToDictionary(model => model.Id, StringComparer.OrdinalIgnoreCase);
        var models = new List<AiModelMetadata>();
        foreach (var item in data.EnumerateArray())
        {
            var id = item.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
            if (string.IsNullOrWhiteSpace(id) || !IsLikelyTextGenerationModel(id, isLocalOrCustom))
            {
                continue;
            }

            models.Add(known.TryGetValue(id, out var metadata)
                ? metadata
                : new AiModelMetadata(id, id));
        }

        return models
            .OrderBy(model => model.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsLikelyTextGenerationModel(string id, bool isLocalOrCustom = false)
    {
        if (id.Contains("embedding", StringComparison.OrdinalIgnoreCase) ||
            id.Contains("moderation", StringComparison.OrdinalIgnoreCase) ||
            id.Contains("image", StringComparison.OrdinalIgnoreCase) ||
            id.Contains("sora", StringComparison.OrdinalIgnoreCase) ||
            id.Contains("tts", StringComparison.OrdinalIgnoreCase) ||
            id.Contains("transcribe", StringComparison.OrdinalIgnoreCase) ||
            id.Contains("whisper", StringComparison.OrdinalIgnoreCase) ||
            id.Contains("realtime", StringComparison.OrdinalIgnoreCase) ||
            id.Contains("audio", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (isLocalOrCustom)
        {
            return true;
        }

        return id.StartsWith("gpt-", StringComparison.OrdinalIgnoreCase) ||
            id.StartsWith("o", StringComparison.OrdinalIgnoreCase) ||
            id.StartsWith("computer-use", StringComparison.OrdinalIgnoreCase);
    }
}
