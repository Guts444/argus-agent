using Argus.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace Argus.Data.Services;

public sealed class ArgusDatabaseInitializer(IDbContextFactory<ArgusDbContext> dbContextFactory)
{
    private const string DeepSeekDefaultsMigrationKey = "Migration:DeepSeekDefaults:v2";
    private const string PublicSeedContentMigrationKey = "Migration:PublicSeedContent:v1";
    private const string OpenAiCodexProviderMigrationKey = "Migration:OpenAiCodexProvider:v1";
    private const string ChatArtifactCleanupMigrationKey = "Migration:ChatArtifactCleanup:v1";
    private const string ProviderDefaultRepairMigrationKey = "Migration:ProviderDefaultRepair:v1";

    public async Task InitializeAsync(
        CancellationToken cancellationToken = default,
        IProgress<DatabaseInitializationProgress>? progress = null)
    {
        progress?.Report(new DatabaseInitializationProgress(
            "Checking local database",
            "Inspecting the SQLite schema and migration history."));
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        bool canConnect = await db.Database.CanConnectAsync(cancellationToken);
        if (!canConnect)
        {
            progress?.Report(new DatabaseInitializationProgress(
                "Creating local database",
                "Applying the initial Argus schema."));
            await db.Database.MigrateAsync(cancellationToken);
        }
        else
        {
            var tableCount = 0;
            var connection = db.Database.GetDbConnection();
            var shouldClose = connection.State != System.Data.ConnectionState.Open;
            if (shouldClose)
            {
                await connection.OpenAsync(cancellationToken);
            }

            try
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%';";
                tableCount = Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken));
            }
            finally
            {
                if (shouldClose)
                {
                    await connection.CloseAsync();
                }
            }

            if (tableCount == 0)
            {
                progress?.Report(new DatabaseInitializationProgress(
                    "Creating local database",
                    "Applying the initial Argus schema."));
                await db.Database.MigrateAsync(cancellationToken);
            }
            else
            {
                bool hasHistory = false;
                if (shouldClose)
                {
                    await connection.OpenAsync(cancellationToken);
                }
                try
                {
                    using var cmd = connection.CreateCommand();
                    cmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='__EFMigrationsHistory';";
                    hasHistory = Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken)) > 0;
                }
                finally
                {
                    if (shouldClose)
                    {
                        await connection.CloseAsync();
                    }
                }

                if (!hasHistory)
                {
                    progress?.Report(new DatabaseInitializationProgress(
                        "Adopting existing database",
                        "Recording migration history for an older Argus database."));
                    await db.Database.ExecuteSqlRawAsync(
                        @"CREATE TABLE ""__EFMigrationsHistory"" (
                            ""MigrationId"" TEXT NOT NULL CONSTRAINT ""PK___EFMigrationsHistory"" PRIMARY KEY,
                            ""ProductVersion"" TEXT NOT NULL
                        );", cancellationToken);
                    await db.Database.ExecuteSqlRawAsync(
                        @"INSERT INTO ""__EFMigrationsHistory"" (""MigrationId"", ""ProductVersion"")
                          VALUES ('20260604194301_InitialCreate', '10.0.8');", cancellationToken);
                }

                progress?.Report(new DatabaseInitializationProgress(
                    "Applying database upgrades",
                    "Updating the local schema without sending data anywhere."));
                await db.Database.MigrateAsync(cancellationToken);
            }
        }

        progress?.Report(new DatabaseInitializationProgress(
            "Checking local search",
            "Refreshing SQLite full-text search indexes."));
        await EnsureSearchIndexAsync(db, cancellationToken);

        progress?.Report(new DatabaseInitializationProgress(
            "Updating provider settings",
            "Preserving model choices while applying provider compatibility fixes."));
        if (!await db.AiProviderProfiles.AnyAsync(cancellationToken))
        {
            db.AiProviderProfiles.AddRange(CreateProviderProfiles());
        }
        else
        {
            await UpgradeProviderProfilesAsync(db, cancellationToken);
        }

        await RemoveLegacyOAuthProfilesAsync(db, cancellationToken);
        await CleanupAutomaticChatArtifactsAsync(db, cancellationToken);

        progress?.Report(new DatabaseInitializationProgress(
            "Loading workspace structure",
            "Checking graph seed content and local application settings."));
        if (!await db.Nodes.AnyAsync(cancellationToken))
        {
            SeedGraph(db);
        }

        await UpgradePublicSeedContentAsync(db, cancellationToken);

        if (!await db.AppSettings.AnyAsync(setting => setting.Key == "DatabaseVersion", cancellationToken))
        {
            db.AppSettings.Add(new AppSetting { Key = "DatabaseVersion", Value = "1" });
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private static async Task UpgradeProviderProfilesAsync(ArgusDbContext db, CancellationToken cancellationToken)
    {
        var deepSeekDefaultsMigrated = await db.AppSettings.AnyAsync(setting => setting.Key == DeepSeekDefaultsMigrationKey, cancellationToken);
        var deepSeek = await db.AiProviderProfiles.FirstOrDefaultAsync(profile => profile.Name == "DeepSeek", cancellationToken);
        if (deepSeek is not null)
        {
            var legacyDeepSeekChat = string.Concat("deepseek-", "chat");
            var legacyDeepSeekReasoner = string.Concat("deepseek-", "reasoner");
            deepSeek.ProviderType = "DeepSeek";
            deepSeek.BaseUrl = "https://api.deepseek.com";
            deepSeek.Model = string.IsNullOrWhiteSpace(deepSeek.Model) ||
                deepSeek.Model.Equals(legacyDeepSeekChat, StringComparison.OrdinalIgnoreCase) ||
                deepSeek.Model.Equals(legacyDeepSeekReasoner, StringComparison.OrdinalIgnoreCase)
                ? "deepseek-v4-pro"
                : deepSeek.Model;
            deepSeek.ApiKeyStorageKey = "ai.deepseek.api_key";
            deepSeek.ThinkingMode = string.IsNullOrWhiteSpace(deepSeek.ThinkingMode)
                ? "enabled"
                : deepSeek.ThinkingMode;
            deepSeek.ReasoningEffort = (deepSeek.ReasoningEffort ?? string.Empty).Trim().ToLowerInvariant() switch
            {
                "max" or "xhigh" => "max",
                _ => "high"
            };
        }

        if (!await db.AiProviderProfiles.AnyAsync(profile => profile.Name == "OpenAI", cancellationToken))
        {
            db.AiProviderProfiles.Add(new AiProviderProfile
            {
                Name = "OpenAI",
                ProviderType = "OpenAI",
                BaseUrl = "https://api.openai.com/v1",
                Model = "gpt-5.5",
                ApiKeyStorageKey = "ai.openai.api_key",
                ThinkingMode = "enabled",
                ReasoningEffort = "medium"
            });
        }

        if (!await db.AppSettings.AnyAsync(setting => setting.Key == OpenAiCodexProviderMigrationKey, cancellationToken))
        {
            if (!await db.AiProviderProfiles.AnyAsync(profile => profile.ProviderType == "OpenAICodex", cancellationToken))
            {
                db.AiProviderProfiles.Add(new AiProviderProfile
                {
                    Name = "OpenAI Codex (ChatGPT)",
                    ProviderType = "OpenAICodex",
                    BaseUrl = "codex://app-server",
                    Model = "gpt-5.5",
                    ApiKeyStorageKey = string.Empty,
                    ThinkingMode = "enabled",
                    ReasoningEffort = "medium"
                });
            }

            db.AppSettings.Add(new AppSetting
            {
                Key = OpenAiCodexProviderMigrationKey,
                Value = "applied",
                UpdatedAt = DateTimeOffset.UtcNow
            });
        }

        var codex = await db.AiProviderProfiles.FirstOrDefaultAsync(
            profile => profile.ProviderType == "OpenAICodex",
            cancellationToken);
        if (codex is not null)
        {
            codex.ThinkingMode = "enabled";
            if (string.IsNullOrWhiteSpace(codex.ReasoningEffort) ||
                codex.ReasoningEffort.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                codex.ReasoningEffort = "medium";
            }
        }

        var openRouter = await db.AiProviderProfiles.FirstOrDefaultAsync(profile => profile.Name == "OpenRouter", cancellationToken);
        if (openRouter is not null)
        {
            var legacyOpenRouterDeepSeekChat = string.Concat("deepseek/deepseek-", "chat");
            openRouter.ProviderType = "OpenRouter";
            openRouter.BaseUrl = "https://openrouter.ai/api/v1";
            openRouter.Model = string.IsNullOrWhiteSpace(openRouter.Model) ||
                openRouter.Model.Equals(legacyOpenRouterDeepSeekChat, StringComparison.OrdinalIgnoreCase)
                ? "deepseek/deepseek-v4-pro"
                : openRouter.Model;
            openRouter.ApiKeyStorageKey = "ai.openrouter.api_key";
        }

        if (!await db.AiProviderProfiles.AnyAsync(profile => profile.Name == "LM Studio", cancellationToken))
        {
            db.AiProviderProfiles.Add(new AiProviderProfile
            {
                Name = "LM Studio",
                ProviderType = "OpenAICompatible",
                BaseUrl = "http://localhost:1234/v1",
                Model = "lmstudio-model",
                ApiKeyStorageKey = "ai.lm-studio.api_key"
            });
        }

        if (!await db.AiProviderProfiles.AnyAsync(profile => profile.Name == "Anthropic", cancellationToken))
        {
            db.AiProviderProfiles.Add(new AiProviderProfile
            {
                Name = "Anthropic",
                ProviderType = "Anthropic",
                BaseUrl = "https://api.anthropic.com/v1",
                Model = "claude-sonnet-4-6",
                ApiKeyStorageKey = "ai.anthropic.api_key",
                ThinkingMode = "disabled"
            });
        }
        else
        {
            var anthropic = await db.AiProviderProfiles.FirstAsync(profile => profile.Name == "Anthropic", cancellationToken);
            if (anthropic.Model is "claude-3-5-sonnet-latest" or "claude-3-5-haiku-latest" or "claude-3-opus-latest")
            {
                anthropic.Model = "claude-sonnet-4-6";
            }
        }

        if (!deepSeekDefaultsMigrated)
        {
            db.AppSettings.Add(new AppSetting
            {
                Key = DeepSeekDefaultsMigrationKey,
                Value = "applied",
                UpdatedAt = DateTimeOffset.UtcNow
            });
        }

        var defaultProfiles = await db.AiProviderProfiles
            .Where(profile => profile.IsDefault)
            .ToListAsync(cancellationToken);
        var providerDefaultRepairApplied = await db.AppSettings.AnyAsync(
            setting => setting.Key == ProviderDefaultRepairMigrationKey,
            cancellationToken);
        if (!providerDefaultRepairApplied)
        {
            if (defaultProfiles.Count > 1)
            {
                var preferred = defaultProfiles.FirstOrDefault(
                    profile => !profile.ProviderType.Equals("DeepSeek", StringComparison.OrdinalIgnoreCase))
                    ?? defaultProfiles[0];
                foreach (var profile in defaultProfiles)
                {
                    profile.IsDefault = profile.Id == preferred.Id;
                }
            }
            else if (defaultProfiles.Count == 0)
            {
                var fallback = deepSeek ?? await db.AiProviderProfiles.FirstOrDefaultAsync(cancellationToken);
                if (fallback is not null)
                {
                    fallback.IsDefault = true;
                }
            }

            db.AppSettings.Add(new AppSetting
            {
                Key = ProviderDefaultRepairMigrationKey,
                Value = "applied",
                UpdatedAt = DateTimeOffset.UtcNow
            });
        }
    }

    private static async Task RemoveLegacyOAuthProfilesAsync(
        ArgusDbContext db,
        CancellationToken cancellationToken)
    {
        var legacyProfiles = await db.AiProviderProfiles
            .Where(profile =>
                profile.Name == "OpenAI (OAuth)" ||
                profile.Name == "Anthropic (OAuth)" ||
                profile.ProviderType == "OpenAI-OAuth" ||
                profile.ProviderType == "OpenAIOAuth" ||
                profile.ProviderType == "Anthropic-OAuth" ||
                profile.ProviderType == "AnthropicOAuth")
            .ToListAsync(cancellationToken);

        if (legacyProfiles.Count > 0)
        {
            db.AiProviderProfiles.RemoveRange(legacyProfiles);
        }
    }

    private static async Task CleanupAutomaticChatArtifactsAsync(
        ArgusDbContext db,
        CancellationToken cancellationToken)
    {
        if (await db.AppSettings.AnyAsync(
                setting => setting.Key == ChatArtifactCleanupMigrationKey,
                cancellationToken))
        {
            return;
        }

        var automaticChatMemories = await db.Memories
            .Where(memory => memory.Source == "chat:user" || memory.Source == "chat:assistant")
            .ToListAsync(cancellationToken);
        db.Memories.RemoveRange(automaticChatMemories);

        var automaticChatNodes = await db.Nodes
            .Where(node =>
                node.Type == "Memory" &&
                node.Status == "Captured" &&
                node.IconKey == "memory")
            .ToListAsync(cancellationToken);
        db.Nodes.RemoveRange(automaticChatNodes);

        const string fakeSubscriptionMarker = "This is a subscription model response from";
        var fakeSubscriptionMessages = await db.Messages
            .Where(message => message.Content.Contains(fakeSubscriptionMarker))
            .ToListAsync(cancellationToken);
        db.Messages.RemoveRange(fakeSubscriptionMessages);

        db.AppSettings.Add(new AppSetting
        {
            Key = ChatArtifactCleanupMigrationKey,
            Value = "applied",
            UpdatedAt = DateTimeOffset.UtcNow
        });
    }

    private static async Task UpgradePublicSeedContentAsync(ArgusDbContext db, CancellationToken cancellationToken)
    {
        if (await db.AppSettings.AnyAsync(setting => setting.Key == PublicSeedContentMigrationKey, cancellationToken))
        {
            return;
        }

        const string oldWelcome =
            "Argus is ready. Add nodes, connect projects, and configure an OpenAI-compatible provider when you want live chat.";
        const string newWelcome =
            "Argus is ready. Connect an LLM to chat, use tools, recall durable memory, and work with your graph and project context.";

        var welcomeMessages = await db.Messages
            .Where(message => message.Role == "assistant" && message.Content == oldWelcome)
            .ToListAsync(cancellationToken);
        foreach (var message in welcomeMessages)
        {
            message.Content = newWelcome;
        }

        var argusNode = await db.Nodes.FirstOrDefaultAsync(
            node =>
                node.Title == "Argus" &&
                node.Summary == "Local-first AI command center" &&
                node.Body == "Local-first AI command center",
            cancellationToken);
        if (argusNode is not null)
        {
            argusNode.Summary = "Local-first AI agent with durable memory and connected context";
            argusNode.Body = argusNode.Summary;
            argusNode.UpdatedAt = DateTimeOffset.UtcNow;
        }

        var providersNode = await db.Nodes.FirstOrDefaultAsync(
            node =>
                node.Title == "Model Providers" &&
                node.Summary == "DeepSeek, OpenAI, OpenRouter, and custom endpoints" &&
                node.Body == "DeepSeek, OpenAI, OpenRouter, and custom endpoints",
            cancellationToken);
        if (providersNode is not null)
        {
            providersNode.Title = "LLM Connections";
            providersNode.Summary = "DeepSeek, OpenAI, OpenRouter, local LLMs, and custom endpoints";
            providersNode.Body = providersNode.Summary;
            providersNode.UpdatedAt = DateTimeOffset.UtcNow;
        }

        db.AppSettings.Add(new AppSetting
        {
            Key = PublicSeedContentMigrationKey,
            Value = "applied",
            UpdatedAt = DateTimeOffset.UtcNow
        });
    }

    private static async Task EnsureSearchIndexAsync(ArgusDbContext db, CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE VIRTUAL TABLE IF NOT EXISTS NodeSearch USING fts5(
                NodeId UNINDEXED,
                Title,
                Type,
                Summary,
                Body
            );
            """,
            cancellationToken);

        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TRIGGER IF NOT EXISTS Nodes_Search_Insert AFTER INSERT ON Nodes BEGIN
                INSERT INTO NodeSearch(NodeId, Title, Type, Summary, Body)
                VALUES (new.Id, new.Title, new.Type, coalesce(new.Summary, ''), coalesce(new.Body, ''));
            END;
            """,
            cancellationToken);

        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TRIGGER IF NOT EXISTS Nodes_Search_Delete AFTER DELETE ON Nodes BEGIN
                DELETE FROM NodeSearch WHERE NodeId = old.Id;
            END;
            """,
            cancellationToken);

        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TRIGGER IF NOT EXISTS Nodes_Search_Update AFTER UPDATE ON Nodes BEGIN
                DELETE FROM NodeSearch WHERE NodeId = old.Id;
                INSERT INTO NodeSearch(NodeId, Title, Type, Summary, Body)
                VALUES (new.Id, new.Title, new.Type, coalesce(new.Summary, ''), coalesce(new.Body, ''));
            END;
            """,
            cancellationToken);

        await db.Database.ExecuteSqlRawAsync(
            """
            DELETE FROM NodeSearch;
            INSERT INTO NodeSearch(NodeId, Title, Type, Summary, Body)
            SELECT Id, Title, Type, coalesce(Summary, ''), coalesce(Body, '')
            FROM Nodes;
            """,
            cancellationToken);

        // FTS5 MessageSearch table
        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE VIRTUAL TABLE IF NOT EXISTS MessageSearch USING fts5(
                MessageId UNINDEXED,
                ConversationId UNINDEXED,
                Role,
                Content
            );
            """,
            cancellationToken);

        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TRIGGER IF NOT EXISTS Messages_Search_Insert AFTER INSERT ON Messages BEGIN
                INSERT INTO MessageSearch(MessageId, ConversationId, Role, Content)
                VALUES (new.Id, new.ConversationId, new.Role, coalesce(new.Content, ''));
            END;
            """,
            cancellationToken);

        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TRIGGER IF NOT EXISTS Messages_Search_Delete AFTER DELETE ON Messages BEGIN
                DELETE FROM MessageSearch WHERE MessageId = old.Id;
            END;
            """,
            cancellationToken);

        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TRIGGER IF NOT EXISTS Messages_Search_Update AFTER UPDATE ON Messages BEGIN
                DELETE FROM MessageSearch WHERE MessageId = old.Id;
                INSERT INTO MessageSearch(MessageId, ConversationId, Role, Content)
                VALUES (new.Id, new.ConversationId, new.Role, coalesce(new.Content, ''));
            END;
            """,
            cancellationToken);

        await db.Database.ExecuteSqlRawAsync(
            """
            DELETE FROM MessageSearch;
            INSERT INTO MessageSearch(MessageId, ConversationId, Role, Content)
            SELECT Id, ConversationId, Role, coalesce(Content, '')
            FROM Messages;
            """,
            cancellationToken);
    }

    private static IEnumerable<AiProviderProfile> CreateProviderProfiles()
    {
        return new[]
        {
            new AiProviderProfile
            {
                Name = "DeepSeek",
                ProviderType = "DeepSeek",
                BaseUrl = "https://api.deepseek.com",
                Model = "deepseek-v4-pro",
                ApiKeyStorageKey = "ai.deepseek.api_key",
                ThinkingMode = "enabled",
                ReasoningEffort = "high",
                IsDefault = true
            },
            new AiProviderProfile
            {
                Name = "OpenAI",
                ProviderType = "OpenAI",
                BaseUrl = "https://api.openai.com/v1",
                Model = "gpt-5.5",
                ApiKeyStorageKey = "ai.openai.api_key",
                ThinkingMode = "enabled",
                ReasoningEffort = "medium"
            },
            new AiProviderProfile
            {
                Name = "OpenAI Codex (ChatGPT)",
                ProviderType = "OpenAICodex",
                BaseUrl = "codex://app-server",
                Model = "gpt-5.5",
                ApiKeyStorageKey = string.Empty,
                ThinkingMode = "enabled",
                ReasoningEffort = "medium"
            },
            new AiProviderProfile
            {
                Name = "OpenRouter",
                ProviderType = "OpenRouter",
                BaseUrl = "https://openrouter.ai/api/v1",
                Model = "deepseek/deepseek-v4-pro",
                ApiKeyStorageKey = "ai.openrouter.api_key"
            },
            new AiProviderProfile
            {
                Name = "Anthropic",
                ProviderType = "Anthropic",
                BaseUrl = "https://api.anthropic.com/v1",
                Model = "claude-sonnet-4-6",
                ApiKeyStorageKey = "ai.anthropic.api_key",
                ThinkingMode = "disabled"
            },
            new AiProviderProfile
            {
                Name = "Local Model",
                ProviderType = "OpenAICompatible",
                BaseUrl = "http://localhost:11434/v1",
                Model = "local-model",
                ApiKeyStorageKey = "ai.local-model.api_key"
            },
            new AiProviderProfile
            {
                Name = "LM Studio",
                ProviderType = "OpenAICompatible",
                BaseUrl = "http://localhost:1234/v1",
                Model = "lmstudio-model",
                ApiKeyStorageKey = "ai.lm-studio.api_key"
            },
            new AiProviderProfile
            {
                Name = "Custom",
                ProviderType = "OpenAICompatible",
                BaseUrl = "http://localhost:8000/v1",
                Model = "custom-model",
                ApiKeyStorageKey = "ai.custom.api_key"
            }
        };
    }

    private static void SeedGraph(ArgusDbContext db)
    {
        var now = DateTimeOffset.UtcNow;
        var nodes = new Dictionary<string, Node>(StringComparer.OrdinalIgnoreCase)
        {
            ["Argus"] = CreateNode("Argus", "Project", "Local-first AI agent with durable memory and connected context", 5, "cyan", 520, 330, now),
            ["Argus Agent"] = CreateNode("Argus Agent", "Agent", "Supervised local agent loop", 5, "violet", 790, 210, now),
            ["Research Brief"] = CreateNode("Research Brief", "Project", "Collect and connect research for a new product idea", 4, "magenta", 270, 210, now),
            ["Product Launch"] = CreateNode("Product Launch", "Project", "A sample launch plan with tasks and decisions", 4, "amber", 250, 500, now),
            ["Automation Lab"] = CreateNode("Automation Lab", "Project", "Experiments for useful local automations", 3, "orange", 700, 520, now),
            ["Telegram Gateway"] = CreateNode("Telegram Gateway", "Tool", "Secure remote access from Telegram", 3, "blue", 120, 350, now),
            ["Web Search"] = CreateNode("Web Search", "Tool", "Private web research through local SearXNG", 4, "teal", 900, 390, now),
            ["Local Model"] = CreateNode("Local Model", "Tool", "OpenAI-compatible local inference endpoint", 3, "rose", 960, 120, now),
            ["LLM Connections"] = CreateNode("LLM Connections", "Note", "DeepSeek, OpenAI, OpenRouter, local LLMs, and custom endpoints", 3, "pink", 1030, 280, now),
            ["Knowledge Base"] = CreateNode("Knowledge Base", "Memory", "SQLite-backed durable recall", 5, "green", 560, 120, now),
            ["Graph Explorer"] = CreateNode("Graph Explorer", "Idea", "Navigate relationships across projects, memories, and decisions", 4, "yellow", 420, 85, now)
        };

        db.Nodes.AddRange(nodes.Values);
        db.Edges.AddRange(
            CreateEdge(nodes["Research Brief"], nodes["Web Search"], "uses", 0.8, now),
            CreateEdge(nodes["Research Brief"], nodes["LLM Connections"], "uses", 0.7, now),
            CreateEdge(nodes["Argus Agent"], nodes["Research Brief"], "related_to", 0.7, now),
            CreateEdge(nodes["Argus"], nodes["Graph Explorer"], "inspired_by", 0.75, now),
            CreateEdge(nodes["Argus"], nodes["Knowledge Base"], "uses", 0.9, now),
            CreateEdge(nodes["Argus"], nodes["Argus Agent"], "related_to", 0.62, now),
            CreateEdge(nodes["Automation Lab"], nodes["Argus"], "related_to", 0.58, now),
            CreateEdge(nodes["Local Model"], nodes["LLM Connections"], "belongs_to", 0.72, now),
            CreateEdge(nodes["Web Search"], nodes["Telegram Gateway"], "related_to", 0.52, now));

        db.Conversations.Add(new Conversation
        {
            Title = "Welcome to Argus",
            CreatedAt = now,
            UpdatedAt = now,
            Messages =
            {
                new Message
                {
                    Role = "assistant",
                    Content = "Argus is ready. Connect an LLM to chat, use tools, recall durable memory, and work with your graph and project context.",
                    CreatedAt = now,
                    LinkedNodeId = nodes["Argus"].Id
                }
            }
        });

        db.Memories.Add(new Memory
        {
            Text = "Argus keeps graph data, conversations, settings, and memories in a local SQLite database.",
            Source = "seed",
            Importance = 5,
            CreatedAt = now,
            LinkedNodeId = nodes["Argus"].Id
        });
    }

    private static Node CreateNode(string title, string type, string summary, int importance, string colorKey, double x, double y, DateTimeOffset now)
    {
        return new Node
        {
            Title = title,
            Type = type,
            Summary = summary,
            Body = summary,
            Importance = importance,
            ColorKey = colorKey,
            IconKey = type.ToLowerInvariant(),
            CreatedAt = now,
            UpdatedAt = now,
            LastTouchedAt = now,
            PositionX = x,
            PositionY = y
        };
    }

    private static Edge CreateEdge(Node source, Node target, string relationshipType, double strength, DateTimeOffset now)
    {
        return new Edge
        {
            SourceNodeId = source.Id,
            TargetNodeId = target.Id,
            RelationshipType = relationshipType,
            Strength = strength,
            CreatedAt = now,
            UpdatedAt = now
        };
    }
}
