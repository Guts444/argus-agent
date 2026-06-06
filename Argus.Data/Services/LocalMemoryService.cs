using Argus.Core.Models;
using Argus.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Argus.Data.Services;

public sealed class LocalMemoryService(
    IDbContextFactory<ArgusDbContext> dbContextFactory,
    ISettingsService settingsService,
    IAiChatService? aiChatService = null) : IMemoryService, IMemoryProvider
{
    public async Task<Memory> SaveMemoryAsync(string text, string source, int importance, Guid? linkedNodeId = null, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        string? embeddingJson = null;
        if (aiChatService is not null)
        {
            var profile = await settingsService.GetDefaultAiProviderProfileAsync(cancellationToken);
            var embedding = await aiChatService.GenerateEmbeddingAsync(profile, text, cancellationToken);
            if (embedding is not null)
            {
                embeddingJson = System.Text.Json.JsonSerializer.Serialize(embedding);
            }
        }

        var memory = new Memory
        {
            Text = text.Trim(),
            Source = string.IsNullOrWhiteSpace(source) ? "manual" : source.Trim(),
            Importance = Math.Clamp(importance, 1, 5),
            CreatedAt = DateTimeOffset.UtcNow,
            LinkedNodeId = linkedNodeId,
            EmbeddingJson = embeddingJson
        };
        db.Memories.Add(memory);
        await db.SaveChangesAsync(cancellationToken);
        return memory;
    }

    public async Task<IReadOnlyList<Memory>> SearchMemoriesAsync(string query, int take = 20, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var trimmed = query.Trim();
        if (trimmed.Length == 0)
        {
            return await db.Memories.AsNoTracking()
                .OrderByDescending(memory => memory.Importance)
                .ThenByDescending(memory => memory.CreatedAt)
                .Take(take)
                .ToListAsync(cancellationToken);
        }

        // 1. Exact phrase match
        var pattern = $"%{trimmed}%";
        var exactResults = await db.Memories.AsNoTracking()
            .Where(memory => EF.Functions.Like(memory.Text, pattern) || EF.Functions.Like(memory.Source, pattern))
            .OrderByDescending(memory => memory.Importance)
            .ThenByDescending(memory => memory.CreatedAt)
            .Take(take)
            .ToListAsync(cancellationToken);

        List<Memory> results;
        if (exactResults.Count >= take)
        {
            results = exactResults;
        }
        else
        {
            // 2. Individual term search fallback
            var terms = trimmed
                .Split(new[] { ' ', '\t', '\r', '\n', ',', '.', ':', ';', '(', ')', '[', ']', '{', '}', '"', '\'' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(t => t.Length >= 3)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (terms.Count > 0)
            {
                var termMatches = new Dictionary<Guid, (Memory Memory, int MatchCount)>();
                foreach (var term in terms)
                {
                    var termPattern = $"%{term}%";
                    var matches = await db.Memories.AsNoTracking()
                        .Where(memory => EF.Functions.Like(memory.Text, termPattern) || EF.Functions.Like(memory.Source, termPattern))
                        .ToListAsync(cancellationToken);
                    foreach (var match in matches)
                    {
                        if (termMatches.TryGetValue(match.Id, out var existing))
                        {
                            termMatches[match.Id] = (existing.Memory, existing.MatchCount + 1);
                        }
                        else
                        {
                            termMatches[match.Id] = (match, 1);
                        }
                    }
                }

                var sortedTermMatches = termMatches.Values
                    .OrderByDescending(x => x.MatchCount)
                    .ThenByDescending(x => x.Memory.Importance)
                    .ThenByDescending(x => x.Memory.CreatedAt)
                    .Select(x => x.Memory)
                    .ToList();

                results = exactResults.Concat(sortedTermMatches)
                    .GroupBy(m => m.Id)
                    .Select(g => g.First())
                    .Take(take)
                    .ToList();
            }
            else
            {
                results = exactResults;
            }
        }

        if (results.Count > 0)
        {
            var ids = results.Select(memory => memory.Id).ToArray();
            await db.Memories
                .Where(memory => ids.Contains(memory.Id))
                .ExecuteUpdateAsync(setters => setters.SetProperty(memory => memory.LastRetrievedAt, DateTimeOffset.UtcNow), cancellationToken);
        }

        return results;
    }

    public async Task<IReadOnlyList<Memory>> RecallAsync(string query, int take = 10, CancellationToken cancellationToken = default)
    {
        var trimmed = query.Trim();
        if (trimmed.Length == 0)
        {
            return await SearchMemoriesAsync(query, take, cancellationToken);
        }

        if (aiChatService is not null)
        {
            var profile = await settingsService.GetDefaultAiProviderProfileAsync(cancellationToken);
            var queryEmbedding = await aiChatService.GenerateEmbeddingAsync(profile, trimmed, cancellationToken);
            if (queryEmbedding is not null)
            {
                await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
                var memoriesWithEmbeddings = await db.Memories
                    .AsNoTracking()
                    .Where(m => m.EmbeddingJson != null)
                    .ToListAsync(cancellationToken);

                if (memoriesWithEmbeddings.Count > 0)
                {
                    var scored = memoriesWithEmbeddings
                        .Select(m =>
                        {
                            try
                            {
                                var vector = System.Text.Json.JsonSerializer.Deserialize<float[]>(m.EmbeddingJson!);
                                if (vector is not null)
                                {
                                    return new { Memory = m, Score = CalculateCosineSimilarity(queryEmbedding, vector) };
                                }
                            }
                            catch { }
                            return new { Memory = m, Score = 0.0 };
                        })
                        .Where(x => x.Score > 0.0)
                        .OrderByDescending(x => x.Score)
                        .ThenByDescending(x => x.Memory.Importance)
                        .Take(take)
                        .Select(x => x.Memory)
                        .ToList();

                    if (scored.Count > 0)
                    {
                        var ids = scored.Select(m => m.Id).ToArray();
                        await db.Memories
                            .Where(memory => ids.Contains(memory.Id))
                            .ExecuteUpdateAsync(setters => setters.SetProperty(memory => memory.LastRetrievedAt, DateTimeOffset.UtcNow), cancellationToken);

                        return scored;
                    }
                }
            }
        }

        return await SearchMemoriesAsync(query, take, cancellationToken);
    }

    private static double CalculateCosineSimilarity(float[] vector1, float[] vector2)
    {
        if (vector1.Length != vector2.Length)
        {
            return 0;
        }

        double dotProduct = 0;
        double magnitude1 = 0;
        double magnitude2 = 0;

        for (int i = 0; i < vector1.Length; i++)
        {
            dotProduct += vector1[i] * vector2[i];
            magnitude1 += vector1[i] * vector1[i];
            magnitude2 += vector2[i] * vector2[i];
        }

        if (magnitude1 == 0 || magnitude2 == 0)
        {
            return 0;
        }

        return dotProduct / (Math.Sqrt(magnitude1) * Math.Sqrt(magnitude2));
    }
}
