using Argus.AI.Services;
using Argus.Core.Models;
using Argus.Core.Services;

namespace Argus.Tests;

public sealed class ProjectContextServiceTests
{
    [Fact]
    public async Task SnapshotIsReusedUntilAnExplicitRefresh()
    {
        var root = CreateProjectRoot("Alpha");
        var service = new ProjectContextService(
            new ProjectSettingsService(root));

        var first = await service.GetSnapshotAsync();
        Directory.CreateDirectory(Path.Combine(root, "Beta"));
        var cached = await service.GetSnapshotAsync();
        var refreshed = await service.RefreshSnapshotAsync();

        Assert.Same(first, cached);
        Assert.Single(cached.Projects);
        Assert.Equal(2, refreshed.Projects.Count);
        Assert.Same(refreshed, service.CurrentSnapshot);
    }

    [Fact]
    public async Task ConcurrentRefreshCallsShareOneSnapshot()
    {
        var root = CreateProjectRoot("Alpha");
        var service = new ProjectContextService(
            new ProjectSettingsService(root));

        var firstRefresh = service.RefreshSnapshotAsync();
        var secondRefresh = service.RefreshSnapshotAsync();
        var snapshots = await Task.WhenAll(firstRefresh, secondRefresh);

        Assert.Same(snapshots[0], snapshots[1]);
        Assert.False(service.IsRefreshing);
    }

    [Fact]
    public async Task RefreshHonorsCancellation()
    {
        var root = CreateProjectRoot("Alpha");
        var service = new ProjectContextService(
            new ProjectSettingsService(root));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.RefreshSnapshotAsync(cancellation.Token));
    }

    [Fact]
    public async Task EmptyRootCanBeRefreshedMoreThanOnce()
    {
        var service = new ProjectContextService(
            new ProjectSettingsService(string.Empty));

        var first = await service.RefreshSnapshotAsync();
        var second = await service.RefreshSnapshotAsync();

        Assert.NotSame(first, second);
        Assert.Empty(second.Projects);
        Assert.False(service.IsRefreshing);
    }

    private static string CreateProjectRoot(params string[] projects)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "ArgusTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        foreach (var project in projects)
        {
            var projectDirectory = Path.Combine(root, project);
            Directory.CreateDirectory(projectDirectory);
            File.WriteAllText(
                Path.Combine(projectDirectory, "README.md"),
                $"# {project}{Environment.NewLine}Local project.");
        }

        return root;
    }

    private sealed class ProjectSettingsService(string projectsRoot) : ISettingsService
    {
        public Task<IReadOnlyList<AiProviderProfile>> GetAiProviderProfilesAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AiProviderProfile>>([]);

        public Task<AiProviderProfile?> GetDefaultAiProviderProfileAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<AiProviderProfile?>(null);

        public Task<AiProviderProfile> SaveAiProviderProfileAsync(
            AiProviderProfile profile,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(profile);

        public Task<string?> GetSettingAsync(
            string key,
            string? defaultValue = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(
                key == "ProjectsRootPath" ? projectsRoot : defaultValue);

        public Task SaveSettingAsync(
            string key,
            string value,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
