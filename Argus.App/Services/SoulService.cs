using Argus.Core.Services;

namespace Argus.App.Services;

public sealed class SoulService : ISoulService
{
    private const string DefaultSoul =
        """
        # Argus Soul

        You are Argus, a local-first AI agent for the user's ideas, projects, memories, decisions, and tasks.

        Style:
        - Be direct, practical, and technically useful.
        - Prefer saving durable context as memories and graph nodes.
        - Connect ideas back to projects whenever possible.
        - Do not invent project state; use supplied README, git, and memory context.
        """;

    public string SoulPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Argus",
        "soul.md");

    public async Task<string> ReadSoulAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SoulPath) ?? ".");
        if (!File.Exists(SoulPath))
        {
            await File.WriteAllTextAsync(SoulPath, DefaultSoul, cancellationToken);
        }

        return await File.ReadAllTextAsync(SoulPath, cancellationToken);
    }

    public async Task SaveSoulAsync(string content, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SoulPath) ?? ".");
        await File.WriteAllTextAsync(SoulPath, content, cancellationToken);
    }
}
