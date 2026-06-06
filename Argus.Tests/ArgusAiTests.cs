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
        var results = await memoryService.RecallAsync("query");

        Assert.NotEmpty(results);
        Assert.Equal("memory1", results[0].Text);
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
        var toolService = new ToolService(graph, memories);

        var agent = new AgentService(graph, memories, toolService, mockSettings, mockChat);

        var log = await agent.RunAsync("Create a note for testing the agent");

        // Assertions
        Assert.Contains("- **Executing Action:** `CreateNode`", log);
        Assert.Contains("- **Decision:** Task completed.", log);

        var nodes = await graph.SearchNodesAsync("Agent Created Node");
        Assert.Contains(nodes, n => n.Title == "Agent Created Node");
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
        var toolService = new ToolService(graph, memories);
        var agent = new AgentService(graph, memories, toolService, mockSettings, mockChat);

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
        var toolService = new ToolService(graph, memories);
        var agent = new AgentService(graph, memories, toolService, mockSettings, mockChat);

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
        var toolService = new ToolService(graph, memories);
        var agent = new AgentService(graph, memories, toolService, mockSettings, mockChat);

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
        var toolService = new ToolService(graph, memories);
        var agent = new AgentService(graph, memories, toolService, mockSettings, mockChat);

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
}
