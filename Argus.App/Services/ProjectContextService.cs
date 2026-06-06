using System.Diagnostics;
using Argus.Core.Models;
using Argus.Core.Services;

namespace Argus.App.Services;

public sealed class ProjectContextService(ISettingsService settingsService) : IProjectContextService
{
    private IReadOnlyList<ProjectContext>? cachedContexts;

    public async Task<IReadOnlyList<ProjectContext>> ScanProjectsAsync(CancellationToken cancellationToken = default)
    {
        var configuredPath = await settingsService.GetSettingAsync("ProjectsRootPath", "", cancellationToken);
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return Array.Empty<ProjectContext>();
        }

        var projectsRoot = Path.GetFullPath(configuredPath);

        if (!Directory.Exists(projectsRoot))
        {
            return Array.Empty<ProjectContext>();
        }

        var directories = Directory.GetDirectories(projectsRoot)
            .Where(path => !Path.GetFileName(path).StartsWith('.'))
            .OrderBy(Path.GetFileName)
            .Take(100)
            .ToList();

        var contexts = new List<ProjectContext>();
        foreach (var directory in directories)
        {
            contexts.Add(await BuildContextAsync(directory, cancellationToken));
        }

        cachedContexts = contexts;
        return contexts;
    }

    public async Task<ProjectContext?> GetProjectContextAsync(string nodeTitle, CancellationToken cancellationToken = default)
    {
        var contexts = cachedContexts ?? await ScanProjectsAsync(cancellationToken);
        var normalizedTitle = Normalize(nodeTitle);
        return contexts.FirstOrDefault(context => Normalize(context.Name) == normalizedTitle)
            ?? contexts.FirstOrDefault(context => Normalize(context.Name).Contains(normalizedTitle) || normalizedTitle.Contains(Normalize(context.Name)));
    }

    private static async Task<ProjectContext> BuildContextAsync(string directory, CancellationToken cancellationToken)
    {
        var name = Path.GetFileName(directory);
        var readmePath = FindReadme(directory);
        var readmePreview = readmePath is null
            ? "No README found."
            : SummarizeReadme(await File.ReadAllTextAsync(readmePath, cancellationToken));

        var branch = await RunGitAsync(directory, "branch --show-current", cancellationToken);
        var remote = await RunGitAsync(directory, "remote get-url origin", cancellationToken);
        var status = await RunGitAsync(directory, "status --short", cancellationToken);
        var hasChanges = !string.IsNullOrWhiteSpace(status);
        var state = BuildStateSummary(readmePath, branch, remote, hasChanges, status);

        return new ProjectContext(
            name,
            directory,
            readmePath,
            readmePreview,
            state,
            string.IsNullOrWhiteSpace(branch) ? null : branch,
            string.IsNullOrWhiteSpace(remote) ? null : remote,
            hasChanges);
    }

    private static string? FindReadme(string directory)
    {
        return Directory.EnumerateFiles(directory, "README*", SearchOption.TopDirectoryOnly)
            .Concat(Directory.EnumerateFiles(directory, "readme*", SearchOption.TopDirectoryOnly))
            .FirstOrDefault();
    }

    private static string SummarizeReadme(string readme)
    {
        var lines = readme
            .Split(["\r\n", "\n"], StringSplitOptions.None)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith("![", StringComparison.Ordinal))
            .Take(16);

        var preview = string.Join(Environment.NewLine, lines);
        return preview.Length <= 1600 ? preview : preview[..1600] + "...";
    }

    private static string BuildStateSummary(string? readmePath, string branch, string remote, bool hasChanges, string status)
    {
        var parts = new List<string>
        {
            readmePath is null ? "README: missing" : $"README: {Path.GetFileName(readmePath)}",
            string.IsNullOrWhiteSpace(branch) ? "Git branch: unavailable" : $"Git branch: {branch.Trim()}",
            string.IsNullOrWhiteSpace(remote) ? "GitHub remote: unavailable" : $"GitHub remote: {remote.Trim()}",
            hasChanges ? "Working tree: has local changes" : "Working tree: clean or unavailable"
        };

        if (hasChanges)
        {
            var changed = status.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries).Take(8);
            parts.Add("Changed files: " + string.Join(", ", changed));
        }

        return string.Join(Environment.NewLine, parts);
    }

    private static async Task<string> RunGitAsync(string directory, string arguments, CancellationToken cancellationToken)
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = $"-C \"{directory}\" {arguments}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            process.Start();
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(3), cancellationToken);
            return process.ExitCode == 0 ? (await outputTask).Trim() : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string Normalize(string value)
    {
        return new string(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    }
}
