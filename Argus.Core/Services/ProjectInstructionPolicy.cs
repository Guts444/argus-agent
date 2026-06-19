using System.Globalization;
using System.Text.RegularExpressions;

namespace Argus.Core.Services;

public static partial class ProjectInstructionPolicy
{
    public const int MaxLength = 4000;

    public static string Normalize(string? content)
    {
        var normalized = (content ?? string.Empty)
            .ReplaceLineEndings("\n")
            .Trim();
        if (normalized.Length > MaxLength)
        {
            throw new ArgumentException(
                $"Project instructions cannot exceed {MaxLength} characters.",
                nameof(content));
        }

        if (normalized.Any(IsUnsafeCharacter))
        {
            throw new ArgumentException(
                "Project instructions contain unsupported control or formatting characters.",
                nameof(content));
        }

        return normalized;
    }

    public static string RedactForOutbound(string? content)
    {
        var redacted = Normalize(content);
        redacted = AuthorizationRegex().Replace(redacted, "$1[redacted]");
        redacted = SecretAssignmentRegex().Replace(redacted, "$1[redacted]");
        redacted = SecretJsonFieldRegex().Replace(redacted, "$1\"[redacted]\"");
        redacted = JwtRegex().Replace(redacted, "[redacted-token]");
        redacted = WindowsPathRegex().Replace(redacted, "[local-path]");
        redacted = UserProfileRegex().Replace(redacted, "[local-path]");
        return redacted;
    }

    private static bool IsUnsafeCharacter(char value)
    {
        if (value is '\n' or '\t')
        {
            return false;
        }

        var category = char.GetUnicodeCategory(value);
        return category is UnicodeCategory.Control or UnicodeCategory.Format;
    }

    [GeneratedRegex(
        @"(?i)\b(authorization\s*[:=]\s*)(?:bearer\s+)?[^\r\n,;]+",
        RegexOptions.CultureInvariant)]
    private static partial Regex AuthorizationRegex();

    [GeneratedRegex(
        @"(?i)\b((?:api[-_ ]?key|access[-_ ]?token|refresh[-_ ]?token|bot[-_ ]?token|token|secret|password|credential|private[-_ ]?key|connection[-_ ]?string)\s*[:=]\s*)(?:""[^""]*""|'[^']*'|[^\s,;]+)",
        RegexOptions.CultureInvariant)]
    private static partial Regex SecretAssignmentRegex();

    [GeneratedRegex(
        @"(?is)(""[^""]*(?:api[-_ ]?key|access[-_ ]?token|refresh[-_ ]?token|token|secret|password|credential|private[-_ ]?key|connection[-_ ]?string)[^""]*""\s*:\s*)""(?:\\.|[^""])*""",
        RegexOptions.CultureInvariant)]
    private static partial Regex SecretJsonFieldRegex();

    [GeneratedRegex(
        @"\beyJ[A-Za-z0-9_-]{16,}\.[A-Za-z0-9_-]{8,}(?:\.[A-Za-z0-9_-]{8,})?\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex JwtRegex();

    [GeneratedRegex(
        @"(?i)(?<![A-Za-z0-9])(?:[A-Z]:\\|\\\\)[^\r\n\t""']+",
        RegexOptions.CultureInvariant)]
    private static partial Regex WindowsPathRegex();

    [GeneratedRegex(
        @"(?i)(?:%USERPROFILE%|%LOCALAPPDATA%|%APPDATA%)(?:\\[^\r\n\t""']*)?",
        RegexOptions.CultureInvariant)]
    private static partial Regex UserProfileRegex();
}
