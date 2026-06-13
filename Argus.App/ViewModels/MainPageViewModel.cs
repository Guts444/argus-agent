using System.Collections.ObjectModel;
using System.Reflection;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Argus.Core.Graph;
using Argus.Core.Models;
using Argus.Core.Services;
using Argus.AI.Services;
using Argus.App.Services;
using Argus.Data.Services;
using Microsoft.Extensions.DependencyInjection;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace Argus.App.ViewModels;

public partial class MainPageViewModel(
    IGraphService graphService,
    ITagService tagService,
    IGraphExchangeService graphExchangeService,
    ISoulService soulService,
    IProjectContextService projectContextService,
    IConversationService conversationService,
    IMemoryService memoryService,
    ISettingsService settingsService,
    IOpenAiCodexService openAiCodexService,
    IAiProviderRegistry aiProviderRegistry,
    IAiChatService aiChatService,
    IAgentService agentService,
    IToolService toolService,
    ITelegramGatewayService telegramGatewayService,
    IAppUpdateService appUpdateService,
    ISecretStore secretStore,
    IToolExecutionAuditService auditService,
    IProjectDashboardService projectDashboardService,
    DatabaseBackupService databaseBackupService) : ObservableObject
{
    private bool initialized;
    private bool suppressSelectedModelUpdate;
    private bool suppressReasoningEffortUpdate;
    private bool suppressSelectedProviderSideEffects;
    private long modelLoadVersion;
    private CancellationTokenSource? selectedNodeRefreshCancellation;
    private readonly SemaphoreSlim providerPreferenceSaveLock = new(1, 1);
    private readonly List<CommandPaletteItem> paletteItems = new();
    private readonly Dictionary<string, AiModelMetadata> modelMetadata = new(StringComparer.OrdinalIgnoreCase);

    public ObservableCollection<Node> Nodes { get; } = new();
    public ObservableCollection<Edge> Edges { get; } = new();
    public ObservableCollection<Node> SearchResults { get; } = new();
    public ObservableCollection<NodeConnection> Connections { get; } = new();
    public ObservableCollection<Conversation> Conversations { get; } = new();
    public ObservableCollection<Message> ChatMessages { get; } = new();
    public ObservableCollection<MemoryRecallDisplayItem> MemoryRecallResults { get; } = new();
    public ObservableCollection<AiProviderProfile> ProviderProfiles { get; } = new();
    public ObservableCollection<string> AvailableTools { get; } = new();
    public ObservableCollection<string> ModelOptions { get; } = new();
    public ObservableCollection<CommandPaletteItem> CommandPaletteItems { get; } = new();
    public ObservableCollection<Tag> SelectedNodeTags { get; } = new();
    public ObservableCollection<ProjectContext> ProjectContexts { get; } = new();
    public ObservableCollection<string> ReasoningEffortOptions { get; } = new();

    public IReadOnlyList<string> NodeTypes { get; } = new[]
    {
        "Project", "Idea", "Task", "Decision", "Note", "Person", "File", "Link", "Conversation", "Memory", "Tool", "Agent"
    };

    public IReadOnlyList<string> RelationshipTypes { get; } = new[]
    {
        "related_to", "depends_on", "inspired_by", "belongs_to", "blocked_by", "uses", "created_from", "discussed_in", "decided_in", "reminds_me_of"
    };

    public IReadOnlyList<string> ThinkingModeOptions { get; } = new[] { "enabled", "disabled" };

    public string VersionDisplayText { get; } = BuildVersionDisplayText();

    [ObservableProperty]
    public partial string UpdateButtonText { get; set; } = BuildVersionDisplayText();

    [ObservableProperty]
    public partial string UpdateStatusText { get; set; } = "Checking for updates...";

    [ObservableProperty]
    public partial string UpdateReleaseNotes { get; set; } = "Argus checks GitHub Releases and verifies the installer SHA-256 digest before launch.";

    [ObservableProperty]
    public partial bool IsUpdateAvailable { get; set; }

    [ObservableProperty]
    public partial bool IsUpdateBusy { get; set; }

    private AppUpdateInfo? availableUpdate;

    [ObservableProperty]
    public partial string CurrentView { get; set; } = "Dashboard";

    [ObservableProperty]
    public partial string GraphFilterType { get; set; } = "All";

    public string GraphFilterLabel => GraphFilterType == "All" ? "All nodes" : $"{GraphFilterType} nodes";

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string MemorySearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string MemoryDebuggerStatusText { get; set; } =
        "Enter a question to inspect which memories Argus recalls and why.";

    [ObservableProperty]
    public partial bool IsMemoryDebuggerBusy { get; set; }

    [ObservableProperty]
    public partial string ChatInput { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ApiKeyInput { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ApiKeyPlaceholder { get; set; } = "Enter API key";

    [ObservableProperty]
    public partial string TelegramTokenPlaceholder { get; set; } = "Telegram bot token";

    [ObservableProperty]
    public partial string TagEditorText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SoulText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SoulPath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ProjectContextText { get; set; } = "Select a project node to inspect README, GitHub remote, and working tree state.";

    [ObservableProperty]
    public partial string StatusText { get; set; } = "Local SQLite memory is ready.";

    [ObservableProperty]
    public partial string SettingsStatus { get; set; } = "LLM credentials are stored with Windows Credential Locker.";

    [ObservableProperty]
    public partial string ContextTrackerText { get; set; } = "0/unknown";

    [ObservableProperty]
    public partial string ContextTrackerPercentText { get; set; } = "0%";

    [ObservableProperty]
    public partial double ContextTrackerPercent { get; set; }

    [ObservableProperty]
    public partial string ContextTrackerDetailText { get; set; } = "No model usage yet.";

    [ObservableProperty]
    public partial string ContextBreakdownText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool ShowThinkingInApp { get; set; }

    [ObservableProperty]
    public partial bool ShowThinkingInTelegram { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial bool IsWebSearchEnabled { get; set; }

    [ObservableProperty]
    public partial Node? SelectedNode { get; set; }

    [ObservableProperty]
    public partial Node? EdgeTargetNode { get; set; }

    [ObservableProperty]
    public partial string NewEdgeRelationship { get; set; } = "related_to";

    [ObservableProperty]
    public partial double NewEdgeStrength { get; set; } = 0.7;

    [ObservableProperty]
    public partial Conversation? SelectedConversation { get; set; }

    [ObservableProperty]
    public partial string ConversationSearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsSearchingConversations { get; set; }

    public ObservableCollection<MessageSearchResult> ConversationSearchResults { get; } = new();

    public bool HasConversationSearchResults => ConversationSearchResults.Count > 0;

    public bool HasConversations => Conversations.Count > 0;

    public bool HasSearchResults => SearchResults.Count > 0;

    public bool HasMemoryResults => MemoryRecallResults.Count > 0;

    public bool ShowProjectNextActionsWidget =>
        ShowProjectsWidget && ProjectCockpit?.HasGlobalNextActions == true;

    [ObservableProperty]
    public partial AiProviderProfile? SelectedProvider { get; set; }

    private AiProviderCapabilities? SelectedProviderCapabilities =>
        SelectedProvider is null ? null : aiProviderRegistry.GetCapabilities(SelectedProvider);

    public bool IsOpenAiCodexProvider =>
        SelectedProviderCapabilities?.Kind == AiProviderKind.OpenAiCodex;

    public bool IsAnthropicProvider =>
        SelectedProviderCapabilities?.Kind == AiProviderKind.Anthropic;

    public bool IsApiKeyRequired =>
        SelectedProviderCapabilities?.AuthenticationMode == AiAuthenticationMode.ApiKey;

    public string ProviderAuthenticationHelpText =>
        SelectedProviderCapabilities?.AuthenticationHelpText ??
        "Select an LLM provider to see its authentication requirements.";

    [ObservableProperty]
    public partial string SelectedModel { get; set; } = string.Empty;

    [ObservableProperty]
    public partial DashboardSnapshot? Dashboard { get; set; }

    [ObservableProperty]
    public partial CoherentDashboard? ProjectCockpit { get; set; }

    public ObservableCollection<ProjectDashboardCard> ProjectCockpitCards { get; } = new();

    [ObservableProperty]
    public partial DashboardWidgetsViewModel? DashboardWidgets { get; set; }

    [ObservableProperty]
    public partial bool IsCommandPaletteOpen { get; set; }

    [ObservableProperty]
    public partial string CommandPaletteQuery { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ProjectsRootPath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool TelegramBotEnabled { get; set; }

    [ObservableProperty]
    public partial string TelegramBotToken { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string TelegramAllowedUserIds { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool TelegramUseWebhook { get; set; }

    [ObservableProperty]
    public partial string TelegramWebhookUrl { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string TelegramWebhookPort { get; set; } = "8080";

    [ObservableProperty]
    public partial string TelegramGatewayStatusText { get; set; } = "Stopped";

    [ObservableProperty]
    public partial string TelegramGatewayLogsText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial CommandPaletteItem? SelectedCommandPaletteItem { get; set; }

    public bool HasSelectedNode => SelectedNode is not null;
    public bool HasReasoningEffortOptions => ReasoningEffortOptions.Count > 0;
    public bool CanToggleThinkingMode =>
        SelectedProviderCapabilities?.SupportsThinkingToggle == true;
    public bool HasReasoningControls => CanToggleThinkingMode || HasReasoningEffortOptions;
    public bool ShowReasoningEffortPicker =>
        HasReasoningEffortOptions && (!CanToggleThinkingMode || IsThinkingEnabled);

    public string ReasoningCapabilityHelpText =>
        SelectedProviderCapabilities?.ReasoningHelpText ?? string.Empty;

    public string SelectedReasoningEffort
    {
        get => SelectedProvider?.ReasoningEffort ?? string.Empty;
        set
        {
            if (suppressReasoningEffortUpdate ||
                SelectedProvider is null ||
                string.IsNullOrWhiteSpace(value) ||
                string.Equals(SelectedProvider.ReasoningEffort, value, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            SelectedProvider.ReasoningEffort = value;
            if (!CanToggleThinkingMode)
            {
                SelectedProvider.ThinkingMode = value.Equals("none", StringComparison.OrdinalIgnoreCase)
                    ? "disabled"
                    : "enabled";
            }
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsThinkingEnabled));
            OnPropertyChanged(nameof(ShowReasoningEffortPicker));
            _ = SaveProviderPreferenceAsync(SelectedProvider);
        }
    }

    partial void OnSelectedNodeChanged(Node? value)
    {
        OnPropertyChanged(nameof(HasSelectedNode));
        OnPropertyChanged(nameof(IsSelectedNodePinned));
        selectedNodeRefreshCancellation?.Cancel();
        selectedNodeRefreshCancellation?.Dispose();
        selectedNodeRefreshCancellation = new CancellationTokenSource();
        _ = RefreshSelectedNodeAsync(value, selectedNodeRefreshCancellation.Token);
    }

    private readonly HashSet<Guid> pinnedNodeIds = new();

    public bool IsSelectedNodePinned
    {
        get => SelectedNode is not null && pinnedNodeIds.Contains(SelectedNode.Id);
        set
        {
            if (SelectedNode is null) return;
            if (value)
            {
                pinnedNodeIds.Add(SelectedNode.Id);
            }
            else
            {
                pinnedNodeIds.Remove(SelectedNode.Id);
            }
            OnPropertyChanged(nameof(IsSelectedNodePinned));
            _ = SavePinnedNodeIdsAsync();
            OnPropertyChanged(nameof(SelectedNode));
        }
    }

    public bool IsNodePinned(Guid nodeId) => pinnedNodeIds.Contains(nodeId);

    private async Task SavePinnedNodeIdsAsync()
    {
        var str = string.Join(",", pinnedNodeIds);
        await settingsService.SaveSettingAsync("PinnedNodeIds", str);
    }

    public Task SaveSettingAsync(string key, string value) => settingsService.SaveSettingAsync(key, value);
    public Task<string?> GetSettingAsync(string key, string? defaultValue = null) => settingsService.GetSettingAsync(key, defaultValue);

    partial void OnSelectedConversationChanged(Conversation? value)
    {
        _ = LoadConversationMessagesAsync(value);
    }

    partial void OnCommandPaletteQueryChanged(string value)
    {
        FilterCommandPalette();
    }

    partial void OnGraphFilterTypeChanged(string value)
    {
        OnPropertyChanged(nameof(GraphFilterLabel));
    }

    [RelayCommand]
    private async Task InitializeAsync()
    {
        if (initialized)
        {
            return;
        }

        initialized = true;
        ChatMessages.CollectionChanged += (s, e) => OnPropertyChanged(nameof(IsChatEmpty));
        await LoadAllAsync();
    }

    [RelayCommand]
    private void ShowDashboard()
    {
        CurrentView = "Dashboard";
        StartDashboardWidgets();
    }

    [RelayCommand]
    private void ShowGraph()
    {
        GraphFilterType = "All";
        CurrentView = "Graph";
        OnPropertyChanged(nameof(GraphFilterLabel));
    }

    [RelayCommand]
    private void ShowProjects() => ApplyGraphFilter("Project");

    [RelayCommand]
    private void ShowIdeas() => ApplyGraphFilter("Idea");

    [RelayCommand]
    private void ShowTasks() => ApplyGraphFilter("Task");

    [RelayCommand]
    private void ClearGraphFilter() => ShowGraph();

    [RelayCommand]
    private void ShowConversations() => CurrentView = "Conversations";
    [RelayCommand]
    private async Task SearchConversationsAsync()
    {
        if (string.IsNullOrWhiteSpace(ConversationSearchText))
        {
            ConversationSearchResults.Clear();
            OnPropertyChanged(nameof(HasConversationSearchResults));
            return;
        }

        IsSearchingConversations = true;
        try
        {
            Replace(ConversationSearchResults, await conversationService.SearchMessagesAsync(ConversationSearchText));
            OnPropertyChanged(nameof(HasConversationSearchResults));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Conversation search error: {ex.Message}");
        }
        finally
        {
            IsSearchingConversations = false;
        }
    }

    [RelayCommand]
    private void ShowMemories() => CurrentView = "Memories";

    [RelayCommand]
    private void ShowSkills() => CurrentView = "Skills";

    [RelayCommand]
    private void ShowSettings() => CurrentView = "Settings";

    [RelayCommand]
    private void OpenCommandPalette()
    {
        EnsurePaletteItems();
        CommandPaletteQuery = string.Empty;
        FilterCommandPalette();
        SelectedCommandPaletteItem = CommandPaletteItems.FirstOrDefault();
        IsCommandPaletteOpen = true;
    }

    [RelayCommand]
    private void CloseCommandPalette()
    {
        IsCommandPaletteOpen = false;
    }

    [RelayCommand]
    private async Task ExecuteCommandPaletteItemAsync(CommandPaletteItem? item)
    {
        item ??= SelectedCommandPaletteItem;
        if (item is null)
        {
            return;
        }

        IsCommandPaletteOpen = false;
        await item.ExecuteAsync();
    }

    [RelayCommand]
    private async Task RefreshGraphAsync()
    {
        await LoadGraphAsync();
        await LoadDashboardAsync();
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        var results = await graphService.SearchNodesAsync(SearchText);
        Replace(SearchResults, results);
        OnPropertyChanged(nameof(HasSearchResults));
        CurrentView = "Graph";
        StatusText = results.Count == 0 ? "No graph matches found." : $"{results.Count} graph matches found.";
    }

    [RelayCommand]
    private async Task SearchMemoriesAsync()
    {
        if (IsMemoryDebuggerBusy)
        {
            return;
        }

        IsMemoryDebuggerBusy = true;
        MemoryDebuggerStatusText = string.IsNullOrWhiteSpace(MemorySearchText)
            ? "Loading recent memories..."
            : "Evaluating memory recall...";
        try
        {
            var results = await memoryService.RecallWithDetailsAsync(MemorySearchText, 50);
            Replace(MemoryRecallResults, results.Select(result => new MemoryRecallDisplayItem(result)));
            OnPropertyChanged(nameof(HasMemoryResults));
            MemoryDebuggerStatusText = results.Count == 0
                ? "No memories were recalled. Try different wording or save a relevant memory first."
                : string.IsNullOrWhiteSpace(MemorySearchText)
                    ? $"Showing {results.Count} recent memories ranked by importance and recency."
                    : $"Recalled {results.Count} memories. Scores show the retrieval evidence used for this query.";
            StatusText = results.Count == 0
                ? "No memories recalled."
                : $"{results.Count} memories recalled with explanations.";
        }
        catch (Exception ex)
        {
            MemoryDebuggerStatusText = $"Memory evaluation failed: {ex.Message}";
            StatusText = "Memory evaluation failed.";
        }
        finally
        {
            IsMemoryDebuggerBusy = false;
        }
    }

    [RelayCommand]
    private async Task ShowRecentMemoriesAsync()
    {
        MemorySearchText = string.Empty;
        await SearchMemoriesAsync();
    }

    [RelayCommand]
    private Task MarkMemoryUsefulAsync(MemoryRecallDisplayItem? item)
    {
        return SaveMemoryRecallFeedbackAsync(item, "useful", "Marked useful.");
    }

    [RelayCommand]
    private Task MarkMemoryNotRelevantAsync(MemoryRecallDisplayItem? item)
    {
        return SaveMemoryRecallFeedbackAsync(item, "not_relevant", "Marked not relevant.");
    }

    private async Task SaveMemoryRecallFeedbackAsync(
        MemoryRecallDisplayItem? item,
        string rating,
        string confirmation)
    {
        if (item is null)
        {
            return;
        }

        try
        {
            await memoryService.SaveRecallFeedbackAsync(
                MemorySearchText,
                item.Recall.Memory.Id,
                rating);
            item.FeedbackText = confirmation;
            MemoryDebuggerStatusText =
                "Feedback saved locally. It is available for retrieval evaluation and future ranking improvements.";
        }
        catch (Exception ex)
        {
            MemoryDebuggerStatusText = $"Could not save recall feedback: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task NewNodeAsync()
    {
        var node = await graphService.CreateNodeAsync(new Node
        {
            Title = "Untitled node",
            Type = "Idea",
            Summary = "New thought",
            Body = string.Empty,
            Status = "Active",
            Importance = 3,
            ColorKey = "cyan",
            IconKey = "idea"
        });

        await LoadGraphAsync();
        SelectedNode = Nodes.FirstOrDefault(existing => existing.Id == node.Id);
        CurrentView = "Graph";
    }

    public async Task CreateNodeAtAsync(double x, double y)
    {
        var node = await graphService.CreateNodeAsync(new Node
        {
            Title = "New node",
            Type = "Idea",
            Summary = "Created from graph canvas",
            Status = "Active",
            Importance = 3,
            ColorKey = "magenta",
            IconKey = "idea",
            PositionX = x,
            PositionY = y
        });

        await LoadGraphAsync();
        SelectedNode = Nodes.FirstOrDefault(existing => existing.Id == node.Id);
    }

    public async Task CreateEdgeBetweenAsync(Node source, Node target)
    {
        if (source.Id == target.Id)
        {
            return;
        }

        SelectedNode = source;
        EdgeTargetNode = target;
        await graphService.CreateEdgeAsync(source.Id, target.Id, NewEdgeRelationship, NewEdgeStrength);
        await LoadGraphAsync();
        await LoadConnectionsAsync(source.Id);
        StatusText = $"Connected {source.Title} to {target.Title}.";
    }

    public async Task PersistNodePositionsAsync(IEnumerable<Node> nodes)
    {
        foreach (var node in nodes)
        {
            await SaveNodePositionAsync(node);
        }

        StatusText = "Graph layout saved.";
    }

    [RelayCommand]
    private async Task SaveSelectedNodeAsync()
    {
        if (SelectedNode is null)
        {
            return;
        }

        await graphService.UpdateNodeAsync(SelectedNode);
        await LoadGraphAsync();
        SelectedNode = Nodes.FirstOrDefault(node => node.Id == SelectedNode.Id);
        StatusText = "Node saved.";
    }

    [RelayCommand]
    private async Task SaveSelectedNodeTagsAsync()
    {
        if (SelectedNode is null)
        {
            return;
        }

        var tagNames = TagEditorText
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(tag => tag.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        Replace(SelectedNodeTags, await tagService.SetNodeTagsAsync(SelectedNode.Id, tagNames));
        TagEditorText = string.Join(", ", SelectedNodeTags.Select(tag => tag.Name));
        StatusText = "Tags saved.";
    }

    [RelayCommand]
    private async Task DeleteSelectedNodeAsync()
    {
        if (SelectedNode is null)
        {
            return;
        }

        var deletedId = SelectedNode.Id;
        await graphService.DeleteNodeAsync(deletedId);
        SelectedNode = null;
        await LoadGraphAsync();
        await LoadDashboardAsync();
        StatusText = "Node deleted.";
    }

    [RelayCommand]
    private async Task CreateEdgeAsync()
    {
        if (SelectedNode is null || EdgeTargetNode is null)
        {
            StatusText = "Select a source node and target node first.";
            return;
        }

        await graphService.CreateEdgeAsync(SelectedNode.Id, EdgeTargetNode.Id, NewEdgeRelationship, NewEdgeStrength);
        await LoadGraphAsync();
        await LoadConnectionsAsync(SelectedNode.Id);
        StatusText = "Edge created.";
    }

    [RelayCommand]
    private async Task DeleteEdgeAsync(NodeConnection? connection)
    {
        if (connection is null)
        {
            return;
        }

        await graphService.DeleteEdgeAsync(connection.EdgeId);
        await LoadGraphAsync();
        if (SelectedNode is not null)
        {
            await LoadConnectionsAsync(SelectedNode.Id);
        }

        StatusText = "Edge deleted.";
    }

    public async Task SaveNodePositionAsync(Node node)
    {
        if (node.PositionX.HasValue && node.PositionY.HasValue)
        {
            await graphService.SaveNodePositionAsync(node.Id, node.PositionX.Value, node.PositionY.Value);
        }
    }

    [RelayCommand]
    private async Task NewConversationAsync()
    {
        var conversation = await conversationService.CreateConversationAsync("New conversation");
        await LoadConversationsAsync();
        SelectedConversation = Conversations.FirstOrDefault(existing => existing.Id == conversation.Id);
        CurrentView = "Conversations";
    }

    [RelayCommand]
    private async Task SendChatAsync()
    {
        var content = ChatInput.Trim();
        if (content.Length == 0)
        {
            return;
        }

        if (content.Equals("/new", StringComparison.OrdinalIgnoreCase))
        {
            ChatInput = string.Empty;
            SelectedConversation = await conversationService.CreateConversationAsync("New conversation");
            await LoadConversationsAsync();
            ChatMessages.Clear();
            StatusText = "Started a new conversation.";
            return;
        }

        if (content.StartsWith("/web ", StringComparison.OrdinalIgnoreCase))
        {
            var query = content["/web ".Length..].Trim();
            ChatInput = string.Empty;

            if (SelectedConversation is null)
            {
                SelectedConversation = await conversationService.CreateConversationAsync("New conversation");
                await LoadConversationsAsync();
            }
            var webConversationId = SelectedConversation!.Id;
            var webUserMessage = await conversationService.AddMessageAsync(webConversationId, "user", content, SelectedNode?.Id);
            ChatMessages.Add(webUserMessage);

            IsBusy = true;

            // Add thinking placeholder message
            var thinkingMessage = new Message { Role = "thinking", Content = "Searching the web...", CreatedAt = DateTimeOffset.UtcNow };
            ChatMessages.Add(thinkingMessage);

            var animCts = new CancellationTokenSource();
            var animToken = animCts.Token;
            var isSearchingWeb = true;
            var visitedUrls = new System.Collections.Generic.List<string>();

            var animTask = Task.Run(async () =>
            {
                int dotCount = 1;
                while (!animToken.IsCancellationRequested)
                {
                    var status = isSearchingWeb ? "Searching the web" : "Thinking";
                    var text = status + new string('.', dotCount);
                    if (visitedUrls.Count > 0)
                    {
                        text += "\nVisited:\n" + string.Join(Environment.NewLine, visitedUrls.Select(u => $"- {u}"));
                    }
                    App.DispatcherQueue.TryEnqueue(() =>
                    {
                        var idx = ChatMessages.IndexOf(thinkingMessage);
                        if (idx >= 0)
                        {
                            var oldMsg = ChatMessages[idx];
                            ChatMessages[idx] = new Message
                            {
                                Id = thinkingMessage.Id,
                                Role = "thinking",
                                Content = text,
                                CreatedAt = thinkingMessage.CreatedAt,
                                IsExpanded = oldMsg.IsExpanded
                            };
                        }
                    });
                    dotCount = (dotCount % 3) + 1;
                    await Task.Delay(400);
                }
            }, animToken);

            var webSearchResultJson = await toolService.ExecuteToolAsync("WebSearch", System.Text.Json.JsonSerializer.Serialize(new { query }));

            // Extract visited URLs from SearXNG JSON response
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(webSearchResultJson);
                var root = doc.RootElement;
                if (root.TryGetProperty("results", out var resultsProp) && resultsProp.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var item in resultsProp.EnumerateArray())
                    {
                        var url = item.TryGetProperty("url", out var u) ? u.GetString() : "";
                        if (!string.IsNullOrWhiteSpace(url))
                        {
                            visitedUrls.Add(url);
                        }
                    }
                }
            }
            catch { }

            isSearchingWeb = false;

            string webContext = "";
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(webSearchResultJson);
                var root = doc.RootElement;
                if (root.TryGetProperty("results", out var resultsProp) && resultsProp.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    var sb = new System.Text.StringBuilder();
                    sb.AppendLine("Web Search Results:");
                    foreach (var item in resultsProp.EnumerateArray())
                    {
                        var title = item.GetProperty("title").GetString();
                        var snippet = item.GetProperty("snippet").GetString();
                        sb.AppendLine($"- **{title}**: {snippet}");
                    }
                    webContext = sb.ToString();
                }
                else if (root.TryGetProperty("error", out var errorProp))
                {
                    webContext = $"Web search error: {errorProp.GetString()}";
                }
            }
            catch
            {
                webContext = "Failed to parse web search results.";
            }

            var webTurns = await BuildChatTurnsAsync(query);
            webTurns.Add(new AiChatTurn("system", $"Web search context for '{query}':\n{webContext}"));

            // Add user and history turns, excluding the temporary thinking message
            webTurns.AddRange(ChatMessages.Where(m => m.Role != "thinking").Select(message => new AiChatTurn(message.Role, message.Content)));

            var webEstimatedPromptTokens = AiModelCatalog.EstimateTokens(webTurns.Select(turn => turn.Content));
            var webResult = await aiChatService.SendAsync(SelectedProvider, webTurns);

            animCts.Cancel();
            try { await animTask; } catch { }

            IsBusy = false;
            UpdateContextTracker(webResult, webEstimatedPromptTokens);

            // Persist the final thinking log to the database
            var finalThinkingText = "";
            if (!string.IsNullOrWhiteSpace(webResult.ReasoningContent))
            {
                finalThinkingText = webResult.ReasoningContent;
            }
            if (visitedUrls.Count > 0)
            {
                var visitedStr = "Visited:\n" + string.Join(Environment.NewLine, visitedUrls.Select(u => $"- {u}"));
                finalThinkingText = string.IsNullOrWhiteSpace(finalThinkingText) ? visitedStr : finalThinkingText + "\n\n" + visitedStr;
            }

            if (!string.IsNullOrWhiteSpace(finalThinkingText))
            {
                var savedThinkingMsg = await conversationService.AddMessageAsync(webConversationId, "thinking", finalThinkingText, SelectedNode?.Id);
                var tIdx = ChatMessages.IndexOf(thinkingMessage);
                if (tIdx >= 0)
                {
                    savedThinkingMsg.IsExpanded = ChatMessages[tIdx].IsExpanded;
                    ChatMessages[tIdx] = savedThinkingMsg;
                }
            }
            else
            {
                ChatMessages.Remove(thinkingMessage);
            }

            var webAssistantText = webResult.SetupRequired || webResult.Error is not null
                ? webContext
                : webResult.Content;
            var webAssistantMessage = await conversationService.AddMessageAsync(webConversationId, "assistant", webAssistantText, SelectedNode?.Id);
            ChatMessages.Add(webAssistantMessage);
            await LoadConversationsAsync(selectId: webConversationId);
            return;
        }

        if (SelectedConversation is null)
        {
            SelectedConversation = await conversationService.CreateConversationAsync("New conversation");
            await LoadConversationsAsync();
        }

        var conversationId = SelectedConversation!.Id;
        ChatInput = string.Empty;
        var linkedNodeId = SelectedNode?.Id;
        var userMessage = await conversationService.AddMessageAsync(conversationId, "user", content, linkedNodeId);
        ChatMessages.Add(userMessage);

        IsBusy = true;

        if (IsWebSearchEnabled)
        {
            // ROUTE THROUGH THE AGENT LOOP
            var thinkingMessage = new Message { Role = "thinking", Content = "Thinking...", CreatedAt = DateTimeOffset.UtcNow };
            ChatMessages.Add(thinkingMessage);

            var animCts = new CancellationTokenSource();
            var animToken = animCts.Token;
            var isSearchingWeb = false;
            var visitedUrls = new System.Collections.Generic.List<string>();

            Action<string> onExecuting = toolName =>
            {
                if (toolName == "WebSearch")
                {
                    isSearchingWeb = true;
                }
            };
            Action<string, string> onExecuted = (toolName, resultJson) =>
            {
                if (toolName == "WebSearch")
                {
                    isSearchingWeb = false;
                    try
                    {
                        using var doc = JsonDocument.Parse(resultJson);
                        var root = doc.RootElement;
                        if (root.TryGetProperty("results", out var resultsProp) && resultsProp.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var item in resultsProp.EnumerateArray())
                            {
                                var url = item.TryGetProperty("url", out var u) ? u.GetString() : "";
                                if (!string.IsNullOrWhiteSpace(url))
                                {
                                    lock (visitedUrls)
                                    {
                                        if (!visitedUrls.Contains(url))
                                        {
                                            visitedUrls.Add(url);
                                        }
                                    }
                                }
                            }
                        }
                    }
                    catch { }
                }
            };

            ToolService.OnToolExecuting += onExecuting;
            ToolService.OnToolExecuted += onExecuted;

            var animTask = Task.Run(async () =>
            {
                int dotCount = 1;
                while (!animToken.IsCancellationRequested)
                {
                    var status = isSearchingWeb ? "Searching the web" : "Thinking";
                    var text = status + new string('.', dotCount);
                    lock (visitedUrls)
                    {
                        if (visitedUrls.Count > 0)
                        {
                            text += "\nVisited:\n" + string.Join(Environment.NewLine, visitedUrls.Select(u => $"- {u}"));
                        }
                    }
                    App.DispatcherQueue.TryEnqueue(() =>
                    {
                        var idx = ChatMessages.IndexOf(thinkingMessage);
                        if (idx >= 0)
                        {
                            var oldMsg = ChatMessages[idx];
                            ChatMessages[idx] = new Message
                            {
                                Id = thinkingMessage.Id,
                                Role = "thinking",
                                Content = text,
                                CreatedAt = thinkingMessage.CreatedAt,
                                IsExpanded = oldMsg.IsExpanded
                            };
                        }
                    });
                    dotCount = (dotCount % 3) + 1;
                    await Task.Delay(400);
                }
            }, animToken);

            (string FinalAnswer, string ExecutionLog) agentResult;
            try
            {
                agentResult = await agentService.RunWithDetailsAsync(content, conversationId, CancellationToken.None);
            }
            finally
            {
                ToolService.OnToolExecuting -= onExecuting;
                ToolService.OnToolExecuted -= onExecuted;
            }

            animCts.Cancel();
            try { await animTask; } catch { }

            IsBusy = false;

            // Save final agent thinking execution log
            var finalThinkingText = agentResult.ExecutionLog;
            lock (visitedUrls)
            {
                if (visitedUrls.Count > 0)
                {
                    var visitedStr = "Visited:\n" + string.Join(Environment.NewLine, visitedUrls.Select(u => $"- {u}"));
                    finalThinkingText = string.IsNullOrWhiteSpace(finalThinkingText) ? visitedStr : finalThinkingText + "\n\n" + visitedStr;
                }
            }

            var savedThinkingMsg = await conversationService.AddMessageAsync(conversationId, "thinking", finalThinkingText, linkedNodeId);
            var tIdx = ChatMessages.IndexOf(thinkingMessage);
            if (tIdx >= 0)
            {
                savedThinkingMsg.IsExpanded = ChatMessages[tIdx].IsExpanded;
                ChatMessages[tIdx] = savedThinkingMsg;
            }

            // Add final answer
            var assistantMessage = await conversationService.AddMessageAsync(conversationId, "assistant", agentResult.FinalAnswer, linkedNodeId);
            ChatMessages.Add(assistantMessage);
            await LoadConversationsAsync(selectId: conversationId);
            await LoadGraphAsync();
        }
        else
        {
            // ROUTE DIRECTLY TO LLM
            var thinkingMessage = new Message { Role = "thinking", Content = "Thinking...", CreatedAt = DateTimeOffset.UtcNow };
            ChatMessages.Add(thinkingMessage);

            var animCts = new CancellationTokenSource();
            var animToken = animCts.Token;
            var animTask = Task.Run(async () =>
            {
                int dotCount = 1;
                while (!animToken.IsCancellationRequested)
                {
                    var text = "Thinking" + new string('.', dotCount);
                    App.DispatcherQueue.TryEnqueue(() =>
                    {
                        var idx = ChatMessages.IndexOf(thinkingMessage);
                        if (idx >= 0)
                        {
                            var oldMsg = ChatMessages[idx];
                            ChatMessages[idx] = new Message
                            {
                                Id = thinkingMessage.Id,
                                Role = "thinking",
                                Content = text,
                                CreatedAt = thinkingMessage.CreatedAt,
                                IsExpanded = oldMsg.IsExpanded
                            };
                        }
                    });
                    dotCount = (dotCount % 3) + 1;
                    await Task.Delay(400);
                }
            }, animToken);

            var turns = await BuildChatTurnsAsync(content);
            // Exclude the temporary thinking message from prompt turns
            turns.AddRange(ChatMessages.Where(m => m.Role != "thinking").Select(message => new AiChatTurn(message.Role, message.Content)));
            var estimatedPromptTokens = AiModelCatalog.EstimateTokens(turns.Select(turn => turn.Content));

            var result = await aiChatService.SendAsync(SelectedProvider, turns);

            animCts.Cancel();
            try { await animTask; } catch { }

            IsBusy = false;
            UpdateContextTracker(result, estimatedPromptTokens);

            // Persist the final thinking log if present
            if (!string.IsNullOrWhiteSpace(result.ReasoningContent))
            {
                var savedThinkingMsg = await conversationService.AddMessageAsync(conversationId, "thinking", result.ReasoningContent, linkedNodeId);
                var tIdx = ChatMessages.IndexOf(thinkingMessage);
                if (tIdx >= 0)
                {
                    savedThinkingMsg.IsExpanded = ChatMessages[tIdx].IsExpanded;
                    ChatMessages[tIdx] = savedThinkingMsg;
                }
            }
            else
            {
                ChatMessages.Remove(thinkingMessage);
            }

            var assistantText = result.Error is not null ? $"LLM error: {result.Error}" : result.Content;
            var assistantMessage = await conversationService.AddMessageAsync(conversationId, "assistant", assistantText, linkedNodeId);
            ChatMessages.Add(assistantMessage);
            await LoadConversationsAsync(selectId: conversationId);
        }
    }

    [RelayCommand]
    private async Task SaveMessageAsMemoryAsync(Message? message)
    {
        if (message is null)
        {
            return;
        }

        await memoryService.SaveMemoryAsync(message.Content, $"chat-manual:{message.Role}", 4, message.LinkedNodeId);
        await SearchMemoriesAsync();
        StatusText = "Message saved as memory.";
    }

    [RelayCommand]
    private async Task CreateNodeFromMessageAsync(Message? message)
    {
        if (message is null)
        {
            return;
        }

        var title = message.Content.Length > 54 ? message.Content[..54] : message.Content;
        var node = await graphService.CreateNodeAsync(new Node
        {
            Title = title,
            Type = "Conversation",
            Summary = message.Content.Length > 180 ? message.Content[..180] : message.Content,
            Body = message.Content,
            Status = "Active",
            Importance = 3,
            ColorKey = message.Role == "assistant" ? "violet" : "blue",
            IconKey = "conversation"
        });

        await LoadGraphAsync();
        SelectedNode = Nodes.FirstOrDefault(existing => existing.Id == node.Id);
        CurrentView = "Graph";
        StatusText = "Node created from message.";
    }

    [RelayCommand]
    private async Task SaveProviderSettingsAsync()
    {
        if (SelectedProvider is null)
        {
            return;
        }

        if (IsApiKeyRequired && string.IsNullOrWhiteSpace(SelectedProvider.ApiKeyStorageKey))
        {
            SelectedProvider.ApiKeyStorageKey = $"ai.{SelectedProvider.Name.ToLowerInvariant().Replace(' ', '-')}.api_key";
        }

        if (IsApiKeyRequired && !string.IsNullOrWhiteSpace(ApiKeyInput))
        {
            // API keys are stored outside SQLite in the Windows Credential Locker.
            await secretStore.SetSecretAsync(SelectedProvider.ApiKeyStorageKey, ApiKeyInput);
            ApiKeyInput = string.Empty;
        }

        var saved = await settingsService.SaveAiProviderProfileAsync(SelectedProvider);
        await LoadProvidersAsync(saved.Id);
        await UpdateApiKeyPlaceholderAsync(SelectedProvider);
        SettingsStatus = "LLM settings saved.";
    }

    [RelayCommand]
    private async Task RefreshProviderModelsAsync()
    {
        if (SelectedProvider is null)
        {
            return;
        }

        SettingsStatus = $"Refreshing models for {SelectedProvider.Name}...";
        await LoadModelOptionsForSelectedProviderAsync(forceRefresh: true);
        SettingsStatus = ModelOptions.Count == 0
            ? $"No models were returned for {SelectedProvider.Name}."
            : $"Loaded {ModelOptions.Count} available models for {SelectedProvider.Name}.";
    }

    [RelayCommand]
    private async Task SignInOpenAiCodexAsync()
    {
        if (!IsOpenAiCodexProvider)
        {
            return;
        }

        try
        {
            SettingsStatus = "Starting ChatGPT sign-in through the official Codex CLI...";
            var login = await openAiCodexService.StartLoginAsync();
            if (!login.Started ||
                string.IsNullOrWhiteSpace(login.LoginId) ||
                !Uri.TryCreate(login.AuthorizationUrl, UriKind.Absolute, out var authorizationUri))
            {
                SettingsStatus = login.Status;
                return;
            }

            if (!await Launcher.LaunchUriAsync(authorizationUri))
            {
                SettingsStatus = $"Open this URL to finish sign-in: {login.AuthorizationUrl}";
                return;
            }

            SettingsStatus = "Waiting for ChatGPT sign-in to complete in the browser...";
            var account = await openAiCodexService.CompleteLoginAsync(login.LoginId, TimeSpan.FromMinutes(5));
            SettingsStatus = account.Status;
            if (account.IsAuthenticated)
            {
                await LoadModelOptionsForSelectedProviderAsync(forceRefresh: true);
            }
        }
        catch (Exception ex)
        {
            SettingsStatus = $"ChatGPT sign-in could not finish in Argus: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task SignOutOpenAiCodexAsync()
    {
        if (!IsOpenAiCodexProvider)
        {
            return;
        }

        try
        {
            await openAiCodexService.LogoutAsync();
            SettingsStatus = "Signed out of the ChatGPT account used by Codex.";
        }
        catch (Exception ex)
        {
            SettingsStatus = $"Could not sign out of Codex: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task TestSelectedProviderAsync()
    {
        if (SelectedProvider is null)
        {
            return;
        }

        SettingsStatus = $"Testing {SelectedProvider.Name} auth...";
        IsBusy = true;
        var result = await aiChatService.SendAsync(SelectedProvider, new[]
        {
            new AiChatTurn("user", "Reply with exactly: argus-auth-ok")
        });
        IsBusy = false;

        if (result.SetupRequired || result.Error is not null)
        {
            SettingsStatus = result.Error ?? result.Content;
            return;
        }

        SettingsStatus = result.Content.Contains("argus-auth-ok", StringComparison.OrdinalIgnoreCase)
            ? $"{SelectedProvider.Name} auth is working."
            : $"{SelectedProvider.Name} responded, but not with the expected smoke-test text.";
    }

    [RelayCommand]
    private async Task SaveSoulAsync()
    {
        await soulService.SaveSoulAsync(SoulText);
        StatusText = $"Soul saved to {SoulPath}.";
    }

    [RelayCommand]
    private async Task ScanProjectsAsync()
    {
        if (string.IsNullOrWhiteSpace(ProjectsRootPath))
        {
            StatusText = "Choose a projects directory in Settings before scanning.";
            return;
        }

        await settingsService.SaveSettingAsync("ProjectsRootPath", ProjectsRootPath.Trim());
        await SyncProjectsFromWorkspaceAsync();
        await LoadGraphAsync();
        await LoadDashboardAsync();
        StatusText = $"Scanned {ProjectContexts.Count} local projects.";
    }

    [RelayCommand]
    private async Task SummarizeSelectedProjectAsync()
    {
        if (SelectedNode is null)
        {
            return;
        }

        var context = await projectContextService.GetProjectContextAsync(SelectedNode.Title);
        if (context is null)
        {
            StatusText = "No matching local project folder found.";
            return;
        }

        var prompt =
            $$"""
            Summarize this local project for a personal knowledge graph.

            {{ProjectContextPrivacy.BuildOutboundPreview(context)}}

            Return:
            - one concise summary sentence
            - current project state
            - next useful actions
            """;

        IsBusy = true;
        var result = await aiChatService.SendAsync(SelectedProvider, new[]
        {
            new AiChatTurn("system", SoulText),
            new AiChatTurn("user", prompt)
        });
        IsBusy = false;

        if (result.Error is not null || result.SetupRequired)
        {
            StatusText = result.Error ?? result.Content;
            return;
        }

        SelectedNode.Summary = result.Content.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? result.Content;
        SelectedNode.Body = $"{result.Content}{Environment.NewLine}{Environment.NewLine}--- Project context ---{Environment.NewLine}{ProjectContextText}";
        await graphService.UpdateNodeAsync(SelectedNode);
        await memoryService.SaveMemoryAsync(result.Content, $"project-summary:{context.Name}", 4, SelectedNode.Id);
        await LoadGraphAsync();
        StatusText = "Project summarized with DeepSeek-compatible chat.";
    }

    [RelayCommand]
    private async Task AskAgentAsync()
    {
        try
        {
            IsBusy = true;
            var (finalAnswer, _) = await agentService.RunWithDetailsAsync("summarize current Argus state");
            StatusText = finalAnswer;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task SaveSystemSettingsAsync()
    {
        await settingsService.SaveSettingAsync("ProjectsRootPath", ProjectsRootPath);
        await settingsService.SaveSettingAsync("TelegramBotEnabled", TelegramBotEnabled.ToString());
        if (string.IsNullOrWhiteSpace(TelegramBotToken))
        {
            await secretStore.RemoveSecretAsync("telegram.bot_token");
        }
        else
        {
            await secretStore.SetSecretAsync("telegram.bot_token", TelegramBotToken.Trim());
        }
        await settingsService.SaveSettingAsync("TelegramAllowedUserIds", TelegramAllowedUserIds);
        await settingsService.SaveSettingAsync("TelegramUseWebhook", TelegramUseWebhook.ToString());
        await settingsService.SaveSettingAsync("TelegramWebhookUrl", TelegramWebhookUrl);
        await settingsService.SaveSettingAsync("TelegramWebhookPort", TelegramWebhookPort);
        await settingsService.SaveSettingAsync("ShowThinkingInApp", ShowThinkingInApp.ToString());
        await settingsService.SaveSettingAsync("ShowThinkingInTelegram", ShowThinkingInTelegram.ToString());

        StatusText = "System & gateway settings saved.";

        await telegramGatewayService.StopAsync();
        if (TelegramBotEnabled)
        {
            await telegramGatewayService.StartAsync();
        }
        else
        {
            TelegramGatewayStatusText = "Disabled";
        }
    }

    [RelayCommand]
    private async Task ExportGraphToClipboardAsync()
    {
        var json = await graphExchangeService.ExportJsonAsync();
        var package = new DataPackage();
        package.SetText(json);
        Clipboard.SetContent(package);
        StatusText = "Graph JSON exported to clipboard.";
    }

    [RelayCommand]
    private async Task ImportGraphFromClipboardAsync()
    {
        var content = Clipboard.GetContent();
        if (!content.Contains(StandardDataFormats.Text))
        {
            StatusText = "Clipboard does not contain graph JSON.";
            return;
        }

        var json = await content.GetTextAsync();
        await graphExchangeService.ImportJsonAsync(json);
        await LoadGraphAsync();
        await LoadDashboardAsync();
        StatusText = "Graph JSON imported from clipboard.";
    }

    private async Task LoadAllAsync()
    {
        EnsurePaletteItems();
        SoulPath = soulService.SoulPath;
        SoulText = await soulService.ReadSoulAsync();
        await LoadProvidersAsync();
        ProjectsRootPath = await settingsService.GetSettingAsync("ProjectsRootPath", "") ?? "";
        if (!string.IsNullOrWhiteSpace(ProjectsRootPath))
        {
            await SyncProjectsFromWorkspaceAsync();
        }
        await LoadGraphAsync();
        await LoadDashboardAsync();
        StartDashboardWidgets();
        await LoadConversationsAsync();
        await SearchMemoriesAsync();
        Replace(AvailableTools, (await toolService.ListToolsAsync()).Take(5));
        FilterCommandPalette();

        var pinnedStr = await settingsService.GetSettingAsync("PinnedNodeIds", "") ?? "";
        pinnedNodeIds.Clear();
        foreach (var idStr in pinnedStr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Guid.TryParse(idStr, out var id))
            {
                pinnedNodeIds.Add(id);
            }
        }

        var enabledStr = await settingsService.GetSettingAsync("TelegramBotEnabled", "false");
        TelegramBotEnabled = bool.TryParse(enabledStr, out var enabled) && enabled;
        TelegramBotToken = await secretStore.GetSecretAsync("telegram.bot_token") ?? "";
        UpdateTelegramTokenPlaceholder();
        if (string.IsNullOrWhiteSpace(TelegramBotToken))
        {
            var legacyToken = await settingsService.GetSettingAsync("TelegramBotToken", "") ?? "";
            if (!string.IsNullOrWhiteSpace(legacyToken))
            {
                TelegramBotToken = legacyToken;
                await secretStore.SetSecretAsync("telegram.bot_token", legacyToken);
                await settingsService.SaveSettingAsync("TelegramBotToken", "");
            }
        }
        TelegramAllowedUserIds = await settingsService.GetSettingAsync("TelegramAllowedUserIds", "") ?? "";

        var useWebhookStr = await settingsService.GetSettingAsync("TelegramUseWebhook", "false");
        TelegramUseWebhook = bool.TryParse(useWebhookStr, out var useWebhook) && useWebhook;
        TelegramWebhookUrl = await settingsService.GetSettingAsync("TelegramWebhookUrl", "") ?? "";
        TelegramWebhookPort = await settingsService.GetSettingAsync("TelegramWebhookPort", "8080") ?? "8080";
        var showThinkingInAppStr = await settingsService.GetSettingAsync("ShowThinkingInApp", "false");
        ShowThinkingInApp = bool.TryParse(showThinkingInAppStr, out var showAppThinking) && showAppThinking;
        var showThinkingInTelegramStr = await settingsService.GetSettingAsync("ShowThinkingInTelegram", "false");
        ShowThinkingInTelegram = bool.TryParse(showThinkingInTelegramStr, out var showTelegramThinking) && showTelegramThinking;

        var webSearchEnabledStr = await settingsService.GetSettingAsync("IsWebSearchEnabled", "false");
        IsWebSearchEnabled = bool.TryParse(webSearchEnabledStr, out var webSearchEnabled) && webSearchEnabled;
        var showSystemDefault = "true"; // System Status always works
        var showProjectsDefault = string.IsNullOrWhiteSpace(ProjectsRootPath) ? "false" : "true";
        var showMarketDefault = "false";   // User must configure stock symbols first
        var showNewsDefault = "true";      // RSS feeds auto-populate without config
        var showSportsDefault = "false";   // User must configure league/teams first
        ShowSystemStatusWidget = bool.TryParse(await settingsService.GetSettingAsync("ShowSystemStatusWidget", showSystemDefault), out var b1) ? b1 : true;
        ShowProjectsWidget = bool.TryParse(await settingsService.GetSettingAsync("ShowProjectsWidget", showProjectsDefault), out var b2) ? b2 : true;
        ShowMarketWidget = bool.TryParse(await settingsService.GetSettingAsync("ShowMarketWidget", showMarketDefault), out var b3) ? b3 : false;
        ShowNewsWidget = bool.TryParse(await settingsService.GetSettingAsync("ShowNewsWidget", showNewsDefault), out var b4) ? b4 : true;
        ShowSportsWidget = bool.TryParse(await settingsService.GetSettingAsync("ShowSportsWidget", showSportsDefault), out var b5) ? b5 : false;

        OnPropertyChanged(nameof(Column0Width));
        OnPropertyChanged(nameof(Column2Width));

        TelegramGatewayStatusText = telegramGatewayService.Status;
        TelegramGatewayLogsText = telegramGatewayService.GetLogs();
        telegramGatewayService.OnStatusChanged += () =>
        {
            App.DispatcherQueue.TryEnqueue(() =>
            {
                TelegramGatewayStatusText = telegramGatewayService.Status;
                TelegramGatewayLogsText = telegramGatewayService.GetLogs();
            });
        };

        await RefreshDatabaseInfoAsync();
        await LoadAuditsAsync();

        _ = CheckForUpdatesAsync();
    }

    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        if (IsUpdateBusy)
        {
            return;
        }

        IsUpdateBusy = true;
        UpdateStatusText = "Checking for updates...";
        try
        {
            var latest = await appUpdateService.GetLatestReleaseAsync();
            if (latest is null)
            {
                IsUpdateAvailable = false;
                UpdateButtonText = VersionDisplayText;
                UpdateStatusText = "Unable to read the latest GitHub release.";
                return;
            }

            var current = BuildVersion();
            availableUpdate = latest.Version > current ? latest : null;
            IsUpdateAvailable = availableUpdate is not null;
            UpdateButtonText = IsUpdateAvailable ? $"Update {latest.DisplayVersion}" : VersionDisplayText;
            UpdateStatusText = IsUpdateAvailable
                ? $"{latest.DisplayVersion} is ready to install."
                : $"Argus {VersionDisplayText} is up to date.";
            UpdateReleaseNotes = string.IsNullOrWhiteSpace(latest.Notes)
                ? latest.Name
                : latest.Notes.Trim();
        }
        catch (Exception ex)
        {
            IsUpdateAvailable = false;
            UpdateButtonText = VersionDisplayText;
            UpdateStatusText = $"Update check failed: {ex.Message}";
        }
        finally
        {
            IsUpdateBusy = false;
        }
    }

    [RelayCommand]
    private async Task InstallUpdateAsync()
    {
        if (availableUpdate is null || IsUpdateBusy)
        {
            return;
        }

        IsUpdateBusy = true;
        UpdateStatusText = $"Downloading {availableUpdate.DisplayVersion}...";
        try
        {
            await appUpdateService.DownloadAndLaunchInstallerAsync(availableUpdate);
            UpdateStatusText = "Installer launched. Argus will close for the update.";
            App.Window.Close();
        }
        catch (Exception ex)
        {
            UpdateStatusText = $"Update failed: {ex.Message}";
            IsUpdateBusy = false;
        }
    }

    private async Task<List<AiChatTurn>> BuildChatTurnsAsync(string userInput)
    {
        var memories = await memoryService.RecallAsync(userInput, 20);
        var memoryContext = memories.Count == 0
            ? "No matching memories."
            : string.Join(Environment.NewLine, memories.Select(memory => $"- {memory.Text}"));
        var selectedProjectContext = SelectedNode?.Type.Equals(
            "Project",
            StringComparison.OrdinalIgnoreCase) == true
            ? await projectContextService.GetProjectContextAsync(SelectedNode.Title)
            : null;
        var projectContext = selectedProjectContext is not null
            ? ProjectContextPrivacy.BuildOutboundPreview(selectedProjectContext)
            : SelectedNode?.Type.Equals("Project", StringComparison.OrdinalIgnoreCase) == true
                ? $"Project: {SelectedNode.Title}{Environment.NewLine}Graph summary: {SelectedNode.Summary}{Environment.NewLine}No current local folder context is available."
                : SelectedNode is null
                    ? "No selected project node."
                    : ProjectContextText;
        var knownProjects = ProjectContexts.Count == 0
            ? "No local projects scanned."
            : string.Join(
                Environment.NewLine,
                ProjectContexts.Take(24).Select(project =>
                    $"- {project.Name}: {ProjectContextPrivacy.SanitizeStateSummary(project.StateSummary).Replace(Environment.NewLine, " | ")}"));

        var turns = new List<AiChatTurn>
        {
            new("system", SoulText),
            new("system",
                $$"""
                Argus memory context:
                {{memoryContext}}

                Selected project context:
                {{projectContext}}

                Local project index:
                {{knownProjects}}

                Use this context across sessions. When useful, suggest graph nodes, memories, project links, and next actions.
                """)
        };

        // Per-source token breakdown
        var soulTokens = AiModelCatalog.EstimateTokens([SoulText]);
        var memoryTokens = AiModelCatalog.EstimateTokens([memoryContext]);
        var projectTokens = AiModelCatalog.EstimateTokens([projectContext]);
        var projectsIndexTokens = AiModelCatalog.EstimateTokens([knownProjects]);
        ContextBreakdownText =
            $"Soul: ~{AiModelCatalog.FormatTokenCount(soulTokens)}  ·  " +
            $"Memories: ~{AiModelCatalog.FormatTokenCount(memoryTokens)}  ·  " +
            $"Project: ~{AiModelCatalog.FormatTokenCount(projectTokens)}  ·  " +
            $"Index: ~{AiModelCatalog.FormatTokenCount(projectsIndexTokens)}";

        return turns;
    }

    private async Task LoadGraphAsync()
    {
        var graph = await graphService.GetGraphAsync();
        Replace(Nodes, graph.Nodes);
        Replace(Edges, graph.Edges);
        if (SelectedNode is not null)
        {
            SelectedNode = Nodes.FirstOrDefault(node => node.Id == SelectedNode.Id);
        }
    }

    private async Task LoadDashboardAsync()
    {
        Dashboard = await graphService.GetDashboardAsync();
        _ = LoadProjectCockpitDeferredAsync();
    }

    /// <summary>
    /// Load the project cockpit on the UI thread after the dashboard has rendered.
    /// Deferred to avoid binding evaluation during the initial async load storm.
    /// </summary>
    private async Task LoadProjectCockpitDeferredAsync()
    {
        try
        {
            // Run the heavy DB/computation work on a background thread
            var cockpit = await Task.Run(() => projectDashboardService.BuildAsync());

            // Dispatch UI updates to the main thread
            App.DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    ProjectCockpit = cockpit;
                    Replace(ProjectCockpitCards, cockpit?.ProjectCards ?? []);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Project cockpit UI update failed: {ex.Message}");
                }
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Project cockpit load failed: {ex.Message}");
        }
    }

    private DashboardWidgetsViewModel EnsureDashboardWidgets()
    {
        DashboardWidgets ??= App.Services.GetRequiredService<DashboardWidgetsViewModel>();
        return DashboardWidgets;
    }

    private void StartDashboardWidgets()
    {
        var widgets = EnsureDashboardWidgets();
        _ = widgets.LoadAllAsync();
    }

    private void ApplyGraphFilter(string nodeType)
    {
        GraphFilterType = nodeType;
        CurrentView = "Graph";
        OnPropertyChanged(nameof(GraphFilterLabel));
        StatusText = $"Showing {nodeType.ToLowerInvariant()} nodes.";
    }

    private async Task LoadConnectionsAsync(Guid? nodeId)
    {
        Connections.Clear();
        if (nodeId is null)
        {
            return;
        }

        Replace(Connections, await graphService.GetConnectionsAsync(nodeId.Value));
    }

    private async Task LoadSelectedNodeTagsAsync(Guid? nodeId)
    {
        SelectedNodeTags.Clear();
        TagEditorText = string.Empty;
        if (nodeId is null)
        {
            return;
        }

        var tags = await tagService.GetNodeTagsAsync(nodeId.Value);
        Replace(SelectedNodeTags, tags);
        TagEditorText = string.Join(", ", tags.Select(tag => tag.Name));
    }

    private async Task RefreshSelectedNodeAsync(Node? node, CancellationToken cancellationToken)
    {
        if (node is null)
        {
            Connections.Clear();
            SelectedNodeTags.Clear();
            TagEditorText = string.Empty;
            ProjectContextText = "Select a project node to inspect README, GitHub remote, and working tree state.";
            return;
        }

        try
        {
            var connectionsTask = graphService.GetConnectionsAsync(node.Id, cancellationToken);
            var tagsTask = tagService.GetNodeTagsAsync(node.Id, cancellationToken);
            var projectTask = node.Type.Equals("Project", StringComparison.OrdinalIgnoreCase)
                ? projectContextService.GetProjectContextAsync(node.Title, cancellationToken)
                : Task.FromResult<ProjectContext?>(null);

            await Task.WhenAll(connectionsTask, tagsTask, projectTask);
            cancellationToken.ThrowIfCancellationRequested();
            if (SelectedNode?.Id != node.Id)
            {
                return;
            }

            var tags = await tagsTask;
            Replace(Connections, await connectionsTask);
            Replace(SelectedNodeTags, tags);
            TagEditorText = string.Join(", ", tags.Select(tag => tag.Name));
            ProjectContextText = BuildInspectorContext(node, await projectTask);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (SelectedNode?.Id == node.Id)
            {
                StatusText = $"Could not load node details: {ex.Message}";
            }
        }
    }

    private async Task SyncProjectsFromWorkspaceAsync()
    {
        var contexts = await projectContextService.ScanProjectsAsync();
        Replace(ProjectContexts, contexts);

        var graph = await graphService.GetGraphAsync();
        foreach (var context in contexts)
        {
            var readmeTitle = ExtractReadmeTitle(context.ReadmePreview);
            var existingProject = graph.Nodes.FirstOrDefault(node =>
                node.Type.Equals("Project", StringComparison.OrdinalIgnoreCase) &&
                (node.Title.Equals(context.Name, StringComparison.OrdinalIgnoreCase) ||
                 (!string.IsNullOrWhiteSpace(readmeTitle) &&
                  node.Title.Equals(readmeTitle, StringComparison.OrdinalIgnoreCase))));
            if (existingProject is not null)
            {
                existingProject.Summary = BuildProjectSummary(context);
                existingProject.Body = BuildProjectBody(context);
                existingProject.Status = context.HasUncommittedChanges ? "Active" : "Scanned";
                existingProject.UpdatedAt = DateTimeOffset.UtcNow;
                existingProject.LastTouchedAt = DateTimeOffset.UtcNow;
                await graphService.UpdateNodeAsync(existingProject);
                continue;
            }

            await graphService.CreateNodeAsync(new Node
            {
                Title = context.Name,
                Type = "Project",
                Summary = BuildProjectSummary(context),
                Body = BuildProjectBody(context),
                Status = context.HasUncommittedChanges ? "Active" : "Scanned",
                Importance = context.ReadmePath is null ? 2 : 3,
                ColorKey = context.GitRemote?.Contains("github.com", StringComparison.OrdinalIgnoreCase) == true ? "blue" : "cyan",
                IconKey = "project"
            });
        }
    }

    private static string BuildInspectorContext(Node node, ProjectContext? context)
    {
        if (!node.Type.Equals("Project", StringComparison.OrdinalIgnoreCase))
        {
            var details = string.IsNullOrWhiteSpace(node.Body) ||
                string.Equals(node.Body, node.Summary, StringComparison.Ordinal)
                ? node.Summary
                : $"{node.Summary}{Environment.NewLine}{Environment.NewLine}{node.Body}";
            return $"{node.Type} · {node.Status}{Environment.NewLine}{Environment.NewLine}{details}";
        }

        if (context is null)
        {
            return
                $$"""
                {{node.Title}}

                No matching folder was found under the configured projects directory.

                Graph summary:
                {{node.Summary}}

                Rescan Projects after changing the projects directory or README title.
                """;
        }

        var suggestions = new List<string>();
        if (context.HasUncommittedChanges)
        {
            suggestions.Add("Review the local changes and decide what should be committed.");
        }
        if (context.ReadmePath is null)
        {
            suggestions.Add("Add a concise README with current status and setup instructions.");
        }
        if (string.IsNullOrWhiteSpace(context.GitRemote))
        {
            suggestions.Add("Confirm whether this project should remain local-only or have a Git remote.");
        }
        suggestions.Add("Record the next concrete action, blocker, or decision in Argus.");

        return
            $$"""
            {{context.Name}}
            {{context.Path}}

            {{context.StateSummary}}

            README preview:
            {{context.ReadmePreview}}

            External provider preview:
            {{ProjectContextPrivacy.BuildOutboundPreview(context)}}

            Suggested next actions:
            {{string.Join(Environment.NewLine, suggestions.Select(item => $"- {item}"))}}
            """;
    }

    private static string FirstLine(string text)
    {
        return text.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "Local project";
    }

    private static string BuildProjectSummary(ProjectContext context)
    {
        return context.ReadmePreview
            .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .FirstOrDefault(line => !line.StartsWith('#') && line.Length >= 16)
            ?? FirstLine(context.ReadmePreview);
    }

    private static string BuildProjectBody(ProjectContext context)
    {
        return
            $$"""
            Local path: {{context.Path}}

            {{context.StateSummary}}

            README:
            {{context.ReadmePreview}}
            """;
    }

    private static string ExtractReadmeTitle(string readmePreview)
    {
        var heading = readmePreview
            .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.StartsWith("# ", StringComparison.Ordinal));
        return heading is null ? string.Empty : heading[2..].Trim();
    }

    private async Task LoadConversationsAsync(Guid? selectId = null)
    {
        var selectedId = selectId ?? SelectedConversation?.Id;
        Replace(Conversations, await conversationService.GetConversationsAsync());
        OnPropertyChanged(nameof(HasConversations));
        SelectedConversation = Conversations.FirstOrDefault(conversation => conversation.Id == selectedId) ?? Conversations.FirstOrDefault();
    }

    private async Task LoadConversationMessagesAsync(Conversation? conversation)
    {
        ChatMessages.Clear();
        if (conversation is null)
        {
            return;
        }

        Replace(ChatMessages, await conversationService.GetMessagesAsync(conversation.Id));
    }

    private async Task LoadProvidersAsync(Guid? selectedId = null)
    {
        Replace(ProviderProfiles, await settingsService.GetAiProviderProfilesAsync());
        suppressSelectedProviderSideEffects = true;
        try
        {
            SelectedProvider = ProviderProfiles.FirstOrDefault(profile => profile.Id == selectedId)
                ?? ProviderProfiles.FirstOrDefault(profile => profile.IsDefault)
                ?? ProviderProfiles.FirstOrDefault();
        }
        finally
        {
            suppressSelectedProviderSideEffects = false;
        }

        await LoadModelOptionsForSelectedProviderAsync(forceRefresh: false);
        await UpdateApiKeyPlaceholderAsync(SelectedProvider);
        await UpdateProviderAuthenticationStatusAsync(SelectedProvider);
    }

    private async Task LoadModelOptionsForSelectedProviderAsync(bool forceRefresh)
    {
        var loadVersion = Interlocked.Increment(ref modelLoadVersion);
        var profile = SelectedProvider;
        ModelOptions.Clear();
        modelMetadata.Clear();
        if (profile is null)
        {
            RefreshReasoningEffortOptions();
            UpdateContextTracker(null, 0);
            return;
        }

        var models = await aiProviderRegistry.ListModelsAsync(profile, forceRefresh);

        if (loadVersion != Volatile.Read(ref modelLoadVersion) || SelectedProvider?.Id != profile.Id)
        {
            return;
        }

        if (models.Count == 0)
        {
            models = aiProviderRegistry.GetFallbackModels(profile);
        }

        foreach (var model in models)
        {
            if (!modelMetadata.ContainsKey(model.Id))
            {
                modelMetadata[model.Id] = model;
                ModelOptions.Add(model.Id);
            }
        }

        if (!string.IsNullOrWhiteSpace(profile.Model) &&
            !ModelOptions.Contains(profile.Model, StringComparer.OrdinalIgnoreCase))
        {
            if (AiModelCatalog.IsOpenAiCodexProvider(profile.ProviderType) && ModelOptions.Count > 0)
            {
                profile.Model = ModelOptions[0];
                await SaveProviderPreferenceAsync(profile);
            }
            else
            {
                ModelOptions.Insert(0, profile.Model);
            }
        }

        if (string.IsNullOrWhiteSpace(profile.Model) && ModelOptions.Count > 0)
        {
            profile.Model = ModelOptions[0];
            await SaveProviderPreferenceAsync(profile);
        }

        suppressSelectedModelUpdate = true;
        SelectedModel = profile.Model;
        suppressSelectedModelUpdate = false;
        RefreshReasoningEffortOptions();
        UpdateContextTracker(null, 0);
    }

    private void UpdateContextTracker(AiChatResult? result, int estimatedPromptTokens)
    {
        var contextLimit = result?.ContextWindowTokens ?? GetSelectedContextWindowTokens();
        var usedTokens = result?.PromptTokens ?? estimatedPromptTokens;
        if (!contextLimit.HasValue || contextLimit <= 0)
        {
            ContextTrackerText = $"{AiModelCatalog.FormatTokenCount(usedTokens)}/unknown";
            ContextTrackerPercent = 0;
            ContextTrackerPercentText = "n/a";
            ContextTrackerDetailText = result is null
                ? "Waiting for model usage."
                : result.PromptTokens is null && estimatedPromptTokens > 0
                ? "LLM usage unavailable; showing local prompt estimate."
                : "Context window unknown for selected model.";
            return;
        }

        var percent = Math.Clamp((double)usedTokens / contextLimit.Value * 100, 0, 100);
        ContextTrackerText = $"{AiModelCatalog.FormatTokenCount(usedTokens)}/{AiModelCatalog.FormatTokenCount(contextLimit)}";
        ContextTrackerPercent = percent;
        ContextTrackerPercentText = $"{percent:0.#}%";
        ContextTrackerDetailText = result is null
            ? "Waiting for model usage."
            : result.PromptTokens is null && estimatedPromptTokens > 0
            ? "LLM usage unavailable; showing local prompt estimate."
            : result.ContextWindowTokens.HasValue && IsOpenAiCodexProvider
            ? "Codex-reported prompt usage and session context window."
            : "LLM-reported prompt usage.";
    }

    private int? GetSelectedContextWindowTokens()
    {
        if (SelectedProvider is null || string.IsNullOrWhiteSpace(SelectedProvider.Model))
        {
            return null;
        }

        if (modelMetadata.TryGetValue(SelectedProvider.Model, out var metadata) && metadata.ContextWindowTokens.HasValue)
        {
            return metadata.ContextWindowTokens;
        }

        return AiModelCatalog.FindKnownModel(SelectedProvider.ProviderType, SelectedProvider.BaseUrl, SelectedProvider.Model)?.ContextWindowTokens;
    }

    private static void Replace<T>(ObservableCollection<T> collection, IEnumerable<T> values)
    {
        collection.Clear();
        foreach (var value in values)
        {
            collection.Add(value);
        }
    }

    private void EnsurePaletteItems()
    {
        if (paletteItems.Count > 0)
        {
            return;
        }

        paletteItems.AddRange(new[]
        {
            new CommandPaletteItem("Go to Dashboard", "View", "Open the dashboard.", () => { ShowDashboard(); return Task.CompletedTask; }),
            new CommandPaletteItem("Go to Graph", "View", "Open the graph canvas.", () => { CurrentView = "Graph"; return Task.CompletedTask; }),
            new CommandPaletteItem("Go to Conversations", "View", "Open local conversations.", () => { CurrentView = "Conversations"; return Task.CompletedTask; }),
            new CommandPaletteItem("Go to Memories", "View", "Open local memory search.", () => { CurrentView = "Memories"; return Task.CompletedTask; }),
            new CommandPaletteItem("Go to Skills", "View", "Open skill integrations.", () => { CurrentView = "Skills"; return Task.CompletedTask; }),
            new CommandPaletteItem("Go to Settings", "View", "Connect and configure LLMs.", () => { CurrentView = "Settings"; return Task.CompletedTask; }),
            new CommandPaletteItem("Create Node", "Graph", "Add a new idea node.", NewNodeAsync),
            new CommandPaletteItem("Create Edge", "Graph", "Connect selected node to target.", CreateEdgeAsync),
            new CommandPaletteItem("Save Selected Node", "Graph", "Persist inspector edits.", SaveSelectedNodeAsync),
            new CommandPaletteItem("Refresh Graph", "Graph", "Reload graph data.", RefreshGraphAsync),
            new CommandPaletteItem("Clear Graph Filter", "Graph", "Show every node type on the graph.", () => { ClearGraphFilter(); return Task.CompletedTask; }),
            new CommandPaletteItem("Save Tags", "Graph", "Save comma-separated tags on the selected node.", SaveSelectedNodeTagsAsync),
            new CommandPaletteItem("Export Graph JSON", "Graph", "Copy graph JSON to the clipboard.", ExportGraphToClipboardAsync),
            new CommandPaletteItem("Import Graph JSON", "Graph", "Merge graph JSON from the clipboard.", ImportGraphFromClipboardAsync),
            new CommandPaletteItem("Scan Projects Folder", "Projects", "Create or update project nodes from your configured projects directory.", ScanProjectsAsync),
            new CommandPaletteItem("Summarize Project", "Projects", "Use the selected LLM to summarize the selected project.", SummarizeSelectedProjectAsync),
            new CommandPaletteItem("Save Soul", "Persona", "Save the current Argus soul.md persona.", SaveSoulAsync),
            new CommandPaletteItem("Test LLM Connection", "AI", "Send a small smoke-test request with the selected LLM.", TestSelectedProviderAsync),
            new CommandPaletteItem("Search", "Search", "Run global node search.", SearchAsync),
            new CommandPaletteItem("Search Memories", "Memory", "Run local memory recall.", SearchMemoriesAsync),
            new CommandPaletteItem("New Conversation", "Chat", "Start a local conversation.", NewConversationAsync),
            new CommandPaletteItem("Ask Argus", "Agent", "Run the v1 local agent summary.", AskAgentAsync),
            new CommandPaletteItem("Create Manual Backup", "Database", "Create a manual database backup.", CreateManualBackupAsync),
            new CommandPaletteItem("Run Database Integrity Check", "Database", "Run PRAGMA quick_check.", RunIntegrityCheckAsync),
            new CommandPaletteItem("Go to Tool Audits", "View", "Open tool execution audits.", () => { ShowAudits(); return Task.CompletedTask; }),
            new CommandPaletteItem("Clear Tool Audits", "Database", "Clear all tool audits.", ClearAllAuditsAsync)
        });
    }

    private void FilterCommandPalette()
    {
        EnsurePaletteItems();
        var query = CommandPaletteQuery.Trim();
        var filtered = string.IsNullOrWhiteSpace(query)
            ? paletteItems
            : paletteItems
                .Where(item =>
                    item.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    item.Category.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    item.Description.Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToList();

        Replace(CommandPaletteItems, filtered);
        SelectedCommandPaletteItem = CommandPaletteItems.FirstOrDefault();
    }

    public Microsoft.UI.Xaml.Media.Brush TelegramGatewayColorBrush => TelegramGatewayStatusText switch
    {
        "Running" => new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 16, 185, 129)),
        "Disabled" => new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 107, 114, 128)),
        _ => new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 239, 68, 68))
    };

    public string TelegramGatewayStatusDisplayText => TelegramGatewayStatusText switch
    {
        "Running" => "Gateway ready",
        "Stopped" => "Gateway stopped",
        "Disabled" => "Gateway disabled",
        _ => TelegramGatewayStatusText
    };

    public bool IsThinkingEnabled
    {
        get => SelectedProvider?.ThinkingMode == "enabled";
        set
        {
            if (SelectedProvider is not null && CanToggleThinkingMode)
            {
                SelectedProvider.ThinkingMode = value ? "enabled" : "disabled";
                OnPropertyChanged(nameof(IsThinkingEnabled));
                OnPropertyChanged(nameof(ShowReasoningEffortPicker));
                _ = SaveProviderPreferenceAsync(SelectedProvider);
            }
        }
    }

    private void RefreshReasoningEffortOptions()
    {
        var profile = SelectedProvider;
        if (profile is null)
        {
            ReasoningEffortOptions.Clear();
            OnPropertyChanged(nameof(HasReasoningEffortOptions));
            OnPropertyChanged(nameof(HasReasoningControls));
            OnPropertyChanged(nameof(ShowReasoningEffortPicker));
            OnPropertyChanged(nameof(ReasoningCapabilityHelpText));
            OnPropertyChanged(nameof(SelectedReasoningEffort));
            return;
        }

        var metadata = !string.IsNullOrWhiteSpace(profile.Model) &&
            modelMetadata.TryGetValue(profile.Model, out var loadedMetadata)
            ? loadedMetadata
            : AiModelCatalog.FindKnownModel(profile.ProviderType, profile.BaseUrl, profile.Model);
        var efforts = metadata?.ReasoningEfforts?.Where(value => !string.IsNullOrWhiteSpace(value)).ToList() ?? [];
        Replace(ReasoningEffortOptions, efforts.Distinct(StringComparer.OrdinalIgnoreCase));
        var previousEffort = profile.ReasoningEffort;
        var previousThinkingMode = profile.ThinkingMode;
        var current = profile.ReasoningEffort;
        var normalized = ReasoningEffortOptions.FirstOrDefault(
            value => value.Equals(current, StringComparison.OrdinalIgnoreCase));
        normalized ??= current.Equals("max", StringComparison.OrdinalIgnoreCase)
            ? ReasoningEffortOptions.FirstOrDefault(value => value.Equals("xhigh", StringComparison.OrdinalIgnoreCase))
            : current.Equals("xhigh", StringComparison.OrdinalIgnoreCase)
                ? ReasoningEffortOptions.FirstOrDefault(value => value.Equals("max", StringComparison.OrdinalIgnoreCase))
                : null;
        normalized ??= ReasoningEffortOptions.FirstOrDefault(
            value => value.Equals("medium", StringComparison.OrdinalIgnoreCase));
        normalized ??= ReasoningEffortOptions.FirstOrDefault();

        suppressReasoningEffortUpdate = true;
        if (!string.IsNullOrWhiteSpace(normalized))
        {
            profile.ReasoningEffort = normalized;
        }

        if (!CanToggleThinkingMode && ReasoningEffortOptions.Count > 0)
        {
            profile.ThinkingMode = profile.ReasoningEffort.Equals("none", StringComparison.OrdinalIgnoreCase)
                ? "disabled"
                : "enabled";
        }

        suppressReasoningEffortUpdate = false;
        OnPropertyChanged(nameof(HasReasoningEffortOptions));
        OnPropertyChanged(nameof(HasReasoningControls));
        OnPropertyChanged(nameof(ShowReasoningEffortPicker));
        OnPropertyChanged(nameof(ReasoningCapabilityHelpText));
        OnPropertyChanged(nameof(IsThinkingEnabled));
        OnPropertyChanged(nameof(SelectedReasoningEffort));
        if (!string.Equals(previousEffort, profile.ReasoningEffort, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(previousThinkingMode, profile.ThinkingMode, StringComparison.OrdinalIgnoreCase))
        {
            _ = SaveProviderPreferenceAsync(profile);
        }
    }

    private async Task SaveProviderPreferenceAsync(AiProviderProfile profile)
    {
        var snapshot = CloneProviderProfile(profile);
        try
        {
            await providerPreferenceSaveLock.WaitAsync();
            try
            {
                await settingsService.SaveAiProviderProfileAsync(snapshot);
            }
            finally
            {
                providerPreferenceSaveLock.Release();
            }
        }
        catch (Exception ex)
        {
            SettingsStatus = $"Could not save {profile.Name} settings: {ex.Message}";
        }
    }

    private static AiProviderProfile CloneProviderProfile(AiProviderProfile profile)
    {
        return new AiProviderProfile
        {
            Id = profile.Id,
            Name = profile.Name,
            ProviderType = profile.ProviderType,
            BaseUrl = profile.BaseUrl,
            Model = profile.Model,
            ApiKeyStorageKey = profile.ApiKeyStorageKey,
            ThinkingMode = profile.ThinkingMode,
            ReasoningEffort = profile.ReasoningEffort,
            OrganizationId = profile.OrganizationId,
            ProjectId = profile.ProjectId,
            IsDefault = profile.IsDefault
        };
    }

    private async Task PersistDefaultProviderAsync(AiProviderProfile profile)
    {
        await SaveProviderPreferenceAsync(profile);
        var profiles = await settingsService.GetAiProviderProfilesAsync();

        void ApplyDefaultFlags()
        {
            foreach (var localProfile in ProviderProfiles)
            {
                var persisted = profiles.FirstOrDefault(candidate => candidate.Id == localProfile.Id);
                if (persisted is not null)
                {
                    localProfile.IsDefault = persisted.IsDefault;
                }
            }
        }

        if (App.DispatcherQueue is not null)
        {
            App.DispatcherQueue.TryEnqueue(ApplyDefaultFlags);
        }
        else
        {
            ApplyDefaultFlags();
        }
    }

    partial void OnTelegramGatewayStatusTextChanged(string value)
    {
        OnPropertyChanged(nameof(TelegramGatewayColorBrush));
        OnPropertyChanged(nameof(TelegramGatewayStatusDisplayText));
    }

    partial void OnSelectedProviderChanged(AiProviderProfile? value)
    {
        ReasoningEffortOptions.Clear();
        OnPropertyChanged(nameof(HasReasoningEffortOptions));
        OnPropertyChanged(nameof(HasReasoningControls));
        OnPropertyChanged(nameof(ShowReasoningEffortPicker));
        OnPropertyChanged(nameof(IsThinkingEnabled));
        OnPropertyChanged(nameof(SelectedReasoningEffort));
        OnPropertyChanged(nameof(IsOpenAiCodexProvider));
        OnPropertyChanged(nameof(IsAnthropicProvider));
        OnPropertyChanged(nameof(IsApiKeyRequired));
        OnPropertyChanged(nameof(ProviderAuthenticationHelpText));
        OnPropertyChanged(nameof(CanToggleThinkingMode));
        OnPropertyChanged(nameof(ReasoningCapabilityHelpText));

        if (suppressSelectedProviderSideEffects)
        {
            return;
        }

        if (value is not null && !value.IsDefault)
        {
            foreach (var profile in ProviderProfiles)
            {
                profile.IsDefault = profile.Id == value.Id;
            }

            _ = PersistDefaultProviderAsync(value);
        }

        _ = LoadModelOptionsForSelectedProviderAsync(forceRefresh: false);
        _ = UpdateApiKeyPlaceholderAsync(value);
        _ = UpdateProviderAuthenticationStatusAsync(value);
    }

    partial void OnSelectedModelChanged(string value)
    {
        if (suppressSelectedModelUpdate || SelectedProvider is null || string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (!string.Equals(SelectedProvider.Model, value, StringComparison.OrdinalIgnoreCase))
        {
            SelectedProvider.Model = value;
            RefreshReasoningEffortOptions();
            UpdateContextTracker(null, 0);
            _ = SaveProviderPreferenceAsync(SelectedProvider);
            OnPropertyChanged(nameof(SelectedProvider));
            OnPropertyChanged(nameof(ReasoningCapabilityHelpText));
        }
    }

    partial void OnIsWebSearchEnabledChanged(bool value)
    {
        _ = Task.Run(async () =>
        {
            await settingsService.SaveSettingAsync("IsWebSearchEnabled", value.ToString());
        });
    }

    private async Task UpdateApiKeyPlaceholderAsync(AiProviderProfile? profile)
    {
        if (profile is null ||
            aiProviderRegistry.GetCapabilities(profile).AuthenticationMode != AiAuthenticationMode.ApiKey)
        {
            if (App.DispatcherQueue is not null)
            {
                App.DispatcherQueue.TryEnqueue(() => ApiKeyPlaceholder = "Enter API key");
            }
            else
            {
                ApiKeyPlaceholder = "Enter API key";
            }
            return;
        }

        var storageKey = profile.ApiKeyStorageKey;
        if (string.IsNullOrWhiteSpace(storageKey))
        {
            storageKey = $"ai.{profile.Name.ToLowerInvariant().Replace(' ', '-')}.api_key";
        }

        var key = await secretStore.GetSecretAsync(storageKey);
        string placeholder;
        if (!string.IsNullOrWhiteSpace(key))
        {
            placeholder = "•••••••• (Saved)";
        }
        else
        {
            var status = await aiProviderRegistry.GetConnectionStatusAsync(profile);
            placeholder = status.UsesEnvironmentCredential
                ? "Configured via Env Var"
                : "Enter API key";
        }

        if (App.DispatcherQueue is not null)
        {
            App.DispatcherQueue.TryEnqueue(() => ApiKeyPlaceholder = placeholder);
        }
        else
        {
            ApiKeyPlaceholder = placeholder;
        }
    }

    private async Task UpdateProviderAuthenticationStatusAsync(AiProviderProfile? profile)
    {
        if (profile is null)
        {
            return;
        }

        var status = await aiProviderRegistry.GetConnectionStatusAsync(profile);
        if (SelectedProvider?.Id == profile.Id)
        {
            SettingsStatus = status.Message;
        }
    }

    private void UpdateTelegramTokenPlaceholder()
    {
        var placeholder = string.IsNullOrWhiteSpace(TelegramBotToken)
            ? "Telegram bot token"
            : "•••••••• (Saved)";
        if (App.DispatcherQueue is not null)
        {
            App.DispatcherQueue.TryEnqueue(() => TelegramTokenPlaceholder = placeholder);
        }
        else
        {
            TelegramTokenPlaceholder = placeholder;
        }
    }

    partial void OnTelegramBotTokenChanged(string value)
    {
        UpdateTelegramTokenPlaceholder();
    }

    public Microsoft.UI.Xaml.Visibility BottomChatVisibility => CurrentView is "Dashboard" ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;
    public int ConversationsRowSpan => CurrentView == "Conversations" ? 2 : 1;
    public bool IsChatEmpty => ChatMessages.Count == 0;

    partial void OnCurrentViewChanged(string value)
    {
        OnPropertyChanged(nameof(BottomChatVisibility));
        OnPropertyChanged(nameof(ConversationsRowSpan));
        if (string.Equals(value, "Dashboard", StringComparison.OrdinalIgnoreCase))
        {
            StartDashboardWidgets();
        }
        else
        {
            DashboardWidgets?.StopLiveMonitoring();
        }
    }

    [ObservableProperty]
    public partial bool ShowSystemStatusWidget { get; set; } = true;

    [ObservableProperty]
    public partial bool ShowProjectsWidget { get; set; } = true;

    [ObservableProperty]
    public partial bool ShowMarketWidget { get; set; } = true;

    [ObservableProperty]
    public partial bool ShowNewsWidget { get; set; } = true;

    [ObservableProperty]
    public partial bool ShowSportsWidget { get; set; } = true;

    public Microsoft.UI.Xaml.GridLength Column0Width => (ShowSystemStatusWidget || ShowProjectsWidget) 
        ? new Microsoft.UI.Xaml.GridLength(330) 
        : new Microsoft.UI.Xaml.GridLength(0);

    public Microsoft.UI.Xaml.GridLength Column2Width => (ShowMarketWidget || ShowNewsWidget || ShowSportsWidget) 
        ? new Microsoft.UI.Xaml.GridLength(400) 
        : new Microsoft.UI.Xaml.GridLength(0);

    partial void OnShowSystemStatusWidgetChanged(bool value)
    {
        SaveWidgetSetting("ShowSystemStatusWidget", value);
        OnPropertyChanged(nameof(Column0Width));
    }

    partial void OnShowProjectsWidgetChanged(bool value)
    {
        SaveWidgetSetting("ShowProjectsWidget", value);
        OnPropertyChanged(nameof(Column0Width));
        OnPropertyChanged(nameof(ShowProjectNextActionsWidget));
    }

    partial void OnProjectCockpitChanged(CoherentDashboard? value)
    {
        OnPropertyChanged(nameof(ShowProjectNextActionsWidget));
    }

    partial void OnShowMarketWidgetChanged(bool value)
    {
        SaveWidgetSetting("ShowMarketWidget", value);
        OnPropertyChanged(nameof(Column2Width));
    }

    partial void OnShowNewsWidgetChanged(bool value)
    {
        SaveWidgetSetting("ShowNewsWidget", value);
        OnPropertyChanged(nameof(Column2Width));
    }

    partial void OnShowSportsWidgetChanged(bool value)
    {
        SaveWidgetSetting("ShowSportsWidget", value);
        OnPropertyChanged(nameof(Column2Width));
    }

    private void SaveWidgetSetting(string key, bool value)
    {
        _ = Task.Run(async () => await settingsService.SaveSettingAsync(key, value.ToString()));
    }

    private static string BuildVersionDisplayText()
    {
        var version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        if (string.IsNullOrWhiteSpace(version))
        {
            version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";
        }

        var plusIndex = version.IndexOf('+');
        if (plusIndex >= 0)
        {
            version = version[..plusIndex];
        }

        return $"v{version}";
    }

    private static Version BuildVersion()
    {
        var text = BuildVersionDisplayText().TrimStart('v');
        return Version.TryParse(text, out var version) ? version : new Version(0, 0, 0);
    }

    // New properties and commands for Tool Audits and Database Maintenance
    public ObservableCollection<ToolExecutionAudit> AuditRecords { get; } = new();
    public ObservableCollection<string> AuditRiskFilterOptions { get; } = new() { "All", "ReadOnly", "Mutating", "Destructive" };
    public ObservableCollection<string> AuditApprovalFilterOptions { get; } = new() { "All", "approved", "auto_approved", "denied", "unknown" };
    public ObservableCollection<string> AuditOutcomeFilterOptions { get; } = new() { "All", "succeeded", "failed", "cancelled", "started" };

    [ObservableProperty]
    public partial string AuditFilterToolName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string AuditFilterRiskLevel { get; set; } = "All";

    [ObservableProperty]
    public partial string AuditFilterApprovalStatus { get; set; } = "All";

    [ObservableProperty]
    public partial string AuditFilterOutcome { get; set; } = "All";

    [ObservableProperty]
    public partial bool AuditFilterOnlyIncomplete { get; set; }

    [ObservableProperty]
    public partial ToolExecutionAudit? SelectedAuditRecord { get; set; }

    [ObservableProperty]
    public partial string DatabasePathDisplay { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DatabaseSizeDisplay { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DatabaseIntegrityDisplay { get; set; } = "Unknown";

    [ObservableProperty]
    public partial string CustomBackupName { get; set; } = string.Empty;

    public ObservableCollection<BackupFileInfo> BackupsCollection { get; } = new();

    [RelayCommand]
    private void ShowAudits()
    {
        CurrentView = "Audits";
        _ = LoadAuditsAsync();
    }

    public async Task LoadAuditsAsync()
    {
        try
        {
            var audits = await auditService.GetFilteredAsync(
                toolName: AuditFilterToolName,
                riskLevel: AuditFilterRiskLevel,
                approvalStatus: AuditFilterApprovalStatus,
                outcome: AuditFilterOutcome,
                onlyIncomplete: AuditFilterOnlyIncomplete);

            AuditRecords.Clear();
            foreach (var audit in audits)
            {
                AuditRecords.Add(audit);
            }
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to load audits: {ex.Message}";
        }
    }

    partial void OnAuditFilterToolNameChanged(string value) => _ = LoadAuditsAsync();
    partial void OnAuditFilterRiskLevelChanged(string value) => _ = LoadAuditsAsync();
    partial void OnAuditFilterApprovalStatusChanged(string value) => _ = LoadAuditsAsync();
    partial void OnAuditFilterOutcomeChanged(string value) => _ = LoadAuditsAsync();
    partial void OnAuditFilterOnlyIncompleteChanged(bool value) => _ = LoadAuditsAsync();

    public async Task RefreshDatabaseInfoAsync()
    {
        DatabasePathDisplay = databaseBackupService.DatabasePath;
        var sizeBytes = databaseBackupService.GetDatabaseSizeInBytes();
        DatabaseSizeDisplay = FormatSize(sizeBytes);
        await RefreshBackupsAsync();
    }

    private static string FormatSize(long bytes)
    {
        string[] suffixes = { "B", "KB", "MB", "GB" };
        int counter = 0;
        decimal number = bytes;
        while (Math.Round(number / 1024) >= 1)
        {
            number /= 1024;
            counter++;
        }
        return string.Format("{0:n1} {1}", number, suffixes[counter]);
    }

    public async Task RefreshBackupsAsync()
    {
        try
        {
            var backups = databaseBackupService.GetBackups();
            BackupsCollection.Clear();
            foreach (var b in backups)
            {
                BackupsCollection.Add(b);
            }
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to read backups: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task RunIntegrityCheckAsync()
    {
        DatabaseIntegrityDisplay = "Running check...";
        var result = await databaseBackupService.RunIntegrityCheckAsync();
        DatabaseIntegrityDisplay = result;
    }

    [RelayCommand]
    private async Task CreateManualBackupAsync()
    {
        try
        {
            var result = await databaseBackupService.CreateManualBackupAsync(
                string.IsNullOrWhiteSpace(CustomBackupName) ? "manual" : CustomBackupName);
            StatusText = result.Message;
            CustomBackupName = string.Empty;
            await RefreshDatabaseInfoAsync();
        }
        catch (Exception ex)
        {
            StatusText = $"Backup failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task RestoreBackupAsync(BackupFileInfo? backup)
    {
        if (backup is null) return;
        try
        {
            var result = await databaseBackupService.RestoreBackupAsync(backup.FilePath);
            StatusText = result.Message;
            await RefreshDatabaseInfoAsync();
            await LoadConversationsAsync();
            await LoadGraphAsync();
        }
        catch (Exception ex)
        {
            StatusText = $"Restore failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task DeleteBackupAsync(BackupFileInfo? backup)
    {
        if (backup is null) return;
        try
        {
            databaseBackupService.DeleteBackup(backup.FilePath);
            StatusText = $"Deleted backup {backup.FileName}.";
            await RefreshBackupsAsync();
        }
        catch (Exception ex)
        {
            StatusText = $"Delete failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task PruneAuditsAsync()
    {
        try
        {
            var limit = DateTimeOffset.UtcNow.AddDays(-30);
            var prunedCount = await auditService.PruneOldAuditsAsync(limit);
            StatusText = $"Pruned {prunedCount} audit records older than 30 days.";
            await LoadAuditsAsync();
        }
        catch (Exception ex)
        {
            StatusText = $"Pruning failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ClearAllAuditsAsync()
    {
        try
        {
            var clearedCount = await auditService.ClearAllAuditsAsync();
            StatusText = $"Cleared {clearedCount} audit records.";
            await LoadAuditsAsync();
        }
        catch (Exception ex)
        {
            StatusText = $"Clearing failed: {ex.Message}";
        }
    }
}

public sealed record CommandPaletteItem(string Title, string Category, string Description, Func<Task> ExecuteAsync);

public partial class MemoryRecallDisplayItem(MemoryRecallResult recall) : ObservableObject
{
    public MemoryRecallResult Recall { get; } = recall;

    [ObservableProperty]
    public partial string FeedbackText { get; set; } = string.Empty;

    public string Text => Recall.Memory.Text;
    public string ScoreText => $"{Recall.Score:P0}";
    public string MethodText => Recall.Method switch
    {
        MemoryRecallMethod.ExactPhrase => "EXACT PHRASE",
        MemoryRecallMethod.Keyword => "KEYWORD",
        MemoryRecallMethod.Semantic => "SEMANTIC",
        MemoryRecallMethod.Hybrid => "HYBRID",
        _ => "RECENT"
    };
    public string MetadataText =>
        $"Source: {Recall.Memory.Source}  |  Importance: {Recall.Memory.Importance}/5  |  Created: {Recall.Memory.CreatedAt.LocalDateTime:g}";
    public string Explanation => Recall.Explanation;
    public string ComponentScoresText =>
        $"Semantic {Recall.SemanticScore:P0}  |  Lexical {Recall.LexicalScore:P0}  |  Importance {Recall.ImportanceScore:P0}  |  Recency {Recall.RecencyScore:P0}";
}
