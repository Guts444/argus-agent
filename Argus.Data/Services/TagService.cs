using Argus.Core.Models;
using Argus.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Argus.Data.Services;

public sealed class TagService(IDbContextFactory<ArgusDbContext> dbContextFactory) : ITagService
{
    public async Task<IReadOnlyList<Tag>> GetTagsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Tags
            .AsNoTracking()
            .OrderBy(tag => tag.Name)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Tag>> GetNodeTagsAsync(Guid nodeId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await db.NodeTags
            .AsNoTracking()
            .Where(nodeTag => nodeTag.NodeId == nodeId)
            .Join(db.Tags.AsNoTracking(), nodeTag => nodeTag.TagId, tag => tag.Id, (_, tag) => tag)
            .OrderBy(tag => tag.Name)
            .ToListAsync(cancellationToken);
    }

    public async Task<Tag> UpsertTagAsync(string name, string colorKey = "cyan", CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var tag = await UpsertTagAsync(db, name, colorKey, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return tag;
    }

    public async Task<IReadOnlyList<Tag>> SetNodeTagsAsync(Guid nodeId, IEnumerable<string> tagNames, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var nodeExists = await db.Nodes.AnyAsync(node => node.Id == nodeId, cancellationToken);
        if (!nodeExists)
        {
            throw new InvalidOperationException("Cannot tag a node that does not exist.");
        }

        var normalizedNames = tagNames
            .Select(NormalizeName)
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var tags = new List<Tag>();
        foreach (var name in normalizedNames)
        {
            tags.Add(await UpsertTagAsync(db, name, "cyan", cancellationToken));
        }

        var targetTagIds = tags.Select(tag => tag.Id).ToHashSet();
        var existingAssignments = await db.NodeTags
            .Where(nodeTag => nodeTag.NodeId == nodeId)
            .ToListAsync(cancellationToken);
        db.NodeTags.RemoveRange(existingAssignments.Where(nodeTag => !targetTagIds.Contains(nodeTag.TagId)));

        foreach (var tag in tags)
        {
            if (existingAssignments.All(nodeTag => nodeTag.TagId != tag.Id))
            {
                db.NodeTags.Add(new NodeTag { NodeId = nodeId, TagId = tag.Id });
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return tags.OrderBy(tag => tag.Name).ToList();
    }

    public async Task AddTagToNodeAsync(Guid nodeId, string tagName, string colorKey = "cyan", CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var nodeExists = await db.Nodes.AnyAsync(node => node.Id == nodeId, cancellationToken);
        if (!nodeExists)
        {
            throw new InvalidOperationException("Cannot tag a node that does not exist.");
        }

        var tag = await UpsertTagAsync(db, tagName, colorKey, cancellationToken);
        var exists = await db.NodeTags.AnyAsync(nodeTag => nodeTag.NodeId == nodeId && nodeTag.TagId == tag.Id, cancellationToken);
        if (!exists)
        {
            db.NodeTags.Add(new NodeTag { NodeId = nodeId, TagId = tag.Id });
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task RemoveTagFromNodeAsync(Guid nodeId, string tagName, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var normalized = NormalizeName(tagName);
        var assignment = await db.NodeTags
            .Join(db.Tags, nodeTag => nodeTag.TagId, tag => tag.Id, (nodeTag, tag) => new { nodeTag, tag })
            .Where(item => item.nodeTag.NodeId == nodeId && item.tag.Name.ToLower() == normalized.ToLower())
            .Select(item => item.nodeTag)
            .FirstOrDefaultAsync(cancellationToken);

        if (assignment is not null)
        {
            db.NodeTags.Remove(assignment);
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task DeleteTagAsync(Guid tagId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var tag = await db.Tags.FirstOrDefaultAsync(existing => existing.Id == tagId, cancellationToken);
        if (tag is null)
        {
            return;
        }

        db.Tags.Remove(tag);
        await db.SaveChangesAsync(cancellationToken);
    }

    internal static async Task<Tag> UpsertTagAsync(ArgusDbContext db, string name, string colorKey, CancellationToken cancellationToken)
    {
        var normalized = NormalizeName(name);
        if (normalized.Length == 0)
        {
            throw new ArgumentException("Tag name cannot be empty.", nameof(name));
        }

        var lowerName = normalized.ToLower();
        var tag = await db.Tags.FirstOrDefaultAsync(existing => existing.Name.ToLower() == lowerName, cancellationToken);
        if (tag is not null)
        {
            tag.ColorKey = NormalizeColor(colorKey);
            return tag;
        }

        tag = new Tag
        {
            Id = Guid.NewGuid(),
            Name = normalized,
            ColorKey = NormalizeColor(colorKey)
        };
        db.Tags.Add(tag);
        return tag;
    }

    private static string NormalizeName(string name)
    {
        return name.Trim();
    }

    private static string NormalizeColor(string colorKey)
    {
        return string.IsNullOrWhiteSpace(colorKey) ? "cyan" : colorKey.Trim();
    }
}
