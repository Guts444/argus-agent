namespace Argus.Core.Models;

public sealed class AiProviderProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string ProviderType { get; set; } = "OpenAICompatible";
    public string BaseUrl { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string ApiKeyStorageKey { get; set; } = string.Empty;
    public string ThinkingMode { get; set; } = "disabled";
    public string ReasoningEffort { get; set; } = "high";
    public string OrganizationId { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
    public bool IsDefault { get; set; }
}
