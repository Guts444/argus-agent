using Argus.Core.Models;
using Argus.Core.Services;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

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
            var embedding = await TryGenerateEmbeddingAsync(text, cancellationToken);
            if (embedding is not null)
            {
                embeddingJson = JsonSerializer.Serialize(embedding);
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
        var results = await RecallWithDetailsAsync(query, take, cancellationToken);
        return results.Select(result => result.Memory).ToList();
    }

    public async Task<IReadOnlyList<MemoryRecallResult>> RecallWithDetailsAsync(
        string query,
        int take = 10,
        CancellationToken cancellationToken = default)
    {
        take = Math.Clamp(take, 1, 100);
        var trimmed = query.Trim();
        if (trimmed.Length == 0)
        {
            await using var recentDb = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            var recentMemories = await recentDb.Memories
                .AsNoTracking()
                .OrderByDescending(memory => memory.Importance)
                .ThenByDescending(memory => memory.CreatedAt)
                .Take(take)
                .ToListAsync(cancellationToken);
            var recentResults = recentMemories
                .Select(memory => CreateRecentResult(memory))
                .ToList();
            await MarkRetrievedAsync(recentDb, recentResults.Select(result => result.Memory.Id), cancellationToken);
            return recentResults;
        }

        var terms = Tokenize(trimmed);
        var candidates = new Dictionary<Guid, RecallCandidate>();
        var candidateLimit = Math.Clamp(take * 10, 50, 250);
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        var exactPattern = $"%{EscapeLikePattern(trimmed)}%";
        var exactMatches = await db.Memories
            .AsNoTracking()
            .Where(memory =>
                EF.Functions.Like(memory.Text, exactPattern, "\\") ||
                EF.Functions.Like(memory.Source, exactPattern, "\\"))
            .OrderByDescending(memory => memory.Importance)
            .ThenByDescending(memory => memory.CreatedAt)
            .Take(candidateLimit)
            .ToListAsync(cancellationToken);
        foreach (var memory in exactMatches)
        {
            GetOrAddCandidate(candidates, memory).ExactPhraseMatch = true;
        }

        foreach (var term in terms)
        {
            var termPattern = $"%{EscapeLikePattern(term)}%";
            var termMatches = await db.Memories
                .AsNoTracking()
                .Where(memory =>
                    EF.Functions.Like(memory.Text, termPattern, "\\") ||
                    EF.Functions.Like(memory.Source, termPattern, "\\"))
                .OrderByDescending(memory => memory.Importance)
                .ThenByDescending(memory => memory.CreatedAt)
                .Take(candidateLimit)
                .ToListAsync(cancellationToken);
            foreach (var memory in termMatches)
            {
                GetOrAddCandidate(candidates, memory).MatchedTerms.Add(term);
            }
        }

        var queryEmbedding = await TryGenerateEmbeddingAsync(trimmed, cancellationToken);
        if (queryEmbedding is not null)
        {
            var memoriesWithEmbeddings = await db.Memories
                .AsNoTracking()
                .Where(memory => memory.EmbeddingJson != null)
                .ToListAsync(cancellationToken);

            foreach (var memory in memoriesWithEmbeddings)
            {
                try
                {
                    var vector = JsonSerializer.Deserialize<float[]>(memory.EmbeddingJson!);
                    if (vector is null)
                    {
                        continue;
                    }

                    var semanticScore = CalculateCosineSimilarity(queryEmbedding, vector);
                    if (semanticScore > 0)
                    {
                        GetOrAddCandidate(candidates, memory).SemanticScore = semanticScore;
                    }
                }
                catch (JsonException)
                {
                    // A malformed stored vector should not prevent lexical recall.
                }
            }
        }

        var now = DateTimeOffset.UtcNow;
        var results = candidates.Values
            .Select(candidate => CreateRecallResult(candidate, terms.Count, now))
            .Where(result => result.LexicalScore > 0 || result.SemanticScore > 0)
            .OrderByDescending(result => result.Score)
            .ThenByDescending(result => result.Memory.Importance)
            .ThenByDescending(result => result.Memory.CreatedAt)
            .Take(take)
            .ToList();

        await MarkRetrievedAsync(db, results.Select(result => result.Memory.Id), cancellationToken);
        return results;
    }

    public async Task<MemoryRecallFeedback> SaveRecallFeedbackAsync(
        string query,
        Guid memoryId,
        string rating,
        CancellationToken cancellationToken = default)
    {
        var normalizedRating = rating.Trim().ToLowerInvariant();
        if (normalizedRating is not ("useful" or "not_relevant"))
        {
            throw new ArgumentException("Memory recall rating must be useful or not_relevant.", nameof(rating));
        }

        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        if (!await db.Memories.AnyAsync(memory => memory.Id == memoryId, cancellationToken))
        {
            throw new InvalidOperationException("The recalled memory no longer exists.");
        }

        var feedback = new MemoryRecallFeedback
        {
            MemoryId = memoryId,
            Query = query.Trim(),
            Rating = normalizedRating,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.MemoryRecallFeedback.Add(feedback);
        await db.SaveChangesAsync(cancellationToken);
        return feedback;
    }

    private async Task<float[]?> TryGenerateEmbeddingAsync(
        string text,
        CancellationToken cancellationToken)
    {
        if (aiChatService is null)
        {
            return null;
        }

        try
        {
            var profile = await settingsService.GetDefaultAiProviderProfileAsync(cancellationToken);
            return await aiChatService.GenerateEmbeddingAsync(profile, text, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static RecallCandidate GetOrAddCandidate(
        IDictionary<Guid, RecallCandidate> candidates,
        Memory memory)
    {
        if (!candidates.TryGetValue(memory.Id, out var candidate))
        {
            candidate = new RecallCandidate(memory);
            candidates.Add(memory.Id, candidate);
        }

        return candidate;
    }

    private static MemoryRecallResult CreateRecentResult(Memory memory)
    {
        var importanceScore = NormalizeImportance(memory.Importance);
        var recencyScore = CalculateRecencyScore(memory.CreatedAt, DateTimeOffset.UtcNow);
        var score = 0.7 * importanceScore + 0.3 * recencyScore;
        return new MemoryRecallResult(
            memory,
            score,
            MemoryRecallMethod.Recent,
            0,
            0,
            importanceScore,
            recencyScore,
            $"Shown as a recent memory; importance {memory.Importance}/5; created {DescribeAge(memory.CreatedAt)}; source {memory.Source}.");
    }

    private static MemoryRecallResult CreateRecallResult(
        RecallCandidate candidate,
        int queryTermCount,
        DateTimeOffset now)
    {
        var lexicalScore = candidate.ExactPhraseMatch
            ? 1.0
            : queryTermCount == 0
                ? 0
                : 0.85 * candidate.MatchedTerms.Count / queryTermCount;
        var semanticScore = Math.Clamp(candidate.SemanticScore, 0, 1);
        var importanceScore = NormalizeImportance(candidate.Memory.Importance);
        var recencyScore = CalculateRecencyScore(candidate.Memory.CreatedAt, now);

        double score;
        MemoryRecallMethod method;
        if (semanticScore > 0 && lexicalScore > 0)
        {
            score = 0.6 * semanticScore + 0.25 * lexicalScore +
                0.1 * importanceScore + 0.05 * recencyScore;
            method = MemoryRecallMethod.Hybrid;
        }
        else if (semanticScore > 0)
        {
            score = 0.8 * semanticScore + 0.15 * importanceScore + 0.05 * recencyScore;
            method = MemoryRecallMethod.Semantic;
        }
        else
        {
            score = 0.75 * lexicalScore + 0.15 * importanceScore + 0.1 * recencyScore;
            method = candidate.ExactPhraseMatch
                ? MemoryRecallMethod.ExactPhrase
                : MemoryRecallMethod.Keyword;
        }

        var evidence = new List<string>();
        if (candidate.ExactPhraseMatch)
        {
            evidence.Add("exact phrase match");
        }
        else if (candidate.MatchedTerms.Count > 0)
        {
            evidence.Add($"matched {candidate.MatchedTerms.Count}/{queryTermCount} query terms");
        }

        if (semanticScore > 0)
        {
            evidence.Add($"semantic similarity {semanticScore:P0}");
        }

        evidence.Add($"importance {candidate.Memory.Importance}/5");
        evidence.Add($"created {DescribeAge(candidate.Memory.CreatedAt)}");
        evidence.Add($"source {candidate.Memory.Source}");

        return new MemoryRecallResult(
            candidate.Memory,
            Math.Clamp(score, 0, 1),
            method,
            semanticScore,
            lexicalScore,
            importanceScore,
            recencyScore,
            string.Join("; ", evidence) + ".");
    }

    private static IReadOnlyList<string> Tokenize(string query)
    {
        return query
            .Split(
                [' ', '\t', '\r', '\n', ',', '.', ':', ';', '(', ')', '[', ']', '{', '}', '"', '\''],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(term => term.Length >= 3)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToList();
    }

    private static string EscapeLikePattern(string value)
    {
        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
    }

    private static double NormalizeImportance(int importance)
    {
        return Math.Clamp(importance, 1, 5) / 5.0;
    }

    private static double CalculateRecencyScore(DateTimeOffset createdAt, DateTimeOffset now)
    {
        var ageDays = Math.Max(0, (now - createdAt).TotalDays);
        return 1.0 / (1.0 + ageDays / 90.0);
    }

    private static string DescribeAge(DateTimeOffset createdAt)
    {
        var age = DateTimeOffset.UtcNow - createdAt;
        if (age.TotalMinutes < 2)
        {
            return "just now";
        }

        if (age.TotalHours < 1)
        {
            return $"{Math.Floor(age.TotalMinutes)} minutes ago";
        }

        if (age.TotalDays < 1)
        {
            return $"{Math.Floor(age.TotalHours)} hours ago";
        }

        return $"{Math.Floor(age.TotalDays)} days ago";
    }

    private static async Task MarkRetrievedAsync(
        ArgusDbContext db,
        IEnumerable<Guid> memoryIds,
        CancellationToken cancellationToken)
    {
        var ids = memoryIds.Distinct().ToArray();
        if (ids.Length == 0)
        {
            return;
        }

        await db.Memories
            .Where(memory => ids.Contains(memory.Id))
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(memory => memory.LastRetrievedAt, DateTimeOffset.UtcNow),
                cancellationToken);
    }

    private sealed class RecallCandidate(Memory memory)
    {
        public Memory Memory { get; } = memory;
        public bool ExactPhraseMatch { get; set; }
        public HashSet<string> MatchedTerms { get; } = new(StringComparer.OrdinalIgnoreCase);
        public double SemanticScore { get; set; }
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
