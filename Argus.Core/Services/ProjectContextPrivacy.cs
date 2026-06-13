using Argus.Core.Models;

namespace Argus.Core.Services;

public static class ProjectContextPrivacy
{
    private static readonly string[] SensitiveFileMarkers =
    [
        ".env",
        "credentials",
        "secrets.json",
        "secret.",
        "id_rsa",
        "id_ed25519",
        ".pfx",
        ".p12",
        ".pem",
        "appsettings.production",
        "appsettings.development"
    ];

    private static readonly string[] SensitiveValueMarkers =
    [
        "api_key",
        "api-key",
        "apikey",
        "access_token",
        "auth_token",
        "client_secret",
        "password",
        "private_key",
        "connectionstring",
        "connection_string"
    ];

    public static string BuildOutboundPreview(ProjectContext context)
    {
        return
            $$"""
            Project: {{context.Name}}

            State:
            {{SanitizeStateSummary(context.StateSummary)}}

            README:
            {{RedactReadme(context.ReadmePreview)}}
            """;
    }

    public static string? SanitizeRemote(string? remote)
    {
        if (string.IsNullOrWhiteSpace(remote))
        {
            return remote;
        }

        var trimmed = remote.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            if (uri.IsFile)
            {
                return "local remote configured";
            }

            var builder = new UriBuilder(uri)
            {
                UserName = string.Empty,
                Password = string.Empty,
                Query = string.Empty,
                Fragment = string.Empty
            };
            return builder.Uri.ToString().TrimEnd('/');
        }

        return Path.IsPathRooted(trimmed)
            ? "local remote configured"
            : trimmed;
    }

    public static string SanitizeGitStatus(string status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return string.Empty;
        }

        return string.Join(
            Environment.NewLine,
            status
                .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries)
                .Select(line => ContainsSensitiveFileName(line)
                    ? $"{GetStatusPrefix(line)}[sensitive file omitted]"
                    : line));
    }

    public static string SanitizeStateSummary(string stateSummary)
    {
        return string.Join(
            Environment.NewLine,
            stateSummary
                .Split(["\r\n", "\n"], StringSplitOptions.None)
                .Select(line =>
                {
                    if (line.StartsWith("GitHub remote:", StringComparison.OrdinalIgnoreCase) ||
                        line.StartsWith("Git remote:", StringComparison.OrdinalIgnoreCase))
                    {
                        var separator = line.IndexOf(':');
                        var label = line[..(separator + 1)];
                        var value = line[(separator + 1)..].Trim();
                        return $"{label} {SanitizeRemote(value)}";
                    }

                    return ContainsSensitiveFileName(line)
                        ? "Changed files: [sensitive file omitted]"
                        : line;
                }));
    }

    public static string RedactReadme(string readme)
    {
        return string.Join(
            Environment.NewLine,
            readme
                .Split(["\r\n", "\n"], StringSplitOptions.None)
                .Select(line => ContainsSensitiveValue(line)
                    ? "[sensitive value omitted]"
                    : line));
    }

    public static bool ContainsSensitiveFileName(string value)
    {
        return SensitiveFileMarkers.Any(marker =>
            value.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private static bool ContainsSensitiveValue(string line)
    {
        if (line.Contains("BEGIN PRIVATE KEY", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var separator = line.IndexOfAny(['=', ':']);
        if (separator <= 0)
        {
            return false;
        }

        var key = line[..separator];
        return SensitiveValueMarkers.Any(marker =>
            key.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private static string GetStatusPrefix(string line)
    {
        return line.Length >= 3 ? line[..3] : string.Empty;
    }
}
