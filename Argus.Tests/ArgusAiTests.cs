using System.Net;
using System.Text;
using System.Text.Json;
using Argus.AI.Services;
using Argus.Core.Models;
using Argus.Core.Services;
using Argus.Data;
using Argus.Data.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;

namespace Argus.Tests;

public sealed class ArgusAiTests
{
    [Fact]
    public void CurrentProviderCatalogUsesPublishedContextAndReasoningLimits()
    {
        var openAi = Assert.Single(AiModelCatalog.OpenAiModels, model => model.Id == "gpt-5.5");
        Assert.Equal(1_050_000, openAi.ContextWindowTokens);
        Assert.Equal(128_000, openAi.MaxOutputTokens);
        Assert.Equal(["none", "low", "medium", "high", "xhigh"], openAi.ReasoningEfforts);

        var codex = Assert.Single(AiModelCatalog.CodexModels, model => model.Id == "gpt-5.5");
        Assert.Equal(258_000, codex.ContextWindowTokens);
        Assert.Equal(["low", "medium", "high", "xhigh"], codex.ReasoningEfforts);

        var deepSeek = Assert.Single(AiModelCatalog.DeepSeekModels, model => model.Id == "deepseek-v4-pro");
        Assert.Equal(1_000_000, deepSeek.ContextWindowTokens);
        Assert.Equal(393_216, deepSeek.MaxOutputTokens);
        Assert.Equal(["high", "max"], deepSeek.ReasoningEfforts);
    }

    [Fact]
    public void ProjectOutboundPreviewOmitsPathsCredentialsAndSensitiveValues()
    {
        var context = new ProjectContext(
            "Argus",
            @"D:\Private\Argus",
            @"D:\Private\Argus\README.md",
            """
            # Argus
            A local-first workspace.
            API_KEY=should-not-leave-device
            """,
            """
            README: README.md
            Git branch: main
            GitHub remote: https://oauth2:private-token@github.com/Guts444/argus-agent.git
            Working tree: has local changes
            Changed files:  M .env,  M src/App.cs
            """,
            "main",
            "https://oauth2:private-token@github.com/Guts444/argus-agent.git",
            true);

        var preview = ProjectContextPrivacy.BuildOutboundPreview(context);

        Assert.DoesNotContain(@"D:\Private", preview, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private-token", preview, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("should-not-leave-device", preview, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".env", preview, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("github.com/Guts444/argus-agent.git", preview);
        Assert.Contains("[sensitive value omitted]", preview);
        Assert.Contains("[sensitive file omitted]", preview);
    }

    [Fact]
    public async Task LocalOpenAiCompatibleEndpointDoesNotRequireApiKey()
    {
        var handler = new CaptureHandler();
        var chat = new OpenAiCompatibleChatService(new HttpClient(handler), new EmptySecretStore());
        var profile = new AiProviderProfile
        {
            Name = "Local",
            BaseUrl = "http://127.0.0.1:11434/v1",
            Model = "local-model",
            ApiKeyStorageKey = "local"
        };

        var result = await chat.SendAsync(profile, new[] { new AiChatTurn("user", "Hello") });

        Assert.False(result.SetupRequired);
        Assert.Null(result.Error);
        Assert.Equal("local response", result.Content);
        Assert.Null(handler.LastRequest?.Headers.Authorization);
    }

    [Fact]
    public async Task RemoteOpenAiCompatibleEndpointStillRequiresApiKey()
    {
        var chat = new OpenAiCompatibleChatService(new HttpClient(new CaptureHandler()), new EmptySecretStore());
        var profile = new AiProviderProfile
        {
            Name = "Remote",
            BaseUrl = "https://api.example.com/v1",
            Model = "remote-model",
            ApiKeyStorageKey = "remote"
        };

        var result = await chat.SendAsync(profile, new[] { new AiChatTurn("user", "Hello") });

        Assert.True(result.SetupRequired);
    }

    [Fact]
    public async Task OpenAiCodexProfileDispatchesWithoutApiKey()
    {
        var handler = new CaptureHandler();
        var codex = new FakeOpenAiCodexService();
        var chat = new OpenAiCompatibleChatService(
            new HttpClient(handler),
            new EmptySecretStore(),
            codex);
        var profile = new AiProviderProfile
        {
            Name = "OpenAI Codex (ChatGPT)",
            ProviderType = "OpenAICodex",
            BaseUrl = "codex://app-server",
            Model = "gpt-5.5",
            ApiKeyStorageKey = string.Empty
        };

        var result = await chat.SendAsync(profile, [new AiChatTurn("user", "Hello")]);

        Assert.Equal("codex response", result.Content);
        Assert.Equal(1, codex.SendCount);
        Assert.Null(handler.LastRequest);
    }

    [Fact]
    public async Task ProviderRouterRoutesCodexAndOpenAiCompatibleProfilesToTheirAdapters()
    {
        var handler = new CaptureHandler();
        var codex = new FakeOpenAiCodexService();
        var compatible = new OpenAiCompatibleChatService(
            new HttpClient(handler),
            new StaticSecretStore("sk-test"));
        var router = new AiProviderRouter(
        [
            compatible,
            new CodexProviderAdapter(codex)
        ]);

        var codexResult = await router.SendAsync(
            new AiProviderProfile
            {
                Name = "OpenAI Codex (ChatGPT)",
                ProviderType = "OpenAICodex",
                BaseUrl = "codex://app-server",
                Model = "gpt-5.5"
            },
            [new AiChatTurn("user", "Hello")]);

        Assert.Equal("codex response", codexResult.Content);
        Assert.Equal(1, codex.SendCount);
        Assert.Null(handler.LastRequest);

        var apiResult = await router.SendAsync(
            new AiProviderProfile
            {
                Name = "OpenAI",
                ProviderType = "OpenAI",
                BaseUrl = "https://api.openai.com/v1",
                Model = "gpt-5.5",
                ApiKeyStorageKey = "openai"
            },
            [new AiChatTurn("user", "Hello")]);

        Assert.Equal("local response", apiResult.Content);
        Assert.NotNull(handler.LastRequest);
    }

    [Fact]
    public void ProviderCapabilitiesAreExplicitAndProviderSpecific()
    {
        var compatible = new OpenAiCompatibleChatService(
            new HttpClient(new CaptureHandler()),
            new EmptySecretStore());

        var deepSeek = compatible.GetCapabilities(new AiProviderProfile
        {
            ProviderType = "DeepSeek",
            BaseUrl = "https://api.deepseek.com"
        });
        Assert.Equal(AiProviderKind.DeepSeek, deepSeek.Kind);
        Assert.Equal(AiAuthenticationMode.ApiKey, deepSeek.AuthenticationMode);
        Assert.True(deepSeek.SupportsThinkingToggle);
        Assert.False(deepSeek.SupportsEmbeddings);

        var local = compatible.GetCapabilities(new AiProviderProfile
        {
            ProviderType = "Local",
            BaseUrl = "http://127.0.0.1:11434/v1"
        });
        Assert.Equal(AiProviderKind.Local, local.Kind);
        Assert.Equal(AiAuthenticationMode.LocalOptional, local.AuthenticationMode);
        Assert.True(local.SupportsEmbeddings);

        var codex = new CodexProviderAdapter(new FakeOpenAiCodexService())
            .GetCapabilities(new AiProviderProfile { ProviderType = "OpenAICodex" });
        Assert.Equal(AiAuthenticationMode.CodexAccount, codex.AuthenticationMode);
        Assert.True(codex.ReasoningAlwaysEnabled);
        Assert.False(codex.SupportsThinkingToggle);
        Assert.False(codex.SupportsEmbeddings);
    }

    [Fact]
    public async Task ProviderAdapterDiscoversModelsForLocalOpenAiCompatibleEndpoint()
    {
        var adapter = new OpenAiCompatibleChatService(
            new HttpClient(new ModelCatalogHandler()),
            new EmptySecretStore());
        var models = await adapter.ListModelsAsync(
            new AiProviderProfile
            {
                Name = "Local",
                ProviderType = "Local",
                BaseUrl = "http://localhost:1234/v1",
                Model = "old-model"
            },
            forceRefresh: true);

        Assert.Contains(models, model => model.Id == "local-instruct");
        Assert.Contains(models, model => model.Id == "local-reasoner");
    }

    [Fact]
    public async Task OpenAiProfileSendsBearerAndRoutingHeaders()
    {
        var handler = new CaptureHandler();
        var chat = new OpenAiCompatibleChatService(new HttpClient(handler), new StaticSecretStore("sk-test"));
        var profile = new AiProviderProfile
        {
            Name = "OpenAI",
            ProviderType = "OpenAI",
            BaseUrl = "https://api.openai.com/v1",
            Model = "gpt-5.5",
            ApiKeyStorageKey = "openai",
            ThinkingMode = "enabled",
            ReasoningEffort = "xhigh",
            OrganizationId = "org_test",
            ProjectId = "proj_test"
        };

        var result = await chat.SendAsync(profile, new[] { new AiChatTurn("user", "Hello") });

        Assert.Null(result.Error);
        Assert.NotNull(handler.LastRequest);
        Assert.Equal("Bearer", handler.LastRequest.Headers.Authorization?.Scheme);
        Assert.Equal("sk-test", handler.LastRequest.Headers.Authorization?.Parameter);
        Assert.True(handler.LastRequest.Headers.TryGetValues("OpenAI-Organization", out var organizations));
        Assert.Contains("org_test", organizations!);
        Assert.True(handler.LastRequest.Headers.TryGetValues("OpenAI-Project", out var projects));
        Assert.Contains("proj_test", projects!);
        Assert.True(handler.LastRequest.Headers.Contains("X-Client-Request-Id"));
        Assert.Contains("\"reasoning_effort\":\"xhigh\"", handler.LastBody);
        Assert.DoesNotContain("temperature", handler.LastBody);
    }

    [Fact]
    public async Task OpenAiReasoningEffortDoesNotDependOnLegacyThinkingToggle()
    {
        var handler = new CaptureHandler();
        var chat = new OpenAiCompatibleChatService(new HttpClient(handler), new StaticSecretStore("sk-test"));
        var profile = new AiProviderProfile
        {
            Name = "OpenAI",
            ProviderType = "OpenAI",
            BaseUrl = "https://api.openai.com/v1",
            Model = "gpt-5.5",
            ApiKeyStorageKey = "openai",
            ThinkingMode = "disabled",
            ReasoningEffort = "high"
        };

        var result = await chat.SendAsync(profile, [new AiChatTurn("user", "Hello")]);

        Assert.Null(result.Error);
        Assert.Contains("\"reasoning_effort\":\"high\"", handler.LastBody);
        Assert.DoesNotContain("temperature", handler.LastBody);
    }

    [Fact]
    public async Task OpenRouterProfileSendsReasoningPayloadAndHeaders()
    {
        var handler = new CaptureHandler();
        var chat = new OpenAiCompatibleChatService(new HttpClient(handler), new StaticSecretStore("sk-or-test"));
        var profile = new AiProviderProfile
        {
            Name = "OpenRouter",
            ProviderType = "OpenRouter",
            BaseUrl = "https://openrouter.ai/api/v1",
            Model = "deepseek/deepseek-v4-pro",
            ApiKeyStorageKey = "openrouter",
            ThinkingMode = "enabled",
            ReasoningEffort = "max"
        };

        var result = await chat.SendAsync(profile, new[] { new AiChatTurn("user", "Hello") });

        Assert.Null(result.Error);
        Assert.NotNull(handler.LastRequest);
        Assert.Equal("Bearer", handler.LastRequest.Headers.Authorization?.Scheme);
        Assert.Contains("sk-or-test", handler.LastRequest.Headers.Authorization?.Parameter);
        Assert.True(handler.LastRequest.Headers.Contains("HTTP-Referer"));
        Assert.True(handler.LastRequest.Headers.Contains("X-Title"));
        Assert.Contains("\"reasoning\"", handler.LastBody);
        Assert.Contains("\"effort\":\"xhigh\"", handler.LastBody);
        Assert.DoesNotContain("temperature", handler.LastBody);
    }

    [Fact]
    public async Task DeepSeekV4ProSendsThinkingMaxPayload()
    {
        var handler = new CaptureHandler();
        var chat = new OpenAiCompatibleChatService(new HttpClient(handler), new StaticSecretStore("sk-ds-test"));
        var profile = new AiProviderProfile
        {
            Name = "DeepSeek",
            ProviderType = "DeepSeek",
            BaseUrl = "https://api.deepseek.com",
            Model = "deepseek-v4-pro",
            ApiKeyStorageKey = "deepseek",
            ThinkingMode = "enabled",
            ReasoningEffort = "max"
        };

        var result = await chat.SendAsync(profile, [new AiChatTurn("user", "Hello")]);

        Assert.Null(result.Error);
        Assert.Contains("\"thinking\":{\"type\":\"enabled\"}", handler.LastBody);
        Assert.Contains("\"reasoning_effort\":\"max\"", handler.LastBody);
        Assert.DoesNotContain("temperature", handler.LastBody);
    }

    [Fact]
    public async Task DeepSeekThinkingCanBeDisabledExplicitly()
    {
        var handler = new CaptureHandler();
        var chat = new OpenAiCompatibleChatService(new HttpClient(handler), new StaticSecretStore("sk-ds-test"));
        var profile = new AiProviderProfile
        {
            Name = "DeepSeek",
            ProviderType = "DeepSeek",
            BaseUrl = "https://api.deepseek.com",
            Model = "deepseek-v4-pro",
            ApiKeyStorageKey = "deepseek",
            ThinkingMode = "disabled",
            ReasoningEffort = "max"
        };

        var result = await chat.SendAsync(profile, [new AiChatTurn("user", "Hello")]);

        Assert.Null(result.Error);
        Assert.Contains("\"thinking\":{\"type\":\"disabled\"}", handler.LastBody);
        Assert.DoesNotContain("reasoning_effort", handler.LastBody);
        Assert.Contains("\"temperature\":0.4", handler.LastBody);
    }

    [Fact]
    public async Task AnthropicProfileUsesMessagesFormatWithoutSamplingOverride()
    {
        var handler = new AnthropicCaptureHandler();
        var chat = new OpenAiCompatibleChatService(new HttpClient(handler), new StaticSecretStore("sk-ant-test"));
        var profile = new AiProviderProfile
        {
            Name = "Anthropic",
            ProviderType = "Anthropic",
            BaseUrl = "https://api.anthropic.com/v1",
            Model = "claude-sonnet-4-6",
            ApiKeyStorageKey = "anthropic"
        };

        var result = await chat.SendAsync(
            profile,
            [
                new AiChatTurn("system", "Be concise."),
                new AiChatTurn("user", "Hello")
            ]);

        Assert.Equal("anthropic response", result.Content);
        Assert.Contains("\"system\":\"Be concise.\"", handler.LastBody);
        Assert.Contains("\"role\":\"user\"", handler.LastBody);
        Assert.DoesNotContain("\"role\":\"system\"", handler.LastBody);
        Assert.DoesNotContain("temperature", handler.LastBody);
        Assert.True(handler.LastRequest!.Headers.Contains("x-api-key"));
        Assert.True(handler.LastRequest.Headers.Contains("anthropic-version"));
    }

    [Fact]
    public async Task AgentPlansFromGraphAndMemoryWithoutSideEffects()
    {
        using var provider = CreateProvider();
        await provider.GetRequiredService<ArgusDatabaseInitializer>().InitializeAsync();
        var graph = provider.GetRequiredService<IGraphService>();
        var memories = provider.GetRequiredService<IMemoryService>();
        var agent = new AgentService(graph, memories);

        var before = await graph.GetGraphAsync();
        await memories.SaveMemoryAsync("Argus keeps durable local-first context.", "test", 5);

        var plan = await agent.PlanAsync("Argus local-first");
        var after = await graph.GetGraphAsync();

        Assert.Contains(plan.MatchingNodes, node => node.Title == "Argus");
        Assert.Contains(plan.MatchingMemories, memory => memory.Text.Contains("local-first", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.ProposedActions, action => action.ActionType == "ReviewNode");
        Assert.Equal(before.Nodes.Count, after.Nodes.Count);
        Assert.Equal(before.Edges.Count, after.Edges.Count);
    }

    private static ServiceProvider CreateProvider()
    {
        var path = Path.Combine(Path.GetTempPath(), "ArgusTests", $"{Guid.NewGuid():N}.db");
        var services = new ServiceCollection();
        services.AddArgusData(path);
        return services.BuildServiceProvider();
    }

    private sealed class EmptySecretStore : ISecretStore
    {
        public Task<string?> GetSecretAsync(string key, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<string?>(null);
        }

        public Task SetSecretAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task RemoveSecretAsync(string key, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class StaticSecretStore(string secret) : ISecretStore
    {
        public Task<string?> GetSecretAsync(string key, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<string?>(secret);
        }

        public Task SetSecretAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task RemoveSecretAsync(string key, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string LastBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastBody = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"choices":[{"message":{"content":"local response"}}]}""",
                    Encoding.UTF8,
                    "application/json")
            };
            return response;
        }
    }

    private sealed class ModelCatalogHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "data": [
                        { "id": "local-instruct" },
                        { "id": "local-reasoner" }
                      ]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json")
            });
        }
    }

    private sealed class AnthropicCaptureHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string LastBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastBody = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"content":[{"type":"text","text":"anthropic response"}],"model":"claude-sonnet-4-6"}""",
                    Encoding.UTF8,
                    "application/json")
            };
        }
    }

    [Fact]
    public async Task VectorMemoryRecallCalculatesCosineSimilarity()
    {
        var services = new ServiceCollection();
        var dbPath = Path.Combine(Path.GetTempPath(), "ArgusTests", $"{Guid.NewGuid():N}.db");
        services.AddArgusData(dbPath);

        var mockChat = new MockEmbeddingChatService();
        var mockSettings = new SimpleSettingsService();

        var provider = services.BuildServiceProvider();
        await provider.GetRequiredService<ArgusDatabaseInitializer>().InitializeAsync();

        var dbFactory = provider.GetRequiredService<IDbContextFactory<ArgusDbContext>>();

        var memoryService = new LocalMemoryService(dbFactory, mockSettings, mockChat);

        // Save memories
        await memoryService.SaveMemoryAsync("memory1", "test", 3);
        await memoryService.SaveMemoryAsync("memory2", "test", 3);

        // Recall
        var results = await memoryService.RecallWithDetailsAsync("query");

        Assert.NotEmpty(results);
        Assert.Equal("memory1", results[0].Memory.Text);
        Assert.Equal(MemoryRecallMethod.Semantic, results[0].Method);
        Assert.Equal(1, results[0].SemanticScore);
        Assert.Contains("semantic similarity", results[0].Explanation);
    }

    [Fact]
    public async Task MemorySaveAndRecallFallBackWhenEmbeddingProviderFails()
    {
        var services = new ServiceCollection();
        var dbPath = Path.Combine(Path.GetTempPath(), "ArgusTests", $"{Guid.NewGuid():N}.db");
        services.AddArgusData(dbPath);
        using var provider = services.BuildServiceProvider();
        await provider.GetRequiredService<ArgusDatabaseInitializer>().InitializeAsync();

        var memoryService = new LocalMemoryService(
            provider.GetRequiredService<IDbContextFactory<ArgusDbContext>>(),
            new SimpleSettingsService(),
            new FailingEmbeddingChatService());

        var saved = await memoryService.SaveMemoryAsync(
            "Embedding outages must not disable local memory.",
            "test:fallback",
            4);
        var recalled = await memoryService.RecallWithDetailsAsync("local memory", 5);

        Assert.Null(saved.EmbeddingJson);
        Assert.Contains(recalled, result =>
            result.Memory.Id == saved.Id &&
            result.Method is MemoryRecallMethod.Keyword or MemoryRecallMethod.ExactPhrase);
    }

    [Fact]
    public async Task AgentExecutionLoopExecutesToolsStepByStep()
    {
        using var provider = CreateProvider();
        await provider.GetRequiredService<ArgusDatabaseInitializer>().InitializeAsync();
        var graph = provider.GetRequiredService<IGraphService>();
        var memories = provider.GetRequiredService<IMemoryService>();

        var mockChat = new MockAgentLoopChatService();
        var mockSettings = new SimpleSettingsService();
        var toolService = new ToolService(
            graph,
            memories,
            auditService: provider.GetRequiredService<IToolExecutionAuditService>());

        var agent = new AgentService(
            graph,
            memories,
            toolService,
            mockSettings,
            mockChat,
            toolApprovalService: new AllowToolApprovalService());

        var log = await agent.RunAsync("Create a note for testing the agent");

        // Assertions
        Assert.Contains("- **Executing Action:** `CreateNode`", log);
        Assert.Contains("- **Execution ID:** `", log);
        Assert.Contains("- **Decision:** Task completed.", log);

        var nodes = await graph.SearchNodesAsync("Agent Created Node");
        Assert.Contains(nodes, n => n.Title == "Agent Created Node");
    }

    [Fact]
    public async Task AgentExecutionLoopDeniesMutationWithoutExecutingTool()
    {
        using var provider = CreateProvider();
        await provider.GetRequiredService<ArgusDatabaseInitializer>().InitializeAsync();
        var graph = provider.GetRequiredService<IGraphService>();
        var memories = provider.GetRequiredService<IMemoryService>();
        var toolService = new ToolService(
            graph,
            memories,
            auditService: provider.GetRequiredService<IToolExecutionAuditService>());
        var agent = new AgentService(
            graph,
            memories,
            toolService,
            new SimpleSettingsService(),
            new MockAgentLoopChatService(),
            toolApprovalService: new DenyToolApprovalService());

        var (answer, log) = await agent.RunWithDetailsAsync("Create a note that must be approved");

        Assert.Contains("did not execute `CreateNode`", answer);
        Assert.Contains("**Approval:** Denied.", log);
        Assert.DoesNotContain(await graph.SearchNodesAsync("Agent Created Node"), node => node.Title == "Agent Created Node");
    }

    [Fact]
    public async Task AgentExecutionLoopFailsClosedWhenApprovalServiceIsMissing()
    {
        using var provider = CreateProvider();
        await provider.GetRequiredService<ArgusDatabaseInitializer>().InitializeAsync();
        var graph = provider.GetRequiredService<IGraphService>();
        var memories = provider.GetRequiredService<IMemoryService>();
        var agent = new AgentService(
            graph,
            memories,
            new ToolService(
                graph,
                memories,
                auditService: provider.GetRequiredService<IToolExecutionAuditService>()),
            new SimpleSettingsService(),
            new MockAgentLoopChatService());

        var (answer, log) = await agent.RunWithDetailsAsync("Create a note without an approval surface");

        Assert.Contains("no approval service is available", answer);
        Assert.Contains("**Approval:** Denied.", log);
        Assert.DoesNotContain(await graph.SearchNodesAsync("Agent Created Node"), node => node.Title == "Agent Created Node");
    }

    [Fact]
    public async Task AgentConversationHistoryFiltersThinkingAndDoesNotDuplicateCurrentInstruction()
    {
        using var provider = CreateProvider();
        await provider.GetRequiredService<ArgusDatabaseInitializer>().InitializeAsync();
        var graph = provider.GetRequiredService<IGraphService>();
        var memories = provider.GetRequiredService<IMemoryService>();
        var conversations = provider.GetRequiredService<IConversationService>();
        var conversation = await conversations.CreateConversationAsync("Agent history");
        await conversations.AddMessageAsync(conversation.Id, "user", "Earlier question");
        await conversations.AddMessageAsync(conversation.Id, "thinking", "Private execution trace");
        await conversations.AddMessageAsync(conversation.Id, "assistant", "Earlier answer");
        await conversations.AddMessageAsync(conversation.Id, "user", "Current instruction");

        var chat = new CapturingTerminalChatService();
        var agent = new AgentService(
            graph,
            memories,
            new ToolService(
                graph,
                memories,
                auditService: provider.GetRequiredService<IToolExecutionAuditService>()),
            new SimpleSettingsService(),
            chat,
            conversationService: conversations);

        await agent.RunWithDetailsAsync("Current instruction", conversation.Id);

        var sent = Assert.Single(chat.Requests);
        Assert.DoesNotContain(sent, turn => turn.Role == "thinking");
        Assert.Equal(2, sent.Count(turn => turn.Role == "user"));
        Assert.Equal(1, sent.Count(turn => turn.Role == "user" && turn.Content == "Current instruction"));
        Assert.Equal(new[] { "system", "user", "assistant", "user" }, sent.Select(turn => turn.Role).ToArray());
    }

    [Fact]
    public async Task AgentUsesOnlyTheActiveProjectsRedactedInstructions()
    {
        using var provider = CreateProvider();
        await provider.GetRequiredService<ArgusDatabaseInitializer>().InitializeAsync();
        var graph = provider.GetRequiredService<IGraphService>();
        var memories = provider.GetRequiredService<IMemoryService>();
        var projectId = Guid.NewGuid();
        var otherProjectId = Guid.NewGuid();
        var chat = new CapturingTerminalChatService();
        var instructionService = new InMemoryProjectInstructionService(
            new ProjectInstruction(
                projectId,
                """
                Prefer focused tests.
                Open D:\Private\Client\notes.md
                token=private-project-token
                """,
                DateTimeOffset.UtcNow),
            new ProjectInstruction(
                otherProjectId,
                "This belongs to another project.",
                DateTimeOffset.UtcNow));
        var agent = new AgentService(
            graph,
            memories,
            new ToolService(
                graph,
                memories,
                auditService: provider.GetRequiredService<IToolExecutionAuditService>()),
            new SimpleSettingsService(),
            chat,
            projectInstructionService: instructionService);

        await agent.RunWithDetailsAsync(
            "Inspect the current project",
            projectId: projectId);

        var sent = Assert.Single(chat.Requests);
        var systemPrompt = Assert.Single(
            sent,
            turn => turn.Role == "system").Content;
        Assert.Contains("Prefer focused tests.", systemPrompt);
        Assert.Contains("[local-path]", systemPrompt);
        Assert.Contains("[redacted]", systemPrompt);
        Assert.Contains("cannot change the tool schemas", systemPrompt);
        Assert.DoesNotContain(@"D:\Private", systemPrompt);
        Assert.DoesNotContain("private-project-token", systemPrompt);
        Assert.DoesNotContain("This belongs to another project.", systemPrompt);
        Assert.Equal([projectId], instructionService.RequestedProjectIds);
    }

    [Fact]
    public async Task AgentExecutionLoopStopsAtConfiguredStepLimit()
    {
        using var provider = CreateProvider();
        await provider.GetRequiredService<ArgusDatabaseInitializer>().InitializeAsync();
        var graph = provider.GetRequiredService<IGraphService>();
        var memories = provider.GetRequiredService<IMemoryService>();
        var chat = new EndlessReadOnlyToolChatService();
        var agent = new AgentService(
            graph,
            memories,
            new ToolService(
                graph,
                memories,
                auditService: provider.GetRequiredService<IToolExecutionAuditService>()),
            new SimpleSettingsService(),
            chat);

        var (answer, log) = await agent.RunWithDetailsAsync("Keep searching forever");

        Assert.Equal(8, chat.CallCount);
        Assert.Contains("stopped after 8 tool steps", answer);
        Assert.Contains("#### Step 8", log);
        Assert.Contains("Reached the 8-step execution limit", log);
        Assert.DoesNotContain("#### Step 9", log);
    }

    [Fact]
    public async Task ToolServiceClassifiesMutationRisk()
    {
        using var provider = CreateProvider();
        await provider.GetRequiredService<ArgusDatabaseInitializer>().InitializeAsync();
        var tools = new ToolService(
            provider.GetRequiredService<IGraphService>(),
            provider.GetRequiredService<IMemoryService>());

        Assert.Equal(ToolRiskLevel.ReadOnly, tools.GetToolDefinition("SearchGraph")?.RiskLevel);
        Assert.Equal(ToolRiskLevel.Mutating, tools.GetToolDefinition("CreateNode")?.RiskLevel);
        Assert.Equal(ToolRiskLevel.Destructive, tools.GetToolDefinition("DeleteNode")?.RiskLevel);
        Assert.Contains(
            "\"additionalProperties\":false",
            tools.GetToolDefinition("CreateNode")?.ArgumentSchemaJson);
        Assert.Null(tools.GetToolDefinition("NotARealTool"));
    }

    [Fact]
    public async Task ToolArgumentsRejectUnknownMissingAndOutOfRangeValues()
    {
        using var provider = CreateProvider();
        await provider.GetRequiredService<ArgusDatabaseInitializer>().InitializeAsync();
        var tools = new ToolService(
            provider.GetRequiredService<IGraphService>(),
            provider.GetRequiredService<IMemoryService>());

        var validation = tools.ValidateArguments(
            "CreateNode",
            """{"title":"","importance":9,"unexpected":true}""");

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Errors, error => error.Contains("'title' is required", StringComparison.Ordinal));
        Assert.Contains(validation.Errors, error => error.Contains("Unknown property 'unexpected'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ToolServiceFailsClosedForMutationWithoutApprovedStatus()
    {
        using var provider = CreateProvider();
        await provider.GetRequiredService<ArgusDatabaseInitializer>().InitializeAsync();
        var graph = provider.GetRequiredService<IGraphService>();
        var tools = new ToolService(
            graph,
            provider.GetRequiredService<IMemoryService>(),
            auditService: provider.GetRequiredService<IToolExecutionAuditService>());

        var result = await tools.ExecuteToolAsync(
            "CreateNode",
            """{"title":"Must Not Exist","type":"Note"}""");

        Assert.Contains("requires explicit approval", result);
        Assert.DoesNotContain(
            await graph.SearchNodesAsync("Must Not Exist"),
            node => node.Title == "Must Not Exist");

        var audit = Assert.Single(
            await provider.GetRequiredService<IToolExecutionAuditService>().GetRecentAsync());
        Assert.Equal("approval_denied", audit.Outcome);
        Assert.Equal("unspecified", audit.ApprovalStatus);
    }

    [Fact]
    public async Task ToolServiceFailsClosedForApprovedMutationWithoutAuditStore()
    {
        using var provider = CreateProvider();
        await provider.GetRequiredService<ArgusDatabaseInitializer>().InitializeAsync();
        var graph = provider.GetRequiredService<IGraphService>();
        var tools = new ToolService(
            graph,
            provider.GetRequiredService<IMemoryService>());

        var result = await tools.ExecuteToolAsync(
            new ToolExecutionRequest(
                "CreateNode",
                """{"title":"Unaudited Node","type":"Note"}""",
                ApprovalStatus: "approved"));

        Assert.False(result.Succeeded);
        Assert.Contains("audit store is unavailable", result.ResultJson);
        Assert.DoesNotContain(
            await graph.SearchNodesAsync("Unaudited Node"),
            node => node.Title == "Unaudited Node");
    }

    [Fact]
    public async Task ApprovedToolExecutionPersistsRedactedAttributableAudit()
    {
        using var provider = CreateProvider();
        await provider.GetRequiredService<ArgusDatabaseInitializer>().InitializeAsync();
        var auditService = provider.GetRequiredService<IToolExecutionAuditService>();
        var tools = new ToolService(
            provider.GetRequiredService<IGraphService>(),
            provider.GetRequiredService<IMemoryService>(),
            auditService: auditService);
        var agentRunId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();

        var result = await tools.ExecuteToolAsync(
            new ToolExecutionRequest(
                "CreateNode",
                """{"title":"Private roadmap title","type":"Decision","summary":"Secret launch details","importance":5}""",
                agentRunId,
                conversationId,
                ApprovalStatus: "approved"));

        Assert.True(result.Succeeded);
        var audit = Assert.Single(await auditService.GetRecentAsync());
        Assert.Equal(result.ExecutionId, audit.ExecutionId);
        Assert.Equal(agentRunId, audit.AgentRunId);
        Assert.Equal(conversationId, audit.ConversationId);
        Assert.Equal("approved", audit.ApprovalStatus);
        Assert.Equal("succeeded", audit.Outcome);
        Assert.DoesNotContain("Private roadmap title", audit.ArgumentsSummary);
        Assert.DoesNotContain("Secret launch details", audit.ArgumentsSummary);
        Assert.Contains("[redacted:", audit.ArgumentsSummary);
    }

    [Fact]
    public async Task InvalidToolAttemptIsAuditedWithoutExecution()
    {
        using var provider = CreateProvider();
        await provider.GetRequiredService<ArgusDatabaseInitializer>().InitializeAsync();
        var auditService = provider.GetRequiredService<IToolExecutionAuditService>();
        var tools = new ToolService(
            provider.GetRequiredService<IGraphService>(),
            provider.GetRequiredService<IMemoryService>(),
            auditService: auditService);

        var result = await tools.ExecuteToolAsync(
            new ToolExecutionRequest(
                "SaveMemory",
                """{"text":"","importance":0,"extra":"nope"}""",
                ApprovalStatus: "validation_rejected"));

        Assert.True(result.ValidationFailed);
        Assert.False(result.Succeeded);
        var audit = Assert.Single(await auditService.GetRecentAsync());
        Assert.Equal("validation_failed", audit.Outcome);
        Assert.Equal(result.ExecutionId, audit.ExecutionId);
    }

    [Fact]
    public async Task NewsServiceFiltersGamingAndFrontierAiFeeds()
    {
        var previousNewsApiKey = Environment.GetEnvironmentVariable("NEWSAPI_KEY");
        Environment.SetEnvironmentVariable("NEWSAPI_KEY", null);

        try
        {
            var service = new NewsService(new HttpClient(new FeedHandler()));

            var all = await service.GetLatestAsync(50);
            var gaming = await service.GetLatestAsync(50, "gaming");
            var ai = await service.GetLatestAsync(50, "ai");

            Assert.Contains(all, article => article.Category == "gaming");
            Assert.Contains(all, article => article.Category == "ai");
            Assert.All(gaming, article => Assert.Equal("gaming", article.Category));
            Assert.Contains(ai, article => article.Title.Contains("Qwen", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(gaming, article => article.Category == "ai");
        }
        finally
        {
            Environment.SetEnvironmentVariable("NEWSAPI_KEY", previousNewsApiKey);
        }
    }

    private sealed class MockEmbeddingChatService : IAiChatService
    {
        public Task<AiChatResult> SendAsync(AiProviderProfile? profile, IReadOnlyList<AiChatTurn> messages, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new AiChatResult("mock response"));
        }

        public Task<float[]?> GenerateEmbeddingAsync(AiProviderProfile? profile, string text, CancellationToken cancellationToken = default)
        {
            if (text == "query" || text == "memory1")
            {
                return Task.FromResult<float[]?>(new[] { 1.0f, 0.0f });
            }
            if (text == "memory2")
            {
                return Task.FromResult<float[]?>(new[] { 0.0f, 1.0f });
            }
            return Task.FromResult<float[]?>(null);
        }
    }

    private sealed class FailingEmbeddingChatService : IAiChatService
    {
        public Task<AiChatResult> SendAsync(
            AiProviderProfile? profile,
            IReadOnlyList<AiChatTurn> messages,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new AiChatResult("unused"));
        }

        public Task<float[]?> GenerateEmbeddingAsync(
            AiProviderProfile? profile,
            string text,
            CancellationToken cancellationToken = default)
        {
            throw new HttpRequestException("Embedding endpoint unavailable.");
        }
    }

    private sealed class SimpleSettingsService : ISettingsService
    {
        private readonly Dictionary<string, string> _settings = new();

        public Task<IReadOnlyList<AiProviderProfile>> GetAiProviderProfilesAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<AiProviderProfile>>(Array.Empty<AiProviderProfile>());
        }

        public Task<AiProviderProfile?> GetDefaultAiProviderProfileAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<AiProviderProfile?>(new AiProviderProfile { Name = "MockProfile" });
        }

        public Task<AiProviderProfile> SaveAiProviderProfileAsync(AiProviderProfile profile, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(profile);
        }

        public Task<string?> GetSettingAsync(string key, string? defaultValue = null, CancellationToken cancellationToken = default)
        {
            if (_settings.TryGetValue(key, out var val))
            {
                return Task.FromResult<string?>(val);
            }
            return Task.FromResult(defaultValue);
        }

        public Task SaveSettingAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            _settings[key] = value;
            return Task.CompletedTask;
        }
    }

    private sealed class MockAgentLoopChatService : IAiChatService
    {
        private int step = 0;

        public Task<AiChatResult> SendAsync(AiProviderProfile? profile, IReadOnlyList<AiChatTurn> messages, CancellationToken cancellationToken = default)
        {
            step++;
            if (step == 1)
            {
                return Task.FromResult(new AiChatResult(
                    """
                    {
                      "thought": "I will create a note node.",
                      "tool": "CreateNode",
                      "arguments": {
                        "title": "Agent Created Node",
                        "type": "Note",
                        "summary": "via agent tool",
                        "body": "Test body",
                        "status": "Active"
                      },
                      "answer": null
                    }
                    """));
            }
            else
            {
                return Task.FromResult(new AiChatResult(
                    """
                    {
                      "thought": "I have created the node and completed the task.",
                      "tool": null,
                      "arguments": null,
                      "answer": "I created the note and saved it in Argus."
                    }
                    """));
            }
        }

        public Task<float[]?> GenerateEmbeddingAsync(AiProviderProfile? profile, string text, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<float[]?>(null);
        }
    }

    private sealed class CapturingTerminalChatService : IAiChatService
    {
        public List<IReadOnlyList<AiChatTurn>> Requests { get; } = new();

        public Task<AiChatResult> SendAsync(
            AiProviderProfile? profile,
            IReadOnlyList<AiChatTurn> messages,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(messages.ToList());
            return Task.FromResult(new AiChatResult(
                """
                {
                  "thought": "The task is complete.",
                  "tool": null,
                  "arguments": null,
                  "answer": "Done."
                }
                """));
        }

        public Task<float[]?> GenerateEmbeddingAsync(
            AiProviderProfile? profile,
            string text,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<float[]?>(null);
        }
    }

    private sealed class InMemoryProjectInstructionService(
        params ProjectInstruction[] instructions) : IProjectInstructionService
    {
        private readonly IReadOnlyDictionary<Guid, ProjectInstruction> byProject =
            instructions.ToDictionary(instruction => instruction.ProjectId);

        public List<Guid> RequestedProjectIds { get; } = [];

        public Task<ProjectInstruction?> GetAsync(
            Guid projectId,
            CancellationToken cancellationToken = default)
        {
            RequestedProjectIds.Add(projectId);
            return Task.FromResult(
                byProject.TryGetValue(projectId, out var instruction)
                    ? instruction
                    : null);
        }

        public Task<IReadOnlyDictionary<Guid, ProjectInstruction>> GetManyAsync(
            IEnumerable<Guid> projectIds,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ProjectInstruction?> SaveAsync(
            Guid projectId,
            string? content,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task ClearAsync(
            Guid projectId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class EndlessReadOnlyToolChatService : IAiChatService
    {
        public int CallCount { get; private set; }

        public Task<AiChatResult> SendAsync(
            AiProviderProfile? profile,
            IReadOnlyList<AiChatTurn> messages,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(new AiChatResult(
                """
                {
                  "thought": "Search again.",
                  "tool": "SearchGraph",
                  "arguments": { "query": "Argus" },
                  "answer": null
                }
                """));
        }

        public Task<float[]?> GenerateEmbeddingAsync(
            AiProviderProfile? profile,
            string text,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<float[]?>(null);
        }
    }

    private sealed class AllowToolApprovalService : IToolApprovalService
    {
        public Task<ToolApprovalDecision> RequestApprovalAsync(
            ToolApprovalRequest request,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new ToolApprovalDecision(true));
        }
    }

    private sealed class DenyToolApprovalService : IToolApprovalService
    {
        public Task<ToolApprovalDecision> RequestApprovalAsync(
            ToolApprovalRequest request,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new ToolApprovalDecision(false, "the test user denied the action"));
        }
    }

    private sealed class FeedHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri?.ToString() ?? string.Empty;
            var content = url.Contains("gaming", StringComparison.OrdinalIgnoreCase) ||
                url.Contains("polygon", StringComparison.OrdinalIgnoreCase) ||
                url.Contains("pcgamer", StringComparison.OrdinalIgnoreCase) ||
                url.Contains("gamedeveloper", StringComparison.OrdinalIgnoreCase)
                    ? Rss("Studio updates a major game engine", "https://example.com/gaming", "Games and engines.")
                    : url.Contains("qwen", StringComparison.OrdinalIgnoreCase) ||
                      url.Contains("openai", StringComparison.OrdinalIgnoreCase) ||
                      url.Contains("venturebeat", StringComparison.OrdinalIgnoreCase) ||
                      url.Contains("artificial-intelligence", StringComparison.OrdinalIgnoreCase)
                        ? Rss("Qwen releases a stronger Chinese model", "https://example.com/qwen", "Frontier AI model update.")
                        : Rss("New developer hardware lands", "https://example.com/tech", "Tech infrastructure update.");

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content, Encoding.UTF8, "application/rss+xml")
            });
        }

        private static string Rss(string title, string link, string description)
        {
            return $$"""
                <rss version="2.0">
                  <channel>
                    <title>Test feed</title>
                    <item>
                      <title>{{WebUtility.HtmlEncode(title)}}</title>
                      <link>{{link}}</link>
                      <description>{{WebUtility.HtmlEncode(description)}}</description>
                      <pubDate>Fri, 05 Jun 2026 20:00:00 GMT</pubDate>
                    </item>
                  </channel>
                </rss>
                """;
        }
    }

    [Fact]
    public async Task TelegramGatewayProcessesAllowedUserMessagesAndBlocksOthers()
    {
        var services = new ServiceCollection();
        var dbPath = Path.Combine(Path.GetTempPath(), "ArgusTests", $"{Guid.NewGuid():N}.db");
        services.AddArgusData(dbPath);

        var mockChat = new MockAgentLoopChatService();
        var mockSettings = new SimpleSettingsService();

        await mockSettings.SaveSettingAsync("TelegramBotEnabled", "true");
        await mockSettings.SaveSettingAsync("TelegramBotToken", "mock-token");
        await mockSettings.SaveSettingAsync("TelegramAllowedUserIds", "allowed-user, 999");

        var provider = services.BuildServiceProvider();
        await provider.GetRequiredService<ArgusDatabaseInitializer>().InitializeAsync();

        var graph = provider.GetRequiredService<IGraphService>();
        var memories = provider.GetRequiredService<IMemoryService>();
        var toolService = new ToolService(
            graph,
            memories,
            auditService: provider.GetRequiredService<IToolExecutionAuditService>());
        var agent = new AgentService(
            graph,
            memories,
            toolService,
            mockSettings,
            mockChat,
            toolApprovalService: new AllowToolApprovalService());

        var handler = new MockTelegramHttpMessageHandler();
        var httpClient = new HttpClient(handler);
        var conversationService = provider.GetRequiredService<IConversationService>();
        var gateway = new TelegramGatewayService(mockSettings, new StaticSecretStore("mock-token"), agent, conversationService, httpClient);

        await gateway.StartAsync();
        await Task.Delay(250);
        await gateway.StopAsync();

        Assert.True(handler.GetUpdatesCalled);
        Assert.True(handler.SendMessageCalled);
        Assert.Contains("I created the note and saved it in Argus.", handler.LastMessageSent);
    }

    [Fact]
    public async Task TelegramGatewayBlocksUnauthorizedUsers()
    {
        var services = new ServiceCollection();
        var dbPath = Path.Combine(Path.GetTempPath(), "ArgusTests", $"{Guid.NewGuid():N}.db");
        services.AddArgusData(dbPath);

        var mockChat = new MockAgentLoopChatService();
        var mockSettings = new SimpleSettingsService();

        await mockSettings.SaveSettingAsync("TelegramBotEnabled", "true");
        await mockSettings.SaveSettingAsync("TelegramBotToken", "mock-token");
        await mockSettings.SaveSettingAsync("TelegramAllowedUserIds", "allowed-user");

        var provider = services.BuildServiceProvider();
        await provider.GetRequiredService<ArgusDatabaseInitializer>().InitializeAsync();

        var graph = provider.GetRequiredService<IGraphService>();
        var memories = provider.GetRequiredService<IMemoryService>();
        var toolService = new ToolService(
            graph,
            memories,
            auditService: provider.GetRequiredService<IToolExecutionAuditService>());
        var agent = new AgentService(
            graph,
            memories,
            toolService,
            mockSettings,
            mockChat,
            toolApprovalService: new AllowToolApprovalService());

        var handler = new MockTelegramBlockedUserHttpMessageHandler();
        var httpClient = new HttpClient(handler);
        var conversationService = provider.GetRequiredService<IConversationService>();
        var gateway = new TelegramGatewayService(mockSettings, new StaticSecretStore("mock-token"), agent, conversationService, httpClient);

        await gateway.StartAsync();
        await Task.Delay(250);
        await gateway.StopAsync();

        Assert.True(handler.GetUpdatesCalled);
        Assert.True(handler.SendMessageCalled);
        Assert.Contains("Unauthorized", handler.LastMessageSent);
    }

    [Fact]
    public async Task TelegramGatewayBlocksWhenAllowlistIsEmpty()
    {
        var services = new ServiceCollection();
        var dbPath = Path.Combine(Path.GetTempPath(), "ArgusTests", $"{Guid.NewGuid():N}.db");
        services.AddArgusData(dbPath);

        var mockChat = new MockAgentLoopChatService();
        var mockSettings = new SimpleSettingsService();

        await mockSettings.SaveSettingAsync("TelegramBotEnabled", "true");
        await mockSettings.SaveSettingAsync("TelegramBotToken", "mock-token");

        var provider = services.BuildServiceProvider();
        await provider.GetRequiredService<ArgusDatabaseInitializer>().InitializeAsync();

        var graph = provider.GetRequiredService<IGraphService>();
        var memories = provider.GetRequiredService<IMemoryService>();
        var toolService = new ToolService(
            graph,
            memories,
            auditService: provider.GetRequiredService<IToolExecutionAuditService>());
        var agent = new AgentService(
            graph,
            memories,
            toolService,
            mockSettings,
            mockChat,
            toolApprovalService: new AllowToolApprovalService());

        var handler = new MockTelegramHttpMessageHandler();
        var httpClient = new HttpClient(handler);
        var conversationService = provider.GetRequiredService<IConversationService>();
        var gateway = new TelegramGatewayService(mockSettings, new StaticSecretStore("mock-token"), agent, conversationService, httpClient);

        await gateway.StartAsync();
        await Task.Delay(250);
        await gateway.StopAsync();

        Assert.True(handler.GetUpdatesCalled);
        Assert.True(handler.SendMessageCalled);
        Assert.Contains("Unauthorized", handler.LastMessageSent);
    }

    [Fact]
    public async Task TelegramGatewayFormatsMessageAsHtml()
    {
        var services = new ServiceCollection();
        var dbPath = Path.Combine(Path.GetTempPath(), "ArgusTests", $"{Guid.NewGuid():N}.db");
        services.AddArgusData(dbPath);

        var mockChat = new MockMarkdownAgentLoopChatService();
        var mockSettings = new SimpleSettingsService();

        await mockSettings.SaveSettingAsync("TelegramBotEnabled", "true");
        await mockSettings.SaveSettingAsync("TelegramBotToken", "mock-token");
        await mockSettings.SaveSettingAsync("TelegramAllowedUserIds", "allowed-user");

        var provider = services.BuildServiceProvider();
        await provider.GetRequiredService<ArgusDatabaseInitializer>().InitializeAsync();

        var graph = provider.GetRequiredService<IGraphService>();
        var memories = provider.GetRequiredService<IMemoryService>();
        var toolService = new ToolService(
            graph,
            memories,
            auditService: provider.GetRequiredService<IToolExecutionAuditService>());
        var agent = new AgentService(
            graph,
            memories,
            toolService,
            mockSettings,
            mockChat,
            toolApprovalService: new AllowToolApprovalService());

        var handler = new MockTelegramHttpMessageHandler();
        var httpClient = new HttpClient(handler);
        var conversationService = provider.GetRequiredService<IConversationService>();
        var gateway = new TelegramGatewayService(mockSettings, new StaticSecretStore("mock-token"), agent, conversationService, httpClient);

        await gateway.StartAsync();
        await Task.Delay(250);
        await gateway.StopAsync();

        Assert.True(handler.GetUpdatesCalled);
        Assert.True(handler.SendMessageCalled);

        Assert.Contains("parse_mode", handler.LastMessageSent);
        Assert.Contains("HTML", handler.LastMessageSent);

        using var doc = JsonDocument.Parse(handler.LastMessageSent);
        var root = doc.RootElement;
        var text = root.GetProperty("text").GetString() ?? string.Empty;

        Assert.Contains("<b>bold</b>", text);
        Assert.Contains("<i>italic</i>", text);
        Assert.Contains("<code>code</code>", text);
        Assert.Contains("<a href=\"http://example.com\">link</a>", text);
        Assert.Contains("<pre>pre code\n</pre>", text);
    }

    private sealed class MockTelegramHttpMessageHandler : HttpMessageHandler
    {
        public bool GetUpdatesCalled { get; private set; }
        public bool SendMessageCalled { get; private set; }
        public string LastMessageSent { get; private set; } = string.Empty;
        private int _updatesReturned = 0;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri?.ToString() ?? string.Empty;
            if (url.Contains("getUpdates"))
            {
                GetUpdatesCalled = true;
                if (_updatesReturned == 0)
                {
                    _updatesReturned++;
                    var json =
                        """
                        {
                          "ok": true,
                          "result": [
                            {
                              "update_id": 10001,
                              "message": {
                                "message_id": 1,
                                "from": {
                                  "id": 999,
                                  "is_bot": false,
                                  "first_name": "Test",
                                  "username": "allowed-user"
                                },
                                "chat": {
                                  "id": 12345,
                                  "first_name": "Test",
                                  "type": "private"
                                },
                                "date": 1622830000,
                                "text": "Create a note for testing the agent"
                              }
                            }
                          ]
                        }
                        """;
                    return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                    {
                        Content = new StringContent(json, Encoding.UTF8, "application/json")
                    };
                }
                else
                {
                    return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                    {
                        Content = new StringContent("""{"ok":true,"result":[]}""", Encoding.UTF8, "application/json")
                    };
                }
            }
            else if (url.Contains("sendMessage"))
            {
                SendMessageCalled = true;
                LastMessageSent = await request.Content!.ReadAsStringAsync(cancellationToken);
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"ok":true,"result":{}}""", Encoding.UTF8, "application/json")
                };
            }

            return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
        }
    }

    private sealed class MockTelegramBlockedUserHttpMessageHandler : HttpMessageHandler
    {
        public bool GetUpdatesCalled { get; private set; }
        public bool SendMessageCalled { get; private set; }
        public string LastMessageSent { get; private set; } = string.Empty;
        private int _updatesReturned = 0;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri?.ToString() ?? string.Empty;
            if (url.Contains("getUpdates"))
            {
                GetUpdatesCalled = true;
                if (_updatesReturned == 0)
                {
                    _updatesReturned++;
                    var json =
                        """
                        {
                          "ok": true,
                          "result": [
                            {
                              "update_id": 10001,
                              "message": {
                                "message_id": 1,
                                "from": {
                                  "id": 888,
                                  "is_bot": false,
                                  "first_name": "Hacker",
                                  "username": "unauthorized-user"
                                },
                                "chat": {
                                  "id": 12345,
                                  "first_name": "Hacker",
                                  "type": "private"
                                },
                                "date": 1622830000,
                                "text": "Hack into Argus"
                              }
                            }
                          ]
                        }
                        """;
                    return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                    {
                        Content = new StringContent(json, Encoding.UTF8, "application/json")
                    };
                }
                else
                {
                    return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                    {
                        Content = new StringContent("""{"ok":true,"result":[]}""", Encoding.UTF8, "application/json")
                    };
                }
            }
            else if (url.Contains("sendMessage"))
            {
                SendMessageCalled = true;
                LastMessageSent = await request.Content!.ReadAsStringAsync(cancellationToken);
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"ok":true,"result":{}}""", Encoding.UTF8, "application/json")
                };
            }

            return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
        }
    }

    private sealed class MockMarkdownAgentLoopChatService : IAiChatService
    {
        private int step = 0;

        public Task<AiChatResult> SendAsync(AiProviderProfile? profile, IReadOnlyList<AiChatTurn> messages, CancellationToken cancellationToken = default)
        {
            step++;
            if (step == 1)
            {
                return Task.FromResult(new AiChatResult(
                    """
                    {
                      "thought": "I will create a note node.",
                      "tool": "CreateNode",
                      "arguments": {
                        "title": "Agent Created Node",
                        "type": "Note",
                        "summary": "via agent tool",
                        "body": "Test body",
                        "status": "Active"
                      },
                      "answer": null
                    }
                    """));
            }
            else
            {
                return Task.FromResult(new AiChatResult(
                    """
                    {
                      "thought": "I have a final formatted response.",
                      "tool": null,
                      "arguments": null,
                      "answer": "This is **bold** and *italic* and `code` and [link](http://example.com) and ```\npre code\n```."
                    }
                    """));
            }
        }

        public Task<float[]?> GenerateEmbeddingAsync(AiProviderProfile? profile, string text, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<float[]?>(null);
        }
    }

    [Fact]
    public async Task StockServiceDoesNotFabricateQuoteWhenProviderFails()
    {
        using var client = new HttpClient(new FailedStockQuoteHttpMessageHandler());
        var stocks = new StockService(client);

        var quote = await stocks.GetQuoteAsync("MSFT");

        Assert.Null(quote);
    }

    private sealed class FailedStockQuoteHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }
    }

    [Fact]
    public async Task WebSearchToolQueriesSearxng()
    {
        using var provider = CreateProvider();
        await provider.GetRequiredService<ArgusDatabaseInitializer>().InitializeAsync();
        var graph = provider.GetRequiredService<IGraphService>();
        var memories = provider.GetRequiredService<IMemoryService>();

        var handler = new MockSearxngHttpMessageHandler();
        var client = new HttpClient(handler);
        var toolService = new ToolService(graph, memories, client);

        var args = "{\"query\":\"Argus local-first\"}";
        var resultJson = await toolService.ExecuteToolAsync("WebSearch", args);

        Assert.Contains("success", resultJson);
        Assert.Contains("Argus title", resultJson);
        Assert.Contains("Argus snippet", resultJson);
    }

    [Fact]
    public async Task MemorySearchToolReturnsRecallEvidence()
    {
        using var provider = CreateProvider();
        await provider.GetRequiredService<ArgusDatabaseInitializer>().InitializeAsync();
        var graph = provider.GetRequiredService<IGraphService>();
        var memories = provider.GetRequiredService<IMemoryService>();
        await memories.SaveMemoryAsync(
            "Argus keeps durable memory in local SQLite storage.",
            "decision:memory",
            5);
        var toolService = new ToolService(graph, memories);

        var resultJson = await toolService.ExecuteToolAsync(
            "SearchMemories",
            """{"query":"local SQLite storage","take":5}""");

        Assert.Contains("\"Score\"", resultJson);
        Assert.Contains("\"Method\"", resultJson);
        Assert.Contains("\"Explanation\"", resultJson);
        Assert.Contains("decision:memory", resultJson);
    }

    private sealed class MockSearxngHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var json =
                """
                {
                  "results": [
                    {
                      "title": "Argus title",
                      "url": "http://example.com",
                      "content": "Argus snippet"
                    }
                  ]
                }
                """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class FakeOpenAiCodexService : IOpenAiCodexService
    {
        public int SendCount { get; private set; }

        public Task<OpenAiCodexAccount> GetAccountAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new OpenAiCodexAccount(true, true, "Signed in."));

        public Task<OpenAiCodexLoginStart> StartLoginAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new OpenAiCodexLoginStart(true, "Started.", "login", "https://example.com"));

        public Task<OpenAiCodexAccount> CompleteLoginAsync(
            string loginId,
            TimeSpan timeout,
            CancellationToken cancellationToken = default) =>
            GetAccountAsync(cancellationToken);

        public Task LogoutAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<AiModelMetadata>> ListModelsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AiModelMetadata>>(
                [new AiModelMetadata("gpt-5.5", "GPT-5.5")]);

        public Task<AiChatResult> SendAsync(
            AiProviderProfile profile,
            IReadOnlyList<AiChatTurn> messages,
            CancellationToken cancellationToken = default)
        {
            SendCount++;
            return Task.FromResult(new AiChatResult("codex response", Model: profile.Model));
        }
    }

    [Fact]
    public async Task ToolServiceRaisesExecutingAndExecutedEvents()
    {
        using var provider = CreateProvider();
        await provider.GetRequiredService<ArgusDatabaseInitializer>().InitializeAsync();
        var graph = provider.GetRequiredService<IGraphService>();
        var memories = provider.GetRequiredService<IMemoryService>();

        var handler = new MockSearxngHttpMessageHandler();
        var client = new HttpClient(handler);
        var toolService = new ToolService(graph, memories, client);

        var executingRaised = false;
        var executedRaised = false;
        string? executingToolName = null;
        string? executedToolName = null;
        string? executedResult = null;

        Action<string> onExecuting = tool =>
        {
            executingRaised = true;
            executingToolName = tool;
        };
        Action<string, string> onExecuted = (tool, result) =>
        {
            executedRaised = true;
            executedToolName = tool;
            executedResult = result;
        };

        ToolService.OnToolExecuting += onExecuting;
        ToolService.OnToolExecuted += onExecuted;

        try
        {
            var args = "{\"query\":\"Argus local-first\"}";
            var resultJson = await toolService.ExecuteToolAsync("WebSearch", args);

            Assert.True(executingRaised);
            Assert.Equal("WebSearch", executingToolName);
            Assert.True(executedRaised);
            Assert.Equal("WebSearch", executedToolName);
            Assert.Equal(resultJson, executedResult);
        }
        finally
        {
            ToolService.OnToolExecuting -= onExecuting;
            ToolService.OnToolExecuted -= onExecuted;
        }
    }

    [Fact]
    public async Task ToolExecutionAuditsFilteringAndPruningWorksCorrectly()
    {
        using var provider = CreateProvider();
        await provider.GetRequiredService<ArgusDatabaseInitializer>().InitializeAsync();
        var auditService = provider.GetRequiredService<IToolExecutionAuditService>();

        var conversationId = Guid.NewGuid();
        var audit1 = new ToolExecutionAudit
        {
            ExecutionId = Guid.NewGuid(),
            ToolName = "CreateNode",
            RiskLevel = "Mutating",
            ApprovalStatus = "approved",
            Outcome = "succeeded",
            StartedAt = DateTimeOffset.UtcNow.AddDays(-5)
        };
        var audit2 = new ToolExecutionAudit
        {
            ExecutionId = Guid.NewGuid(),
            ToolName = "SaveMemory",
            RiskLevel = "Mutating",
            ApprovalStatus = "auto_approved",
            Outcome = "failed",
            StartedAt = DateTimeOffset.UtcNow.AddDays(-2)
        };
        var audit3 = new ToolExecutionAudit
        {
            ExecutionId = Guid.NewGuid(),
            ToolName = "WebSearch",
            RiskLevel = "ReadOnly",
            ApprovalStatus = "unknown",
            Outcome = "started",
            StartedAt = DateTimeOffset.UtcNow
        };

        await auditService.RecordAsync(audit1);
        await auditService.RecordAsync(audit2);
        await auditService.RecordAsync(audit3);

        // Filter by tool name
        var filteredByName = await auditService.GetFilteredAsync(toolName: "Save");
        Assert.Single(filteredByName);
        Assert.Equal(audit2.ExecutionId, filteredByName[0].ExecutionId);

        // Filter by risk
        var filteredByRisk = await auditService.GetFilteredAsync(riskLevel: "ReadOnly");
        Assert.Single(filteredByRisk);
        Assert.Equal(audit3.ExecutionId, filteredByRisk[0].ExecutionId);

        // Filter by outcome
        var filteredByOutcome = await auditService.GetFilteredAsync(outcome: "failed");
        Assert.Single(filteredByOutcome);

        // Filter by incomplete
        var filteredByIncomplete = await auditService.GetFilteredAsync(onlyIncomplete: true);
        Assert.Single(filteredByIncomplete);
        Assert.Equal(audit3.ExecutionId, filteredByIncomplete[0].ExecutionId);

        // Fetch by ExecutionId
        var fetched = await auditService.GetByExecutionIdAsync(audit1.ExecutionId);
        Assert.NotNull(fetched);
        Assert.Equal("CreateNode", fetched.ToolName);

        // Prune older than 3 days (should prune audit1)
        var pruned = await auditService.PruneOldAuditsAsync(DateTimeOffset.UtcNow.AddDays(-3));
        Assert.Equal(1, pruned);

        var recent = await auditService.GetRecentAsync();
        Assert.Equal(2, recent.Count);
        Assert.DoesNotContain(recent, a => a.ExecutionId == audit1.ExecutionId);

        // Clear all
        var cleared = await auditService.ClearAllAuditsAsync();
        Assert.Equal(2, cleared);
        Assert.Empty(await auditService.GetRecentAsync());
    }

    [Fact]
    public async Task ToolExecutionIdempotencyPolicyPreventsDuplicateSuccessAndAllowsRetryOnFailure()
    {
        using var provider = CreateProvider();
        await provider.GetRequiredService<ArgusDatabaseInitializer>().InitializeAsync();
        var auditService = provider.GetRequiredService<IToolExecutionAuditService>();
        var graph = provider.GetRequiredService<IGraphService>();
        var memories = provider.GetRequiredService<IMemoryService>();

        var tools = new ToolService(graph, memories, auditService: auditService);

        var executionId = Guid.NewGuid();
        var request = new ToolExecutionRequest(
            "CreateNode",
            "{\"title\":\"Unique Node X\",\"type\":\"Idea\",\"summary\":\"X details\"}",
            Guid.NewGuid(),
            Guid.NewGuid(),
            ApprovalStatus: "approved",
            ExecutionId: executionId);

        // First execution: should succeed and create node
        var result1 = await tools.ExecuteToolAsync(request);
        Assert.True(result1.Succeeded);
        Assert.DoesNotContain("idempotent_success", result1.ResultJson);

        // Verify node was created
        var nodesBefore = await graph.SearchNodesAsync("Unique Node X");
        Assert.Single(nodesBefore);

        // Second execution (same ExecutionId): should skip and return cached idempotent response
        var result2 = await tools.ExecuteToolAsync(request);
        Assert.True(result2.Succeeded);
        Assert.Contains("idempotent_success", result2.ResultJson);

        // Verify no duplicate node was created (still only one node exists)
        var nodesAfter = await graph.SearchNodesAsync("Unique Node X");
        Assert.Single(nodesAfter);

        // Third execution (different ExecutionId): should allow execution again and create second node
        var request2 = request with { ExecutionId = Guid.NewGuid() };
        var result3 = await tools.ExecuteToolAsync(request2);
        Assert.True(result3.Succeeded);
        Assert.DoesNotContain("idempotent_success", result3.ResultJson);

        // Setup a failed audit for a retry test
        var failedExecutionId = Guid.NewGuid();
        var failedAudit = new ToolExecutionAudit
        {
            ExecutionId = failedExecutionId,
            ToolName = "CreateNode",
            RiskLevel = "Mutating",
            ApprovalStatus = "approved",
            Outcome = "failed",
            StartedAt = DateTimeOffset.UtcNow
        };
        await auditService.RecordAsync(failedAudit);

        // Try execution with the failed ExecutionId: should execute (retry allowed)
        var request3 = request with { ExecutionId = failedExecutionId, ArgumentsJson = "{\"title\":\"Retry Node\",\"type\":\"Idea\",\"summary\":\"y\"}" };
        var result4 = await tools.ExecuteToolAsync(request3);
        Assert.True(result4.Succeeded);
        Assert.DoesNotContain("idempotent_success", result4.ResultJson);

        var nodesRetry = await graph.SearchNodesAsync("Retry Node");
        Assert.Single(nodesRetry);
    }
}
