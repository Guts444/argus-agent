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
}
