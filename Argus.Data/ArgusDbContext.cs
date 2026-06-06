using Argus.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Argus.Data;

public sealed class ArgusDbContext(DbContextOptions<ArgusDbContext> options) : DbContext(options)
{
    public DbSet<Node> Nodes => Set<Node>();
    public DbSet<Edge> Edges => Set<Edge>();
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<NodeTag> NodeTags => Set<NodeTag>();
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<Memory> Memories => Set<Memory>();
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();
    public DbSet<AiProviderProfile> AiProviderProfiles => Set<AiProviderProfile>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var dateTimeOffsetConverter = new ValueConverter<DateTimeOffset, long>(
            value => value.ToUnixTimeMilliseconds(),
            value => DateTimeOffset.FromUnixTimeMilliseconds(value));
        var nullableDateTimeOffsetConverter = new ValueConverter<DateTimeOffset?, long?>(
            value => value.HasValue ? value.Value.ToUnixTimeMilliseconds() : null,
            value => value.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(value.Value) : null);

        modelBuilder.Entity<Node>(entity =>
        {
            entity.ToTable("Nodes");
            entity.HasKey(node => node.Id);
            entity.Property(node => node.Title).HasMaxLength(220).IsRequired();
            entity.Property(node => node.Type).HasMaxLength(60).IsRequired();
            entity.Property(node => node.Status).HasMaxLength(60).IsRequired();
            entity.Property(node => node.ColorKey).HasMaxLength(60).IsRequired();
            entity.Property(node => node.IconKey).HasMaxLength(60).IsRequired();
            entity.Property(node => node.CreatedAt).HasConversion(dateTimeOffsetConverter);
            entity.Property(node => node.UpdatedAt).HasConversion(dateTimeOffsetConverter);
            entity.Property(node => node.LastTouchedAt).HasConversion(dateTimeOffsetConverter);
            entity.HasIndex(node => node.Title);
            entity.HasIndex(node => node.Type);
            entity.HasIndex(node => node.LastTouchedAt);
        });

        modelBuilder.Entity<Edge>(entity =>
        {
            entity.ToTable("Edges");
            entity.HasKey(edge => edge.Id);
            entity.Property(edge => edge.RelationshipType).HasMaxLength(80).IsRequired();
            entity.Property(edge => edge.CreatedAt).HasConversion(dateTimeOffsetConverter);
            entity.Property(edge => edge.UpdatedAt).HasConversion(dateTimeOffsetConverter);
            entity.HasIndex(edge => new { edge.SourceNodeId, edge.TargetNodeId, edge.RelationshipType });
            entity.HasOne(edge => edge.SourceNode)
                .WithMany(node => node.OutgoingEdges)
                .HasForeignKey(edge => edge.SourceNodeId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(edge => edge.TargetNode)
                .WithMany(node => node.IncomingEdges)
                .HasForeignKey(edge => edge.TargetNodeId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Tag>(entity =>
        {
            entity.ToTable("Tags");
            entity.HasKey(tag => tag.Id);
            entity.Property(tag => tag.Name).HasMaxLength(80).IsRequired();
            entity.Property(tag => tag.ColorKey).HasMaxLength(60).IsRequired();
            entity.HasIndex(tag => tag.Name).IsUnique();
        });

        modelBuilder.Entity<NodeTag>(entity =>
        {
            entity.ToTable("NodeTags");
            entity.HasKey(nodeTag => new { nodeTag.NodeId, nodeTag.TagId });
            entity.HasOne(nodeTag => nodeTag.Node)
                .WithMany(node => node.NodeTags)
                .HasForeignKey(nodeTag => nodeTag.NodeId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(nodeTag => nodeTag.Tag)
                .WithMany(tag => tag.NodeTags)
                .HasForeignKey(nodeTag => nodeTag.TagId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Conversation>(entity =>
        {
            entity.ToTable("Conversations");
            entity.HasKey(conversation => conversation.Id);
            entity.Property(conversation => conversation.Title).HasMaxLength(220).IsRequired();
            entity.Property(conversation => conversation.CreatedAt).HasConversion(dateTimeOffsetConverter);
            entity.Property(conversation => conversation.UpdatedAt).HasConversion(dateTimeOffsetConverter);
            entity.HasIndex(conversation => conversation.UpdatedAt);
        });

        modelBuilder.Entity<Message>(entity =>
        {
            entity.ToTable("Messages");
            entity.HasKey(message => message.Id);
            entity.Property(message => message.Role).HasMaxLength(32).IsRequired();
            entity.Property(message => message.Content).IsRequired();
            entity.Property(message => message.CreatedAt).HasConversion(dateTimeOffsetConverter);
            entity.HasOne(message => message.Conversation)
                .WithMany(conversation => conversation.Messages)
                .HasForeignKey(message => message.ConversationId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(message => message.LinkedNode)
                .WithMany()
                .HasForeignKey(message => message.LinkedNodeId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<Memory>(entity =>
        {
            entity.ToTable("Memories");
            entity.HasKey(memory => memory.Id);
            entity.Property(memory => memory.Text).IsRequired();
            entity.Property(memory => memory.Source).HasMaxLength(120).IsRequired();
            entity.Property(memory => memory.CreatedAt).HasConversion(dateTimeOffsetConverter);
            entity.Property(memory => memory.LastRetrievedAt).HasConversion(nullableDateTimeOffsetConverter);
            entity.Property(memory => memory.EmbeddingJson).IsRequired(false);
            entity.HasIndex(memory => memory.CreatedAt);
            entity.HasOne(memory => memory.LinkedNode)
                .WithMany()
                .HasForeignKey(memory => memory.LinkedNodeId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<AppSetting>(entity =>
        {
            entity.ToTable("AppSettings");
            entity.HasKey(setting => setting.Key);
            entity.Property(setting => setting.Key).HasMaxLength(160);
            entity.Property(setting => setting.Value).IsRequired();
            entity.Property(setting => setting.UpdatedAt).HasConversion(dateTimeOffsetConverter);
        });

        modelBuilder.Entity<AiProviderProfile>(entity =>
        {
            entity.ToTable("AiProviderProfiles");
            entity.HasKey(profile => profile.Id);
            entity.Property(profile => profile.Name).HasMaxLength(120).IsRequired();
            entity.Property(profile => profile.ProviderType).HasMaxLength(80).IsRequired();
            entity.Property(profile => profile.BaseUrl).HasMaxLength(600).IsRequired();
            entity.Property(profile => profile.Model).HasMaxLength(180).IsRequired();
            entity.Property(profile => profile.ApiKeyStorageKey).HasMaxLength(180).IsRequired();
            entity.Property(profile => profile.ThinkingMode).HasMaxLength(32).IsRequired();
            entity.Property(profile => profile.ReasoningEffort).HasMaxLength(32).IsRequired();
            entity.Property(profile => profile.OrganizationId).HasMaxLength(180).IsRequired();
            entity.Property(profile => profile.ProjectId).HasMaxLength(180).IsRequired();
            entity.HasIndex(profile => profile.Name).IsUnique();
        });
    }
}
