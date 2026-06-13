using Argus.Core.Graph;
using Argus.Core.Models;

namespace Argus.Core.Services;

public interface IGraphService
{
    Task<GraphSnapshot> GetGraphAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Node>> SearchNodesAsync(string query, CancellationToken cancellationToken = default);
    Task<Node> CreateNodeAsync(Node node, CancellationToken cancellationToken = default);
    Task<Node> UpdateNodeAsync(Node node, CancellationToken cancellationToken = default);
    Task DeleteNodeAsync(Guid nodeId, CancellationToken cancellationToken = default);
    Task<Edge> CreateEdgeAsync(Guid sourceNodeId, Guid targetNodeId, string relationshipType, double strength, CancellationToken cancellationToken = default);
    Task DeleteEdgeAsync(Guid edgeId, CancellationToken cancellationToken = default);
    Task SaveNodePositionAsync(Guid nodeId, double x, double y, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<NodeConnection>> GetConnectionsAsync(Guid nodeId, CancellationToken cancellationToken = default);
    Task<DashboardSnapshot> GetDashboardAsync(CancellationToken cancellationToken = default);
}

public interface ITagService
{
    Task<IReadOnlyList<Tag>> GetTagsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Tag>> GetNodeTagsAsync(Guid nodeId, CancellationToken cancellationToken = default);
    Task<Tag> UpsertTagAsync(string name, string colorKey = "cyan", CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Tag>> SetNodeTagsAsync(Guid nodeId, IEnumerable<string> tagNames, CancellationToken cancellationToken = default);
    Task AddTagToNodeAsync(Guid nodeId, string tagName, string colorKey = "cyan", CancellationToken cancellationToken = default);
    Task RemoveTagFromNodeAsync(Guid nodeId, string tagName, CancellationToken cancellationToken = default);
    Task DeleteTagAsync(Guid tagId, CancellationToken cancellationToken = default);
}

public interface IGraphExchangeService
{
    Task<string> ExportJsonAsync(bool includeArchived = false, CancellationToken cancellationToken = default);
    Task<GraphSnapshot> ImportJsonAsync(string json, GraphImportMode mode = GraphImportMode.Merge, CancellationToken cancellationToken = default);
}

public interface IConversationService
{
    Task<IReadOnlyList<Conversation>> GetConversationsAsync(CancellationToken cancellationToken = default);
    Task<Conversation> CreateConversationAsync(string title, CancellationToken cancellationToken = default);
    Task<Message> AddMessageAsync(Guid conversationId, string role, string content, Guid? linkedNodeId = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Message>> GetMessagesAsync(Guid conversationId, CancellationToken cancellationToken = default);
    Task DeleteMessageAsync(Guid messageId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MessageSearchResult>> SearchMessagesAsync(string query, int take = 20, CancellationToken cancellationToken = default);
}

public sealed record MessageSearchResult(
    Guid MessageId,
    Guid ConversationId,
    string ConversationTitle,
    string Role,
    string Content,
    string Snippet,
    DateTimeOffset MessageCreatedAt);

public interface IMemoryService
{
    Task<Memory> SaveMemoryAsync(string text, string source, int importance, Guid? linkedNodeId = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Memory>> SearchMemoriesAsync(string query, int take = 20, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Memory>> RecallAsync(string query, int take = 10, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MemoryRecallResult>> RecallWithDetailsAsync(string query, int take = 10, CancellationToken cancellationToken = default);
    Task<MemoryRecallFeedback> SaveRecallFeedbackAsync(
        string query,
        Guid memoryId,
        string rating,
        CancellationToken cancellationToken = default);
}

public enum MemoryRecallMethod
{
    Recent,
    ExactPhrase,
    Keyword,
    Semantic,
    Hybrid
}

public sealed record MemoryRecallResult(
    Memory Memory,
    double Score,
    MemoryRecallMethod Method,
    double SemanticScore,
    double LexicalScore,
    double ImportanceScore,
    double RecencyScore,
    string Explanation);

public interface IMemoryProvider
{
    Task<IReadOnlyList<Memory>> RecallAsync(string query, int take = 10, CancellationToken cancellationToken = default);
}

public interface ISettingsService
{
    Task<IReadOnlyList<AiProviderProfile>> GetAiProviderProfilesAsync(CancellationToken cancellationToken = default);
    Task<AiProviderProfile?> GetDefaultAiProviderProfileAsync(CancellationToken cancellationToken = default);
    Task<AiProviderProfile> SaveAiProviderProfileAsync(AiProviderProfile profile, CancellationToken cancellationToken = default);
    Task<string?> GetSettingAsync(string key, string? defaultValue = null, CancellationToken cancellationToken = default);
    Task SaveSettingAsync(string key, string value, CancellationToken cancellationToken = default);
}

public interface ISecretStore
{
    Task<string?> GetSecretAsync(string key, CancellationToken cancellationToken = default);
    Task SetSecretAsync(string key, string value, CancellationToken cancellationToken = default);
    Task RemoveSecretAsync(string key, CancellationToken cancellationToken = default);
}

public sealed record AiChatTurn(string Role, string Content);

public sealed record AiChatResult(
    string Content,
    bool SetupRequired = false,
    string? Error = null,
    string? ReasoningContent = null,
    int? PromptTokens = null,
    int? CompletionTokens = null,
    int? TotalTokens = null,
    string? Model = null,
    int? ContextWindowTokens = null);

public interface IAiChatService
{
    Task<AiChatResult> SendAsync(AiProviderProfile? profile, IReadOnlyList<AiChatTurn> messages, CancellationToken cancellationToken = default);
    Task<float[]?> GenerateEmbeddingAsync(AiProviderProfile? profile, string text, CancellationToken cancellationToken = default);
}

public enum AiProviderKind
{
    OpenAiCompatible,
    Local,
    DeepSeek,
    OpenAi,
    OpenAiCodex,
    OpenRouter,
    Anthropic
}

public enum AiAuthenticationMode
{
    ApiKey,
    CodexAccount,
    LocalOptional
}

public sealed record AiProviderCapabilities(
    string AdapterId,
    AiProviderKind Kind,
    AiAuthenticationMode AuthenticationMode,
    bool SupportsDynamicModels,
    bool SupportsEmbeddings,
    bool SupportsThinkingToggle,
    bool ReasoningAlwaysEnabled,
    string AuthenticationHelpText,
    string ReasoningHelpText);

public sealed record AiProviderConnectionStatus(
    bool IsConfigured,
    string Message,
    bool UsesEnvironmentCredential = false);

public interface IAiProviderAdapter
{
    string Id { get; }
    bool CanHandle(AiProviderProfile profile);
    AiProviderCapabilities GetCapabilities(AiProviderProfile profile);
    IReadOnlyList<AiModelMetadata> GetFallbackModels(AiProviderProfile profile);
    Task<IReadOnlyList<AiModelMetadata>> ListModelsAsync(
        AiProviderProfile profile,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default);
    Task<AiProviderConnectionStatus> GetConnectionStatusAsync(
        AiProviderProfile profile,
        CancellationToken cancellationToken = default);
    Task<AiChatResult> SendAsync(
        AiProviderProfile profile,
        IReadOnlyList<AiChatTurn> messages,
        CancellationToken cancellationToken = default);
    Task<float[]?> GenerateEmbeddingAsync(
        AiProviderProfile profile,
        string text,
        CancellationToken cancellationToken = default);
}

public interface IAiProviderRegistry
{
    AiProviderCapabilities GetCapabilities(AiProviderProfile profile);
    IReadOnlyList<AiModelMetadata> GetFallbackModels(AiProviderProfile profile);
    Task<IReadOnlyList<AiModelMetadata>> ListModelsAsync(
        AiProviderProfile profile,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default);
    Task<AiProviderConnectionStatus> GetConnectionStatusAsync(
        AiProviderProfile profile,
        CancellationToken cancellationToken = default);
}

public sealed record OpenAiCodexAccount(
    bool CliAvailable,
    bool IsAuthenticated,
    string Status,
    string? Email = null,
    string? PlanType = null);

public sealed record OpenAiCodexLoginStart(
    bool Started,
    string Status,
    string? LoginId = null,
    string? AuthorizationUrl = null);

public interface IOpenAiCodexService
{
    Task<OpenAiCodexAccount> GetAccountAsync(CancellationToken cancellationToken = default);
    Task<OpenAiCodexLoginStart> StartLoginAsync(CancellationToken cancellationToken = default);
    Task<OpenAiCodexAccount> CompleteLoginAsync(
        string loginId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
    Task LogoutAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AiModelMetadata>> ListModelsAsync(CancellationToken cancellationToken = default);
    Task<AiChatResult> SendAsync(
        AiProviderProfile profile,
        IReadOnlyList<AiChatTurn> messages,
        CancellationToken cancellationToken = default);
}

public interface IAgentService
{
    Task<string> RunAsync(string instruction, CancellationToken cancellationToken = default);
    Task<AgentPlan> PlanAsync(string instruction, CancellationToken cancellationToken = default);
    Task<(string FinalAnswer, string ExecutionLog)> RunWithDetailsAsync(string instruction, Guid? conversationId = null, CancellationToken cancellationToken = default);
}

public sealed record AgentActionProposal(
    string ActionType,
    string Title,
    string Description,
    Guid? NodeId = null,
    string? Payload = null);

public sealed record AgentPlan(
    string Instruction,
    IReadOnlyList<Node> MatchingNodes,
    IReadOnlyList<Memory> MatchingMemories,
    IReadOnlyList<AgentActionProposal> ProposedActions);

public enum ToolRiskLevel
{
    ReadOnly,
    Mutating,
    Destructive
}

public sealed record ToolDefinition(
    string Name,
    string Description,
    ToolRiskLevel RiskLevel,
    string ArgumentSchemaJson = "{}");

public sealed record ToolArgumentValidationResult(
    bool IsValid,
    string NormalizedArgumentsJson,
    IReadOnlyList<string> Errors);

public sealed record ToolExecutionRequest(
    string ToolName,
    string ArgumentsJson,
    Guid? AgentRunId = null,
    Guid? ConversationId = null,
    string ApprovalStatus = "not_required",
    Guid? ExecutionId = null);

public sealed record ToolExecutionResult(
    Guid ExecutionId,
    bool Succeeded,
    bool ValidationFailed,
    string ResultJson,
    string? Error,
    long DurationMilliseconds);

public sealed record ToolApprovalRequest(
    string ToolName,
    string Description,
    ToolRiskLevel RiskLevel,
    string ArgumentsJson);

public sealed record ToolApprovalDecision(
    bool Approved,
    string? Reason = null);

public interface IToolApprovalService
{
    Task<ToolApprovalDecision> RequestApprovalAsync(
        ToolApprovalRequest request,
        CancellationToken cancellationToken = default);
}

public interface IToolService
{
    Task<IReadOnlyList<string>> ListToolsAsync(CancellationToken cancellationToken = default);
    ToolDefinition? GetToolDefinition(string toolName);
    ToolArgumentValidationResult ValidateArguments(string toolName, string argumentsJson);
    Task<ToolExecutionResult> ExecuteToolAsync(
        ToolExecutionRequest request,
        CancellationToken cancellationToken = default);
    Task<string> ExecuteToolAsync(string toolName, string argumentsJson, CancellationToken cancellationToken = default);
}

public interface IToolExecutionAuditService
{
    Task RecordAsync(
        ToolExecutionAudit audit,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ToolExecutionAudit>> GetRecentAsync(
        int take = 100,
        CancellationToken cancellationToken = default);
    Task<ToolExecutionAudit?> GetByExecutionIdAsync(Guid executionId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ToolExecutionAudit>> GetFilteredAsync(
        string? toolName = null,
        string? riskLevel = null,
        string? approvalStatus = null,
        string? outcome = null,
        bool? onlyIncomplete = null,
        int take = 100,
        CancellationToken cancellationToken = default);
    Task<int> PruneOldAuditsAsync(DateTimeOffset before, CancellationToken cancellationToken = default);
    Task<int> ClearAllAuditsAsync(CancellationToken cancellationToken = default);
}

public interface ITelegramGatewayService
{
    bool IsRunning { get; }
    string Status { get; }
    string GetLogs();
    event Action? OnStatusChanged;
    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}

public interface ISoulService
{
    string SoulPath { get; }
    Task<string> ReadSoulAsync(CancellationToken cancellationToken = default);
    Task SaveSoulAsync(string content, CancellationToken cancellationToken = default);
}

public interface IProjectContextService
{
    Task<IReadOnlyList<ProjectContext>> ScanProjectsAsync(CancellationToken cancellationToken = default);
    Task<ProjectContext?> GetProjectContextAsync(string nodeTitle, CancellationToken cancellationToken = default);
}
