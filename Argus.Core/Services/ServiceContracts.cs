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
}

public interface IMemoryService
{
    Task<Memory> SaveMemoryAsync(string text, string source, int importance, Guid? linkedNodeId = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Memory>> SearchMemoriesAsync(string query, int take = 20, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Memory>> RecallAsync(string query, int take = 10, CancellationToken cancellationToken = default);
}

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
    string? Model = null);

public interface IAiChatService
{
    Task<AiChatResult> SendAsync(AiProviderProfile? profile, IReadOnlyList<AiChatTurn> messages, CancellationToken cancellationToken = default);
    Task<float[]?> GenerateEmbeddingAsync(AiProviderProfile? profile, string text, CancellationToken cancellationToken = default);
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

public interface IToolService
{
    Task<IReadOnlyList<string>> ListToolsAsync(CancellationToken cancellationToken = default);
    Task<string> ExecuteToolAsync(string toolName, string argumentsJson, CancellationToken cancellationToken = default);
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
