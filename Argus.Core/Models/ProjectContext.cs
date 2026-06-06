namespace Argus.Core.Models;

public sealed record ProjectContext(
    string Name,
    string Path,
    string? ReadmePath,
    string ReadmePreview,
    string StateSummary,
    string? GitBranch,
    string? GitRemote,
    bool HasUncommittedChanges);
