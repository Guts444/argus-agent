using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace Argus.App.Services;

public sealed record AppUpdateInfo(
    Version Version,
    string DisplayVersion,
    string Name,
    string Notes,
    Uri ReleasePageUri,
    Uri InstallerUri,
    string? InstallerSha256);

public interface IAppUpdateService
{
    Task<AppUpdateInfo?> GetLatestReleaseAsync(CancellationToken cancellationToken = default);
    Task DownloadAndLaunchInstallerAsync(AppUpdateInfo update, CancellationToken cancellationToken = default);
}

public sealed class GitHubUpdateService(HttpClient httpClient) : IAppUpdateService
{
    private static readonly Uri LatestReleaseUri =
        new("https://api.github.com/repos/Guts444/argus-agent/releases/latest");

    public async Task<AppUpdateInfo?> GetLatestReleaseAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseUri);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Argus", GetCurrentVersion()));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;
        var tag = root.GetProperty("tag_name").GetString()?.Trim().TrimStart('v');
        if (!Version.TryParse(tag, out var version))
        {
            return null;
        }

        Uri? installerUri = null;
        string? installerSha256 = null;
        if (root.TryGetProperty("assets", out var assets))
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString();
                if (name?.StartsWith("ArgusAgentSetup-x64", StringComparison.OrdinalIgnoreCase) != true ||
                    !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var downloadUrl = asset.GetProperty("browser_download_url").GetString();
                if (Uri.TryCreate(downloadUrl, UriKind.Absolute, out installerUri))
                {
                    var digest = asset.TryGetProperty("digest", out var digestProperty)
                        ? digestProperty.GetString()
                        : null;
                    if (digest?.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        installerSha256 = digest["sha256:".Length..].Trim();
                    }

                    break;
                }
            }
        }

        var releaseUrl = root.GetProperty("html_url").GetString();
        if (installerUri is null || !Uri.TryCreate(releaseUrl, UriKind.Absolute, out var releasePageUri))
        {
            return null;
        }

        return new AppUpdateInfo(
            version,
            $"v{version}",
            root.GetProperty("name").GetString() ?? $"Argus v{version}",
            root.GetProperty("body").GetString() ?? "See the release page for details.",
            releasePageUri,
            installerUri,
            installerSha256);
    }

    public async Task DownloadAndLaunchInstallerAsync(
        AppUpdateInfo update,
        CancellationToken cancellationToken = default)
    {
        if (!IsValidSha256(update.InstallerSha256))
        {
            throw new InvalidDataException(
                "The release does not provide a valid SHA-256 digest for its installer.");
        }

        var updateDirectory = Path.Combine(Path.GetTempPath(), "Argus", "updates");
        Directory.CreateDirectory(updateDirectory);
        var installerPath = Path.Combine(updateDirectory, $"ArgusAgentSetup-x64-{update.Version}.exe");

        using var response = await httpClient.GetAsync(
            update.InstallerUri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var destination = File.Create(installerPath))
        {
            await source.CopyToAsync(destination, cancellationToken);
        }

        await using var installerStream = File.OpenRead(installerPath);
        var actualHash = Convert.ToHexString(
            await SHA256.HashDataAsync(installerStream, cancellationToken));
        if (!actualHash.Equals(update.InstallerSha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(installerPath);
            throw new InvalidDataException("The downloaded installer failed SHA-256 verification.");
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = installerPath,
            Arguments = "/CLOSEAPPLICATIONS /RESTARTAPPLICATIONS",
            UseShellExecute = true
        });
    }

    private static string GetCurrentVersion()
    {
        return typeof(GitHubUpdateService).Assembly.GetName().Version?.ToString(3) ?? "0.3.3";
    }

    private static bool IsValidSha256(string? value)
    {
        return value is { Length: 64 } && value.All(Uri.IsHexDigit);
    }
}
