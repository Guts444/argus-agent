using Argus.Core.Models;
using Argus.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Argus.Data.Services;

public sealed class ConversationService(IDbContextFactory<ArgusDbContext> dbContextFactory) : IConversationService
{
    public async Task<IReadOnlyList<Conversation>> GetConversationsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Conversations
            .AsNoTracking()
            .Include(conversation => conversation.Messages.OrderBy(message => message.CreatedAt))
            .OrderByDescending(conversation => conversation.UpdatedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<Conversation> CreateConversationAsync(string title, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var conversation = new Conversation
        {
            Title = string.IsNullOrWhiteSpace(title) ? "New conversation" : title.Trim(),
            CreatedAt = now,
            UpdatedAt = now
        };
        db.Conversations.Add(conversation);
        await db.SaveChangesAsync(cancellationToken);
        return conversation;
    }

    public async Task<Message> AddMessageAsync(Guid conversationId, string role, string content, Guid? linkedNodeId = null, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var message = new Message
        {
            ConversationId = conversationId,
            Role = role,
            Content = content,
            CreatedAt = DateTimeOffset.UtcNow,
            LinkedNodeId = linkedNodeId
        };
        db.Messages.Add(message);

        var conversation = await db.Conversations.FirstAsync(existing => existing.Id == conversationId, cancellationToken);
        conversation.UpdatedAt = message.CreatedAt;
        if (conversation.Title == "New conversation" && role == "user")
        {
            conversation.Title = content.Length > 80 ? content[..80] : content;
        }

        await db.SaveChangesAsync(cancellationToken);
        return message;
    }

    public async Task<IReadOnlyList<Message>> GetMessagesAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Messages
            .AsNoTracking()
            .Where(message => message.ConversationId == conversationId)
            .OrderBy(message => message.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task DeleteMessageAsync(Guid messageId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var message = await db.Messages.FirstOrDefaultAsync(m => m.Id == messageId, cancellationToken);
        if (message is not null)
        {
            db.Messages.Remove(message);
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task<IReadOnlyList<MessageSearchResult>> SearchMessagesAsync(string query, int take = 20, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Array.Empty<MessageSearchResult>();
        }

        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync(cancellationToken);

        // FTS5 query: sanitize input, escape special chars for MATCH
        var sanitized = query.Replace("\"", "\"\"");
        var ftsQuery = string.Join(" AND ", sanitized.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(term => $"\"{term}\""));

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                ms.MessageId,
                m.ConversationId,
                c.Title AS ConversationTitle,
                ms.Role,
                m.Content,
                snippet(MessageSearch, 2, '<mark>', '</mark>', '…', 40) AS Snippet,
                m.CreatedAt
            FROM MessageSearch ms
            JOIN Messages m ON m.Id = ms.MessageId
            JOIN Conversations c ON c.Id = m.ConversationId
            WHERE MessageSearch MATCH @query
            ORDER BY rank
            LIMIT @take
            """;

        var queryParam = command.CreateParameter();
        queryParam.ParameterName = "@query";
        queryParam.Value = ftsQuery;
        command.Parameters.Add(queryParam);

        var takeParam = command.CreateParameter();
        takeParam.ParameterName = "@take";
        takeParam.Value = take;
        command.Parameters.Add(takeParam);

        var results = new List<MessageSearchResult>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new MessageSearchResult(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                new DateTimeOffset(reader.GetInt64(6), TimeSpan.Zero)));
        }

        return results;
    }
}
