using Argus.Core.Models;
using Argus.Core.Services;
using Argus.Data;
using Argus.Data.Services;
using Argus.Core.Graph;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;

namespace Argus.Tests;

public sealed class ArgusDataTests
{
    [Fact]
    public async Task InitializerSeedsGraphAndDashboard()
    {
        using var provider = CreateProvider();
        await provider.GetRequiredService<ArgusDatabaseInitializer>().InitializeAsync();

        var graphService = provider.GetRequiredService<IGraphService>();
        var graph = await graphService.GetGraphAsync();
        var dashboard = await graphService.GetDashboardAsync();

        Assert.Contains(graph.Nodes, node => node.Title == "Argus");
        Assert.Contains(graph.Nodes, node => node.Title == "Research Brief");
        Assert.Contains(graph.Nodes, node => node.Title == "Argus Agent");
        Assert.Contains(graph.Edges, edge => edge.RelationshipType == "uses");
        Assert.NotEmpty(dashboard.ActiveProjects);
        Assert.NotEmpty(dashboard.MostConnectedNodes);

        var settings = provider.GetRequiredService<ISettingsService>();
        Assert.Contains(
            await settings.GetAiProviderProfilesAsync(),
            profile => profile.ProviderType == "OpenAICodex" && profile.Name == "OpenAI Codex (ChatGPT)");
    }

    [Fact]
    public async Task InitializerRemovesLegacyOAuthProfilesAndAutomaticChatArtifacts()
    {
        using var provider = CreateProvider();
        var initializer = provider.GetRequiredService<ArgusDatabaseInitializer>();
        await initializer.InitializeAsync();
        var factory = provider.GetRequiredService<IDbContextFactory<ArgusDbContext>>();

        await using (var db = await factory.CreateDbContextAsync())
        {
            db.AppSettings.Remove(
                await db.AppSettings.SingleAsync(setting => setting.Key == "Migration:ChatArtifactCleanup:v1"));
            db.AiProviderProfiles.AddRange(
                new AiProviderProfile
                {
                    Name = "OpenAI (OAuth)",
                    ProviderType = "OpenAI-OAuth",
                    BaseUrl = "https://api.openai.com/v1",
                    Model = "gpt-5.5",
                    ApiKeyStorageKey = "legacy"
                },
                new AiProviderProfile
                {
                    Name = "Anthropic (OAuth)",
                    ProviderType = "Anthropic-OAuth",
                    BaseUrl = "https://api.anthropic.com/v1",
                    Model = "claude-sonnet",
                    ApiKeyStorageKey = "legacy"
                });
            db.Memories.Add(new Memory
            {
                Text = "ordinary chat duplicate",
                Source = "chat:user",
                Importance = 3
            });
            db.Nodes.Add(new Node
            {
                Title = "ordinary chat duplicate",
                Type = "Memory",
                Status = "Captured",
                IconKey = "memory"
            });
            var conversation = await db.Conversations.FirstAsync();
            db.Messages.Add(new Message
            {
                ConversationId = conversation.Id,
                Role = "assistant",
                Content = "[Subscription Mode] This is a subscription model response from OpenAI (OAuth)."
            });
            await db.SaveChangesAsync();
        }

        await initializer.InitializeAsync();

        await using var verified = await factory.CreateDbContextAsync();
        Assert.DoesNotContain(
            await verified.AiProviderProfiles.ToListAsync(),
            profile => profile.Name.EndsWith("(OAuth)", StringComparison.Ordinal));
        Assert.DoesNotContain(
            await verified.Memories.ToListAsync(),
            memory => memory.Source is "chat:user" or "chat:assistant");
        Assert.DoesNotContain(
            await verified.Nodes.ToListAsync(),
            node => node.Type == "Memory" && node.Status == "Captured" && node.IconKey == "memory");
        Assert.DoesNotContain(
            await verified.Messages.ToListAsync(),
            message => message.Content.Contains("subscription model response from", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProviderModelAndReasoningPreferencesSurviveRestartInitialization()
    {
        using var provider = CreateProvider();
        var initializer = provider.GetRequiredService<ArgusDatabaseInitializer>();
        var settings = provider.GetRequiredService<ISettingsService>();
        await initializer.InitializeAsync();

        var profiles = await settings.GetAiProviderProfilesAsync();
        var deepSeek = Assert.Single(profiles, profile => profile.ProviderType == "DeepSeek");
        deepSeek.ThinkingMode = "disabled";
        deepSeek.ReasoningEffort = "max";
        await settings.SaveAiProviderProfileAsync(deepSeek);

        var codex = Assert.Single(profiles, profile => profile.ProviderType == "OpenAICodex");
        codex.Model = "gpt-5.4";
        codex.ThinkingMode = "enabled";
        codex.ReasoningEffort = "xhigh";
        codex.IsDefault = true;
        await settings.SaveAiProviderProfileAsync(codex);

        await initializer.InitializeAsync();

        var restartedProfiles = await settings.GetAiProviderProfilesAsync();
        var restartedCodex = Assert.Single(restartedProfiles, profile => profile.ProviderType == "OpenAICodex");
        var restartedDeepSeek = Assert.Single(restartedProfiles, profile => profile.ProviderType == "DeepSeek");
        Assert.True(restartedCodex.IsDefault);
        Assert.False(restartedDeepSeek.IsDefault);
        Assert.Equal("gpt-5.4", restartedCodex.Model);
        Assert.Equal("enabled", restartedCodex.ThinkingMode);
        Assert.Equal("xhigh", restartedCodex.ReasoningEffort);
        Assert.Equal("disabled", restartedDeepSeek.ThinkingMode);
        Assert.Equal("max", restartedDeepSeek.ReasoningEffort);
    }

    [Fact]
    public async Task StartupCreatesVerifiedBackupBeforeDatabaseInitialization()
    {
        using var provider = CreateProvider();
        var initializer = provider.GetRequiredService<ArgusDatabaseInitializer>();
        var settings = provider.GetRequiredService<ISettingsService>();
        var startup = provider.GetRequiredService<DatabaseStartupService>();
        await initializer.InitializeAsync();
        await settings.SaveSettingAsync("BackupTestMarker", "before-startup");

        var result = await startup.StartAsync();

        Assert.True(result.Backup.Created);
        Assert.NotNull(result.Backup.BackupPath);
        Assert.True(File.Exists(result.Backup.BackupPath));

        var backupServices = new ServiceCollection();
        backupServices.AddArgusData(result.Backup.BackupPath!);
        using var backupProvider = backupServices.BuildServiceProvider();
        var backupSettings = backupProvider.GetRequiredService<ISettingsService>();
        Assert.Equal("before-startup", await backupSettings.GetSettingAsync("BackupTestMarker"));
    }

    [Fact]
    public async Task RestoreLatestBackupRollsBackDatabaseAndPreservesReplacedCopy()
    {
        using var provider = CreateProvider();
        var initializer = provider.GetRequiredService<ArgusDatabaseInitializer>();
        var settings = provider.GetRequiredService<ISettingsService>();
        var backups = provider.GetRequiredService<DatabaseBackupService>();
        await initializer.InitializeAsync();
        await settings.SaveSettingAsync("RestoreTestMarker", "backup-value");
        var backup = await backups.CreateStartupBackupAsync();
        Assert.True(backup.Created);

        await settings.SaveSettingAsync("RestoreTestMarker", "newer-value");
        var restore = await backups.RestoreLatestBackupAsync();

        Assert.True(restore.Restored);
        Assert.Equal("backup-value", await settings.GetSettingAsync("RestoreTestMarker"));
        Assert.NotEmpty(Directory.EnumerateFiles(
            backups.BackupDirectory,
            "argus-before-restore-*.db",
            SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task FirstStartupInitializesWithoutCreatingEmptyBackup()
    {
        using var provider = CreateProvider();
        var startup = provider.GetRequiredService<DatabaseStartupService>();

        var result = await startup.StartAsync();

        Assert.False(result.Backup.Created);
        var graph = await provider.GetRequiredService<IGraphService>().GetGraphAsync();
        Assert.Contains(graph.Nodes, node => node.Title == "Argus");
    }

    [Fact]
    public async Task GraphServiceCreatesSearchesConnectsAndDeletesNodes()
    {
        using var provider = CreateProvider();
        await provider.GetRequiredService<ArgusDatabaseInitializer>().InitializeAsync();
        var graphService = provider.GetRequiredService<IGraphService>();

        var source = await graphService.CreateNodeAsync(new Node
        {
            Title = "Decision Log",
            Type = "Decision",
            Summary = "Important architecture decisions",
            Importance = 4
        });
        var target = (await graphService.SearchNodesAsync("Argus")).First(node => node.Title == "Argus");
        var edge = await graphService.CreateEdgeAsync(source.Id, target.Id, "decided_in", 0.8);

        var matches = await graphService.SearchNodesAsync("Decision Log");
        var connections = await graphService.GetConnectionsAsync(source.Id);

        Assert.Contains(matches, node => node.Id == source.Id);
        Assert.Contains(connections, connection => connection.EdgeId == edge.Id && connection.NodeId == target.Id);

        source.Body = "Updated body";
        await graphService.UpdateNodeAsync(source);
        await graphService.DeleteNodeAsync(source.Id);

        Assert.DoesNotContain(await graphService.SearchNodesAsync("Decision Log"), node => node.Id == source.Id);
    }

    [Fact]
    public async Task ConversationMessagesCanBecomeMemories()
    {
        using var provider = CreateProvider();
        await provider.GetRequiredService<ArgusDatabaseInitializer>().InitializeAsync();
        var conversations = provider.GetRequiredService<IConversationService>();
        var memories = provider.GetRequiredService<IMemoryService>();

        var conversation = await conversations.CreateConversationAsync("Test chat");
        var message = await conversations.AddMessageAsync(conversation.Id, "user", "Remember that Argus is local-first.");
        await memories.SaveMemoryAsync(message.Content, "chat:user", 5);

        var recalled = await memories.SearchMemoriesAsync("local-first");

        Assert.Contains(recalled, memory => memory.Text.Contains("local-first", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task MemoryRecallDetailsExplainLexicalRankingAndSource()
    {
        using var provider = CreateProvider();
        await provider.GetRequiredService<ArgusDatabaseInitializer>().InitializeAsync();
        var memories = provider.GetRequiredService<IMemoryService>();
        var important = await memories.SaveMemoryAsync(
            "Argus uses local-first storage for durable memory.",
            "decision:architecture",
            5);
        await memories.SaveMemoryAsync(
            "The local-first storage documentation needs another example.",
            "note:docs",
            1);

        var recalled = await memories.RecallWithDetailsAsync("local-first storage", 10);

        Assert.NotEmpty(recalled);
        var first = recalled[0];
        Assert.Equal(important.Id, first.Memory.Id);
        Assert.Equal(MemoryRecallMethod.ExactPhrase, first.Method);
        Assert.Equal(1, first.LexicalScore);
        Assert.True(first.Score > recalled.Last().Score);
        Assert.Contains("exact phrase match", first.Explanation);
        Assert.Contains("source decision:architecture", first.Explanation);
    }

    [Fact]
    public async Task MemoryRecallFeedbackPersistsQueryRatingAndMemoryLink()
    {
        using var provider = CreateProvider();
        await provider.GetRequiredService<ArgusDatabaseInitializer>().InitializeAsync();
        var memories = provider.GetRequiredService<IMemoryService>();
        var memory = await memories.SaveMemoryAsync(
            "Argus stores recall evaluation feedback locally.",
            "test",
            4);

        var saved = await memories.SaveRecallFeedbackAsync(
            "Where is recall feedback stored?",
            memory.Id,
            "useful");

        var factory = provider.GetRequiredService<IDbContextFactory<ArgusDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        var persisted = await db.MemoryRecallFeedback.SingleAsync(item => item.Id == saved.Id);
        Assert.Equal(memory.Id, persisted.MemoryId);
        Assert.Equal("Where is recall feedback stored?", persisted.Query);
        Assert.Equal("useful", persisted.Rating);
    }

    [Fact]
    public async Task GraphSearchUsesBodyTextIndex()
    {
        using var provider = CreateProvider();
        await provider.GetRequiredService<ArgusDatabaseInitializer>().InitializeAsync();
        var graphService = provider.GetRequiredService<IGraphService>();

        var node = await graphService.CreateNodeAsync(new Node
        {
            Title = "Search Surface",
            Type = "Note",
            Body = "The premium graph renderer should find phosphor trails through FTS."
        });

        var matches = await graphService.SearchNodesAsync("phosphor trails");

        Assert.Contains(matches, match => match.Id == node.Id);
    }

    [Fact]
    public async Task TagServiceManagesNodeTags()
    {
        using var provider = CreateProvider();
        await provider.GetRequiredService<ArgusDatabaseInitializer>().InitializeAsync();
        var graphService = provider.GetRequiredService<IGraphService>();
        var tagService = provider.GetRequiredService<ITagService>();

        var node = await graphService.CreateNodeAsync(new Node { Title = "Tagged Node", Type = "Note" });
        await tagService.AddTagToNodeAsync(node.Id, "Research", "green");
        await tagService.SetNodeTagsAsync(node.Id, new[] { "Research", "Local First" });

        var nodeTags = await tagService.GetNodeTagsAsync(node.Id);
        Assert.Equal(new[] { "Local First", "Research" }, nodeTags.Select(tag => tag.Name).ToArray());

        await tagService.RemoveTagFromNodeAsync(node.Id, "research");
        nodeTags = await tagService.GetNodeTagsAsync(node.Id);
        Assert.Equal("Local First", Assert.Single(nodeTags).Name);
    }

    [Fact]
    public async Task GraphExchangeExportsAndImportsJson()
    {
        using var sourceProvider = CreateProvider();
        await sourceProvider.GetRequiredService<ArgusDatabaseInitializer>().InitializeAsync();
        var sourceGraph = sourceProvider.GetRequiredService<IGraphService>();
        var sourceTags = sourceProvider.GetRequiredService<ITagService>();
        var sourceExchange = sourceProvider.GetRequiredService<IGraphExchangeService>();

        var node = await sourceGraph.CreateNodeAsync(new Node
        {
            Title = "Portable Node",
            Type = "Project",
            Summary = "Export me"
        });
        await sourceTags.AddTagToNodeAsync(node.Id, "Portable", "blue");

        var json = await sourceExchange.ExportJsonAsync();

        using var targetProvider = CreateProvider();
        await targetProvider.GetRequiredService<ArgusDatabaseInitializer>().InitializeAsync();
        var targetExchange = targetProvider.GetRequiredService<IGraphExchangeService>();
        var imported = await targetExchange.ImportJsonAsync(json, GraphImportMode.Replace);
        var targetTags = targetProvider.GetRequiredService<ITagService>();

        Assert.Contains(imported.Nodes, importedNode => importedNode.Id == node.Id && importedNode.Title == "Portable Node");
        Assert.Contains(await targetTags.GetNodeTagsAsync(node.Id), tag => tag.Name == "Portable");
    }

    [Fact]
    public async Task DatabaseBackupServiceSupportsManualBackupsRestoreDeleteAndIntegrityCheck()
    {
        using var provider = CreateProvider();
        var initializer = provider.GetRequiredService<ArgusDatabaseInitializer>();
        var settings = provider.GetRequiredService<ISettingsService>();
        var backups = provider.GetRequiredService<DatabaseBackupService>();
        
        await initializer.InitializeAsync();
        await settings.SaveSettingAsync("BackupMarker", "original-value");

        // 1. Manual Backup
        var manualResult = await backups.CreateManualBackupAsync("integrationtest");
        Assert.True(manualResult.Created);
        Assert.NotNull(manualResult.BackupPath);
        Assert.True(File.Exists(manualResult.BackupPath));
        Assert.Contains("argus-manual-integrationtest", manualResult.BackupPath);

        // 2. GetBackups list
        var list = backups.GetBackups();
        Assert.Contains(list, b => b.FilePath == manualResult.BackupPath && b.IsManual);

        // 3. Database Size
        var size = backups.GetDatabaseSizeInBytes();
        Assert.True(size > 0);

        // 4. Integrity Check
        var integrity = await backups.RunIntegrityCheckAsync();
        Assert.Equal("ok", integrity);

        // 5. Restore specific backup
        await settings.SaveSettingAsync("BackupMarker", "modified-value");
        var restoreResult = await backups.RestoreBackupAsync(manualResult.BackupPath);
        Assert.True(restoreResult.Restored);
        
        // Re-read value to confirm restore worked
        Assert.Equal("original-value", await settings.GetSettingAsync("BackupMarker"));

        // 6. Delete backup
        backups.DeleteBackup(manualResult.BackupPath);
        Assert.False(File.Exists(manualResult.BackupPath));
        Assert.DoesNotContain(backups.GetBackups(), b => b.FilePath == manualResult.BackupPath);
    }

    private static ServiceProvider CreateProvider()
    {
        var path = Path.Combine(Path.GetTempPath(), "ArgusTests", $"{Guid.NewGuid():N}.db");
        var services = new ServiceCollection();
        services.AddArgusData(path);
        return services.BuildServiceProvider();
    }
}
