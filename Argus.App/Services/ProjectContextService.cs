using System.Diagnostics;
using Argus.Core.Models;
using Argus.Core.Services;

namespace Argus.App.Services;

public sealed class ProjectContextService(ISettingsService settingsService) : IProjectContextService
{
    private static readonly HashSet<string> ExcludedDirectoryNames = new(
        [
            ".git",
            ".vs",
            ".idea",
            "bin",
            "obj",
            "node_modules",
            "packages",
            "artifacts",
            "TestResults"
        ],
        StringComparer.OrdinalIgnoreCase);

    private IReadOnlyList<ProjectContext>? cachedContexts;

    public async Task<IReadOnlyList<ProjectContext>> ScanProjectsAsync(CancellationToken cancellationToken = default)
    {
        var configuredPath = await settingsService.GetSettingAsync("ProjectsRootPath", "", cancellationToken);
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return Array.Empty<ProjectContext>();
        }

        string projectsRoot;
        try
        {
            projectsRoot = Path.GetFullPath(configuredPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Array.Empty<ProjectContext>();
        }

        if (!Directory.Exists(projectsRoot))
        {
            return Array.Empty<ProjectContext>();
        }

        List<string> directories;
        try
        {
            directories = Directory.EnumerateDirectories(projectsRoot)
                .Where(path => !ExcludedDirectoryNames.Contains(Path.GetFileName(path)))
                .Where(path => !Path.GetFileName(path).StartsWith('.'))
                .Where(path => !File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
                .OrderBy(Path.GetFileName)
                .Take(100)
                .ToList();
        }
        catch (UnauthorizedAccessException)
        {
            return Array.Empty<ProjectContext>();
        }
        catch (IOException)
        {
            return Array.Empty<ProjectContext>();
        }

        var contexts = new List<ProjectContext>();
        foreach (var directory in directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                contexts.Add(await BuildContextAsync(directory, cancellationToken));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                // Skip unreadable project folders without aborting the entire opt-in scan.
            }
        }

        cachedContexts = contexts;
        return contexts;
    }

    public async Task<ProjectContext?> GetProjectContextAsync(string nodeTitle, CancellationToken cancellationToken = default)
    {
        var contexts = cachedContexts ?? await ScanProjectsAsync(cancellationToken);
        var normalizedTitle = Normalize(nodeTitle);
        return contexts.FirstOrDefault(context => Normalize(context.Name) == normalizedTitle)
            ?? contexts.FirstOrDefault(context => Normalize(ExtractReadmeTitle(context.ReadmePreview)) == normalizedTitle)
            ?? contexts.FirstOrDefault(context => Normalize(context.Name).Contains(normalizedTitle) || normalizedTitle.Contains(Normalize(context.Name)));
    }

    private static async Task<ProjectContext> BuildContextAsync(string directory, CancellationToken cancellationToken)
    {
        var name = Path.GetFileName(directory);
        var readmePath = FindReadme(directory);
        var readmePreview = readmePath is null
            ? "No README found."
            : new FileInfo(readmePath).Length > 1_000_000
                ? "README omitted because it is larger than 1 MB."
                : ProjectContextPrivacy.RedactReadme(
                    SummarizeReadme(await File.ReadAllTextAsync(readmePath, cancellationToken)));

        var branch = await RunGitAsync(directory, cancellationToken, "branch", "--show-current");
        var remote = ProjectContextPrivacy.SanitizeRemote(
            await RunGitAsync(directory, cancellationToken, "remote", "get-url", "origin"));
        var rawStatus = await RunGitAsync(
            directory,
            cancellationToken,
            "status",
            "--short",
            "--untracked-files=normal");
        var hasChanges = !string.IsNullOrWhiteSpace(rawStatus);
        var status = ProjectContextPrivacy.SanitizeGitStatus(rawStatus);
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
            .Where(line =>
                line.Length > 0 &&
                !line.StartsWith("![", StringComparison.Ordinal) &&
                !line.StartsWith("[![", StringComparison.Ordinal) &&
                !line.StartsWith('<'))
            .Take(16);

        var preview = string.Join(Environment.NewLine, lines);
        return preview.Length <= 1600 ? preview : preview[..1600] + "...";
    }

    private static string BuildStateSummary(string? readmePath, string branch, string? remote, bool hasChanges, string status)
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

    private static async Task<string> RunGitAsync(
        string directory,
        CancellationToken cancellationToken,
        params string[] arguments)
    {
        using var process = new Process();
        try
        {
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            process.StartInfo.ArgumentList.Add("-C");
            process.StartInfo.ArgumentList.Add(directory);
            foreach (var argument in arguments)
            {
                process.StartInfo.ArgumentList.Add(argument);
            }

            process.Start();
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(3), cancellationToken);
            return process.ExitCode == 0 ? (await outputTask).Trim() : string.Empty;
        }
        catch (TimeoutException)
        {
            TryKill(process);
            return string.Empty;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw;
        }
        catch
        {
            TryKill(process);
            return string.Empty;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    }

    private static string Normalize(string value)
    {
        return new string(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    }

    private static string ExtractReadmeTitle(string readmePreview)
    {
        var heading = readmePreview
            .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.StartsWith("# ", StringComparison.Ordinal));
        return heading is null ? string.Empty : heading[2..].Trim();
    }
}
