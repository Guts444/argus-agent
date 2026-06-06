using System.Collections.ObjectModel;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Argus.Core.Graph;
using Argus.Core.Models;
using Argus.Core.Services;
using Argus.AI.Services;
using Argus.App.Services;
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
    IAiChatService aiChatService,
    IAgentService agentService,
    IToolService toolService,
    ITelegramGatewayService telegramGatewayService,
    IAppUpdateService appUpdateService,
    HttpClient httpClient,
    ISecretStore secretStore) : ObservableObject
{
    private bool initialized;
    private bool suppressSelectedModelUpdate;
    private readonly List<CommandPaletteItem> paletteItems = new();
    private readonly Dictionary<string, AiModelMetadata> modelMetadata = new(StringComparer.OrdinalIgnoreCase);

    public ObservableCollection<Node> Nodes { get; } = new();
    public ObservableCollection<Edge> Edges { get; } = new();
    public ObservableCollection<Node> SearchResults { get; } = new();
    public ObservableCollection<NodeConnection> Connections { get; } = new();
    public ObservableCollection<Conversation> Conversations { get; } = new();
    public ObservableCollection<Message> ChatMessages { get; } = new();
    public ObservableCollection<Memory> Memories { get; } = new();
    public ObservableCollection<AiProviderProfile> ProviderProfiles { get; } = new();
    public ObservableCollection<string> AvailableTools { get; } = new();
    public ObservableCollection<string> ModelOptions { get; } = new();
    public ObservableCollection<CommandPaletteItem> CommandPaletteItems { get; } = new();
    public ObservableCollection<Tag> SelectedNodeTags { get; } = new();
    public ObservableCollection<ProjectContext> ProjectContexts { get; } = new();

    public IReadOnlyList<string> NodeTypes { get; } = new[]
    {
        "Project", "Idea", "Task", "Decision", "Note", "Person", "File", "Link", "Conversation", "Memory", "Tool", "Agent"
    };

    public IReadOnlyList<string> RelationshipTypes { get; } = new[]
    {
        "related_to", "depends_on", "inspired_by", "belongs_to", "blocked_by", "uses", "created_from", "discussed_in", "decided_in", "reminds_me_of"
    };

    public IReadOnlyList<string> ThinkingModeOptions { get; } = new[] { "enabled", "disabled" };

    public IReadOnlyList<string> ReasoningEffortOptions { get; } = new[] { "none", "minimal", "low", "medium", "high", "xhigh", "max" };

    public string VersionDisplayText { get; } = BuildVersionDisplayText();

    [ObservableProperty]
    public partial string UpdateButtonText { get; set; } = BuildVersionDisplayText();

    [ObservableProperty]
    public partial string UpdateStatusText { get; set; } = "Checking for updates...";

    [ObservableProperty]
    public partial string UpdateReleaseNotes { get; set; } = "Argus checks GitHub Releases for signed release metadata.";

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
    public partial string ChatInput { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ApiKeyInput { get; set; } = string.Empty;

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
    public partial string SettingsStatus { get; set; } = "Provider keys are stored with Windows Credential Locker.";

    [ObservableProperty]
    public partial string ContextTrackerText { get; set; } = "0/unknown";

    [ObservableProperty]
    public partial string ContextTrackerPercentText { get; set; } = "0%";

    [ObservableProperty]
    public partial double ContextTrackerPercent { get; set; }

    [ObservableProperty]
    public partial string ContextTrackerDetailText { get; set; } = "No model usage yet.";

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
    public partial AiProviderProfile? SelectedProvider { get; set; }

    [ObservableProperty]
    public partial string SelectedModel { get; set; } = string.Empty;

    [ObservableProperty]
    public partial DashboardSnapshot? Dashboard { get; set; }

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

    partial void OnSelectedNodeChanged(Node? value)
    {
        OnPropertyChanged(nameof(HasSelectedNode));
        OnPropertyChanged(nameof(IsSelectedNodePinned));
        _ = LoadConnectionsAsync(value?.Id);
        _ = LoadSelectedNodeTagsAsync(value?.Id);
        _ = LoadProjectContextAsync(value);
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
        StatusText = results.Count == 0 ? "No graph matches found." : $"{results.Count} graph matches found.";
    }

    [RelayCommand]
    private async Task SearchMemoriesAsync()
    {
        var results = await memoryService.SearchMemoriesAsync(MemorySearchText, 100);
        Replace(Memories, results);
        StatusText = results.Count == 0 ? "No memories matched." : $"{results.Count} memories recalled.";
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
            await PersistChatMessageAsMemoryNodeAsync(webUserMessage);

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
            await PersistChatMessageAsMemoryNodeAsync(webAssistantMessage);
            await LoadConversationsAsync(selectId: webConversationId);
            await LoadGraphAsync();
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
        await PersistChatMessageAsMemoryNodeAsync(userMessage);

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
            await PersistChatMessageAsMemoryNodeAsync(assistantMessage);
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

            var assistantText = result.Error is not null ? $"Provider error: {result.Error}" : result.Content;
            var assistantMessage = await conversationService.AddMessageAsync(conversationId, "assistant", assistantText, linkedNodeId);
            ChatMessages.Add(assistantMessage);
            await PersistChatMessageAsMemoryNodeAsync(assistantMessage);
            await LoadConversationsAsync(selectId: conversationId);
            await LoadGraphAsync();
        }
    }

    [RelayCommand]
    private async Task SaveMessageAsMemoryAsync(Message? message)
    {
        if (message is null)
        {
            return;
        }

        await memoryService.SaveMemoryAsync(message.Content, $"chat:{message.Role}", 4, message.LinkedNodeId);
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

        if (string.IsNullOrWhiteSpace(SelectedProvider.ApiKeyStorageKey))
        {
            SelectedProvider.ApiKeyStorageKey = $"ai.{SelectedProvider.Name.ToLowerInvariant().Replace(' ', '-')}.api_key";
        }

        if (!string.IsNullOrWhiteSpace(ApiKeyInput))
        {
            // API keys are stored outside SQLite in the Windows Credential Locker.
            await App.Services.GetRequiredService<ISecretStore>().SetSecretAsync(SelectedProvider.ApiKeyStorageKey, ApiKeyInput);
            ApiKeyInput = string.Empty;
        }

        var saved = await settingsService.SaveAiProviderProfileAsync(SelectedProvider);
        await LoadProvidersAsync(saved.Id);
        await LoadModelOptionsForSelectedProviderAsync(forceRefresh: false);
        SettingsStatus = "Provider settings saved.";
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
    private async Task OpenOpenAiLoginAsync()
    {
        await Launcher.LaunchUriAsync(new Uri("https://platform.openai.com/api-keys"));
        SettingsStatus = "Opened OpenAI account sign-in. Create or copy an API key, save it here, then refresh models.";
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

            Project: {{context.Name}}
            Path: {{context.Path}}
            State:
            {{context.StateSummary}}

            README:
            {{context.ReadmePreview}}

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
        var projectContext = SelectedNode is null
            ? "No selected project node."
            : ProjectContextText;
        var knownProjects = ProjectContexts.Count == 0
            ? "No local projects scanned."
            : string.Join(Environment.NewLine, ProjectContexts.Take(24).Select(project => $"- {project.Name}: {project.StateSummary.Replace(Environment.NewLine, " | ")}"));

        return new List<AiChatTurn>
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

    private async Task LoadProjectContextAsync(Node? node)
    {
        if (node is null)
        {
            ProjectContextText = "Select a project node to inspect README, GitHub remote, and working tree state.";
            return;
        }

        var context = await projectContextService.GetProjectContextAsync(node.Title);
        if (context is null)
        {
            ProjectContextText = "No matching local project folder found.";
            return;
        }

        ProjectContextText =
            $$"""
            {{context.Name}}
            {{context.Path}}

            {{context.StateSummary}}

            README preview:
            {{context.ReadmePreview}}
            """;
    }

    private async Task SyncProjectsFromWorkspaceAsync()
    {
        var contexts = await projectContextService.ScanProjectsAsync();
        Replace(ProjectContexts, contexts);

        var graph = await graphService.GetGraphAsync();
        var existingTitles = graph.Nodes.Select(node => node.Title).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var context in contexts)
        {
            if (existingTitles.Contains(context.Name))
            {
                continue;
            }

            await graphService.CreateNodeAsync(new Node
            {
                Title = context.Name,
                Type = "Project",
                Summary = FirstLine(context.ReadmePreview),
                Body = $"{context.StateSummary}{Environment.NewLine}{Environment.NewLine}{context.ReadmePreview}",
                Status = context.HasUncommittedChanges ? "Active" : "Scanned",
                Importance = context.ReadmePath is null ? 2 : 3,
                ColorKey = context.GitRemote?.Contains("github.com", StringComparison.OrdinalIgnoreCase) == true ? "blue" : "cyan",
                IconKey = "project"
            });
        }
    }

    private async Task PersistChatMessageAsMemoryNodeAsync(Message message)
    {
        if (string.IsNullOrWhiteSpace(message.Content))
        {
            return;
        }

        var source = $"chat:{message.Role}";
        var memory = await memoryService.SaveMemoryAsync(message.Content, source, message.Role == "assistant" ? 4 : 3, message.LinkedNodeId);
        var title = message.Content.Trim().ReplaceLineEndings(" ");
        if (title.Length > 70)
        {
            title = title[..70];
        }

        await graphService.CreateNodeAsync(new Node
        {
            Title = title,
            Type = "Memory",
            Summary = message.Content.Length > 220 ? message.Content[..220] : message.Content,
            Body = message.Content,
            Status = "Captured",
            Importance = message.Role == "assistant" ? 4 : 3,
            ColorKey = message.Role == "assistant" ? "violet" : "blue",
            IconKey = "memory",
            LastTouchedAt = memory.CreatedAt
        });
    }

    private static string FirstLine(string text)
    {
        return text.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "Local project";
    }

    private async Task LoadConversationsAsync(Guid? selectId = null)
    {
        var selectedId = selectId ?? SelectedConversation?.Id;
        Replace(Conversations, await conversationService.GetConversationsAsync());
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
        SelectedProvider = ProviderProfiles.FirstOrDefault(profile => profile.Id == selectedId)
            ?? ProviderProfiles.FirstOrDefault(profile => profile.IsDefault)
            ?? ProviderProfiles.FirstOrDefault();
        await LoadModelOptionsForSelectedProviderAsync(forceRefresh: false);
    }

    private async Task LoadModelOptionsForSelectedProviderAsync(bool forceRefresh)
    {
        ModelOptions.Clear();
        modelMetadata.Clear();
        if (SelectedProvider is null)
        {
            UpdateContextTracker(null, 0);
            return;
        }

        IReadOnlyList<AiModelMetadata> models = [];
        if (AiModelCatalog.IsDeepSeekProvider(SelectedProvider.ProviderType, SelectedProvider.BaseUrl))
        {
            models = AiModelCatalog.DeepSeekModels;
        }
        else if (AiModelCatalog.IsOpenRouterProvider(SelectedProvider.ProviderType, SelectedProvider.BaseUrl))
        {
            models = await LoadOpenRouterModelsAsync(forceRefresh);
        }
        else if (AiModelCatalog.IsOpenAiProvider(SelectedProvider.ProviderType, SelectedProvider.BaseUrl))
        {
            models = await LoadOpenAiModelsAsync(forceRefresh);
        }
        else
        {
            models = await LoadOpenAiCompatibleModelsAsync(forceRefresh);
        }

        if (models.Count == 0)
        {
            models = AiModelCatalog.GetKnownModels(SelectedProvider.ProviderType, SelectedProvider.BaseUrl);
        }

        foreach (var model in models)
        {
            if (!modelMetadata.ContainsKey(model.Id))
            {
                modelMetadata[model.Id] = model;
                ModelOptions.Add(model.Id);
            }
        }

        if (!string.IsNullOrWhiteSpace(SelectedProvider.Model) &&
            !ModelOptions.Contains(SelectedProvider.Model, StringComparer.OrdinalIgnoreCase))
        {
            ModelOptions.Insert(0, SelectedProvider.Model);
        }

        if (string.IsNullOrWhiteSpace(SelectedProvider.Model) && ModelOptions.Count > 0)
        {
            SelectedProvider.Model = ModelOptions[0];
        }

        suppressSelectedModelUpdate = true;
        SelectedModel = SelectedProvider.Model;
        suppressSelectedModelUpdate = false;
        UpdateContextTracker(null, 0);
    }

    private async Task<IReadOnlyList<AiModelMetadata>> LoadOpenRouterModelsAsync(bool forceRefresh)
    {
        var cacheKey = "ModelCatalog:OpenRouter";
        var cached = forceRefresh ? null : await settingsService.GetSettingAsync(cacheKey, null);
        if (!string.IsNullOrWhiteSpace(cached))
        {
            return AiModelCatalog.ParseOpenRouterModels(cached);
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://openrouter.ai/api/v1/models");
            var apiKey = await ResolveApiKeyAsync(SelectedProvider!);
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            }

            using var response = await httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                return AiModelCatalog.OpenRouterFallbackModels;
            }

            var body = await response.Content.ReadAsStringAsync();
            await settingsService.SaveSettingAsync(cacheKey, body);
            return AiModelCatalog.ParseOpenRouterModels(body)
                .Where(model => model.Id.StartsWith("deepseek/", StringComparison.OrdinalIgnoreCase) ||
                    model.Id.StartsWith("openai/", StringComparison.OrdinalIgnoreCase) ||
                    model.Id.StartsWith("anthropic/", StringComparison.OrdinalIgnoreCase) ||
                    model.Id.StartsWith("google/", StringComparison.OrdinalIgnoreCase) ||
                    model.Id.StartsWith("qwen/", StringComparison.OrdinalIgnoreCase) ||
                    model.Id.Equals("openrouter/auto", StringComparison.OrdinalIgnoreCase))
                .Take(250)
                .ToList();
        }
        catch
        {
            return AiModelCatalog.OpenRouterFallbackModels;
        }
    }

    private async Task<IReadOnlyList<AiModelMetadata>> LoadOpenAiModelsAsync(bool forceRefresh)
    {
        var cacheKey = "ModelCatalog:OpenAI";
        var cached = forceRefresh ? null : await settingsService.GetSettingAsync(cacheKey, null);
        if (!string.IsNullOrWhiteSpace(cached))
        {
            var cachedModels = AiModelCatalog.ParseOpenAiModels(cached);
            if (cachedModels.Count > 0)
            {
                return cachedModels;
            }
        }

        var apiKey = await ResolveApiKeyAsync(SelectedProvider!);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return AiModelCatalog.OpenAiModels;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, BuildModelsUrl(SelectedProvider!.BaseUrl));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            if (!string.IsNullOrWhiteSpace(SelectedProvider.OrganizationId))
            {
                request.Headers.TryAddWithoutValidation("OpenAI-Organization", SelectedProvider.OrganizationId);
            }

            if (!string.IsNullOrWhiteSpace(SelectedProvider.ProjectId))
            {
                request.Headers.TryAddWithoutValidation("OpenAI-Project", SelectedProvider.ProjectId);
            }

            using var response = await httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                return AiModelCatalog.OpenAiModels;
            }

            var body = await response.Content.ReadAsStringAsync();
            await settingsService.SaveSettingAsync(cacheKey, body);
            var models = AiModelCatalog.ParseOpenAiModels(body);
            return models.Count > 0 ? models : AiModelCatalog.OpenAiModels;
        }
        catch
        {
            return AiModelCatalog.OpenAiModels;
        }
    }

    private async Task<IReadOnlyList<AiModelMetadata>> LoadOpenAiCompatibleModelsAsync(bool forceRefresh)
    {
        if (!forceRefresh && !string.IsNullOrWhiteSpace(SelectedProvider?.Model))
        {
            return [new AiModelMetadata(SelectedProvider.Model, SelectedProvider.Model)];
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, BuildModelsUrl(SelectedProvider!.BaseUrl));
            var apiKey = await ResolveApiKeyAsync(SelectedProvider);
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            }

            using var response = await httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                return string.IsNullOrWhiteSpace(SelectedProvider.Model)
                    ? []
                    : [new AiModelMetadata(SelectedProvider.Model, SelectedProvider.Model)];
            }

            var body = await response.Content.ReadAsStringAsync();
            var parsed = AiModelCatalog.ParseOpenAiModels(body);
            return parsed.Count > 0
                ? parsed
                : string.IsNullOrWhiteSpace(SelectedProvider.Model)
                    ? []
                    : [new AiModelMetadata(SelectedProvider.Model, SelectedProvider.Model)];
        }
        catch
        {
            return string.IsNullOrWhiteSpace(SelectedProvider?.Model)
                ? []
                : [new AiModelMetadata(SelectedProvider.Model, SelectedProvider.Model)];
        }
    }

    private async Task<string?> ResolveApiKeyAsync(AiProviderProfile profile)
    {
        var key = await secretStore.GetSecretAsync(profile.ApiKeyStorageKey);
        if (!string.IsNullOrWhiteSpace(key))
        {
            return key;
        }

        var candidates = new List<string>();
        if (AiModelCatalog.IsDeepSeekProvider(profile.ProviderType, profile.BaseUrl))
        {
            candidates.Add("ARGUS_DEEPSEEK_API_KEY");
            candidates.Add("DEEPSEEK_API_KEY");
        }
        else if (AiModelCatalog.IsOpenAiProvider(profile.ProviderType, profile.BaseUrl))
        {
            candidates.Add("ARGUS_OPENAI_API_KEY");
            candidates.Add("OPENAI_API_KEY");
        }
        else if (AiModelCatalog.IsOpenRouterProvider(profile.ProviderType, profile.BaseUrl))
        {
            candidates.Add("ARGUS_OPENROUTER_API_KEY");
            candidates.Add("OPENROUTER_API_KEY");
        }

        candidates.Add($"ARGUS_{NormalizeEnvironmentName(profile.Name)}_API_KEY");
        return candidates
            .Select(Environment.GetEnvironmentVariable)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
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

    private void UpdateContextTracker(AiChatResult? result, int estimatedPromptTokens)
    {
        var contextLimit = GetSelectedContextWindowTokens();
        var usedTokens = result?.PromptTokens ?? estimatedPromptTokens;
        if (!contextLimit.HasValue || contextLimit <= 0)
        {
            ContextTrackerText = $"{AiModelCatalog.FormatTokenCount(usedTokens)}/unknown";
            ContextTrackerPercent = 0;
            ContextTrackerPercentText = "n/a";
            ContextTrackerDetailText = result is null
                ? "Waiting for model usage."
                : result.PromptTokens is null && estimatedPromptTokens > 0
                ? "Provider usage unavailable; showing local prompt estimate."
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
            ? "Provider usage unavailable; showing local prompt estimate."
            : "Provider-reported prompt usage.";
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

    private static string NormalizeEnvironmentName(string value)
    {
        return new string(value.Select(character => char.IsLetterOrDigit(character) ? char.ToUpperInvariant(character) : '_').ToArray());
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
            new CommandPaletteItem("Go to Settings", "View", "Configure AI providers.", () => { CurrentView = "Settings"; return Task.CompletedTask; }),
            new CommandPaletteItem("Create Node", "Graph", "Add a new idea node.", NewNodeAsync),
            new CommandPaletteItem("Create Edge", "Graph", "Connect selected node to target.", CreateEdgeAsync),
            new CommandPaletteItem("Save Selected Node", "Graph", "Persist inspector edits.", SaveSelectedNodeAsync),
            new CommandPaletteItem("Refresh Graph", "Graph", "Reload graph data.", RefreshGraphAsync),
            new CommandPaletteItem("Clear Graph Filter", "Graph", "Show every node type on the graph.", () => { ClearGraphFilter(); return Task.CompletedTask; }),
            new CommandPaletteItem("Save Tags", "Graph", "Save comma-separated tags on the selected node.", SaveSelectedNodeTagsAsync),
            new CommandPaletteItem("Export Graph JSON", "Graph", "Copy graph JSON to the clipboard.", ExportGraphToClipboardAsync),
            new CommandPaletteItem("Import Graph JSON", "Graph", "Merge graph JSON from the clipboard.", ImportGraphFromClipboardAsync),
            new CommandPaletteItem("Scan Projects Folder", "Projects", "Create/update project nodes from D:\\Projects.", ScanProjectsAsync),
            new CommandPaletteItem("Summarize Project", "Projects", "Use the selected provider to summarize the selected project.", SummarizeSelectedProjectAsync),
            new CommandPaletteItem("Save Soul", "Persona", "Save the current Argus soul.md persona.", SaveSoulAsync),
            new CommandPaletteItem("Test Provider Auth", "AI", "Send a small smoke-test request with the selected provider.", TestSelectedProviderAsync),
            new CommandPaletteItem("Search", "Search", "Run global node search.", SearchAsync),
            new CommandPaletteItem("Search Memories", "Memory", "Run local memory recall.", SearchMemoriesAsync),
            new CommandPaletteItem("New Conversation", "Chat", "Start a local conversation.", NewConversationAsync),
            new CommandPaletteItem("Ask Argus", "Agent", "Run the v1 local agent summary.", AskAgentAsync)
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
            if (SelectedProvider is not null)
            {
                SelectedProvider.ThinkingMode = value ? "enabled" : "disabled";
                OnPropertyChanged(nameof(IsThinkingEnabled));
                _ = Task.Run(async () =>
                {
                    await settingsService.SaveAiProviderProfileAsync(SelectedProvider);
                });
            }
        }
    }

    public bool ReasoningEffortMinimal
    {
        get => SelectedProvider?.ReasoningEffort?.ToLowerInvariant() == "minimal";
        set { if (value && SelectedProvider is not null) { SelectedProvider.ReasoningEffort = "minimal"; SaveSelectedProviderEffort("minimal"); } }
    }
    public bool ReasoningEffortLow
    {
        get => SelectedProvider?.ReasoningEffort?.ToLowerInvariant() == "low";
        set { if (value && SelectedProvider is not null) { SelectedProvider.ReasoningEffort = "low"; SaveSelectedProviderEffort("low"); } }
    }
    public bool ReasoningEffortMedium
    {
        get => SelectedProvider?.ReasoningEffort?.ToLowerInvariant() == "medium";
        set { if (value && SelectedProvider is not null) { SelectedProvider.ReasoningEffort = "medium"; SaveSelectedProviderEffort("medium"); } }
    }
    public bool ReasoningEffortHigh
    {
        get => SelectedProvider?.ReasoningEffort?.ToLowerInvariant() == "high";
        set { if (value && SelectedProvider is not null) { SelectedProvider.ReasoningEffort = "high"; SaveSelectedProviderEffort("high"); } }
    }
    public bool ReasoningEffortMax
    {
        get => SelectedProvider?.ReasoningEffort?.ToLowerInvariant() == "max" || SelectedProvider?.ReasoningEffort?.ToLowerInvariant() == "xhigh";
        set { if (value && SelectedProvider is not null) { SelectedProvider.ReasoningEffort = "max"; SaveSelectedProviderEffort("max"); } }
    }

    private void SaveSelectedProviderEffort(string effort)
    {
        OnPropertyChanged(nameof(ReasoningEffortMinimal));
        OnPropertyChanged(nameof(ReasoningEffortLow));
        OnPropertyChanged(nameof(ReasoningEffortMedium));
        OnPropertyChanged(nameof(ReasoningEffortHigh));
        OnPropertyChanged(nameof(ReasoningEffortMax));
        _ = Task.Run(async () =>
        {
            if (SelectedProvider is not null)
            {
                await settingsService.SaveAiProviderProfileAsync(SelectedProvider);
            }
        });
    }

    partial void OnTelegramGatewayStatusTextChanged(string value)
    {
        OnPropertyChanged(nameof(TelegramGatewayColorBrush));
        OnPropertyChanged(nameof(TelegramGatewayStatusDisplayText));
    }

    partial void OnSelectedProviderChanged(AiProviderProfile? value)
    {
        OnPropertyChanged(nameof(IsThinkingEnabled));
        OnPropertyChanged(nameof(ReasoningEffortMinimal));
        OnPropertyChanged(nameof(ReasoningEffortLow));
        OnPropertyChanged(nameof(ReasoningEffortMedium));
        OnPropertyChanged(nameof(ReasoningEffortHigh));
        OnPropertyChanged(nameof(ReasoningEffortMax));
        _ = LoadModelOptionsForSelectedProviderAsync(forceRefresh: false);

        if (value is not null && !value.IsDefault)
        {
            value.IsDefault = true;
            _ = Task.Run(async () =>
            {
                await settingsService.SaveAiProviderProfileAsync(value);
                var profiles = await settingsService.GetAiProviderProfilesAsync();
                App.DispatcherQueue.TryEnqueue(() =>
                {
                    foreach (var p in ProviderProfiles)
                    {
                        var dbProfile = profiles.FirstOrDefault(dp => dp.Id == p.Id);
                        if (dbProfile is not null)
                        {
                            p.IsDefault = dbProfile.IsDefault;
                        }
                    }
                });
            });
        }
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
            UpdateContextTracker(null, 0);
            _ = Task.Run(async () => await settingsService.SaveAiProviderProfileAsync(SelectedProvider));
            OnPropertyChanged(nameof(SelectedProvider));
        }
    }

    partial void OnIsWebSearchEnabledChanged(bool value)
    {
        _ = Task.Run(async () =>
        {
            await settingsService.SaveSettingAsync("IsWebSearchEnabled", value.ToString());
        });
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
}

public sealed record CommandPaletteItem(string Title, string Category, string Description, Func<Task> ExecuteAsync);
