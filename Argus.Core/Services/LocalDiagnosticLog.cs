using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Argus.Core.Services;

public enum DiagnosticSeverity
{
    Debug,
    Information,
    Warning,
    Error,
    Critical
}

public interface IDiagnosticLog
{
    string DiagnosticsDirectory { get; }
    void Write(
        DiagnosticSeverity severity,
        string component,
        string eventName,
        string? detail = null,
        Exception? exception = null);
    IDisposable BeginOperation(string component, string operation);
}

public sealed partial class LocalDiagnosticLog : IDiagnosticLog
{
    private const long DefaultMaxFileBytes = 1_048_576;
    private const int DefaultRetainedFiles = 5;
    private readonly object syncRoot = new();
    private readonly long maxFileBytes;
    private readonly int retainedFiles;
    private readonly string activeLogPath;

    public LocalDiagnosticLog(
        string diagnosticsDirectory,
        long maxFileBytes = DefaultMaxFileBytes,
        int retainedFiles = DefaultRetainedFiles)
    {
        if (string.IsNullOrWhiteSpace(diagnosticsDirectory))
        {
            throw new ArgumentException(
                "A diagnostics directory is required.",
                nameof(diagnosticsDirectory));
        }

        DiagnosticsDirectory = Path.GetFullPath(diagnosticsDirectory);
        this.maxFileBytes = Math.Max(256, maxFileBytes);
        this.retainedFiles = Math.Max(1, retainedFiles);
        Directory.CreateDirectory(DiagnosticsDirectory);
        activeLogPath = Path.Combine(DiagnosticsDirectory, "argus.log");
    }

    public string DiagnosticsDirectory { get; }

    public void Write(
        DiagnosticSeverity severity,
        string component,
        string eventName,
        string? detail = null,
        Exception? exception = null)
    {
        var line = BuildLine(severity, component, eventName, detail, exception);
        try
        {
            lock (syncRoot)
            {
                RotateIfNeeded(Encoding.UTF8.GetByteCount(line));
                File.AppendAllText(activeLogPath, line, Encoding.UTF8);
            }
        }
        catch
        {
            // Diagnostics must never become a new application failure.
        }
    }

    public IDisposable BeginOperation(string component, string operation)
    {
        Write(DiagnosticSeverity.Information, component, $"{operation}.started");
        return new DiagnosticOperation(this, component, operation);
    }

    public static string Redact(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var redacted = value;
        redacted = AuthorizationRegex().Replace(redacted, "$1[redacted]");
        redacted = SecretAssignmentRegex().Replace(redacted, "$1[redacted]");
        redacted = TelegramIdentifierRegex().Replace(redacted, "$1[redacted]");
        redacted = PrivateJsonFieldRegex().Replace(redacted, "$1\"[redacted]\"");
        redacted = JwtRegex().Replace(redacted, "[redacted-token]");
        redacted = WindowsPathRegex().Replace(redacted, "[local-path]");
        redacted = UserProfileRegex().Replace(redacted, "[local-path]");
        return redacted;
    }

    private static string BuildLine(
        DiagnosticSeverity severity,
        string component,
        string eventName,
        string? detail,
        Exception? exception)
    {
        var builder = new StringBuilder();
        builder.Append(DateTimeOffset.UtcNow.ToString("O"));
        builder.Append(" | ");
        builder.Append(severity.ToString().ToUpperInvariant());
        builder.Append(" | ");
        builder.Append(NormalizeLabel(component));
        builder.Append(" | ");
        builder.Append(NormalizeLabel(eventName));

        if (!string.IsNullOrWhiteSpace(detail))
        {
            builder.Append(" | ");
            builder.Append(Redact(detail).ReplaceLineEndings(" "));
        }

        if (exception is not null)
        {
            builder.Append(" | exception=");
            builder.Append(exception.GetType().Name);
            builder.Append(" message=");
            builder.Append(Redact(exception.Message).ReplaceLineEndings(" "));
            if (!string.IsNullOrWhiteSpace(exception.StackTrace))
            {
                builder.Append(" stack=");
                builder.Append(Redact(exception.StackTrace).ReplaceLineEndings(" "));
            }
        }

        builder.AppendLine();
        return builder.ToString();
    }

    private static string NormalizeLabel(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "unknown";
        }

        return Regex.Replace(value.Trim(), @"[^A-Za-z0-9_.-]", "_");
    }

    private void RotateIfNeeded(int incomingBytes)
    {
        if (!File.Exists(activeLogPath))
        {
            return;
        }

        var currentSize = new FileInfo(activeLogPath).Length;
        if (currentSize + incomingBytes <= maxFileBytes)
        {
            return;
        }

        var oldestPath = GetRolledPath(retainedFiles);
        if (File.Exists(oldestPath))
        {
            File.Delete(oldestPath);
        }

        for (var index = retainedFiles - 1; index >= 1; index--)
        {
            var source = GetRolledPath(index);
            if (File.Exists(source))
            {
                File.Move(source, GetRolledPath(index + 1), overwrite: true);
            }
        }

        File.Move(activeLogPath, GetRolledPath(1), overwrite: true);
    }

    private string GetRolledPath(int index)
    {
        return Path.Combine(DiagnosticsDirectory, $"argus.{index}.log");
    }

    private sealed class DiagnosticOperation(
        LocalDiagnosticLog owner,
        string component,
        string operation) : IDisposable
    {
        private readonly Stopwatch stopwatch = Stopwatch.StartNew();
        private int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return;
            }

            stopwatch.Stop();
            owner.Write(
                DiagnosticSeverity.Information,
                component,
                $"{operation}.completed",
                $"duration_ms={stopwatch.ElapsedMilliseconds}");
        }
    }

    [GeneratedRegex(
        @"(?i)\b(authorization\s*[:=]\s*)(?:bearer\s+)?[^\s,;]+",
        RegexOptions.CultureInvariant)]
    private static partial Regex AuthorizationRegex();

    [GeneratedRegex(
        @"(?i)\b((?:api[-_ ]?key|access[-_ ]?token|refresh[-_ ]?token|bot[-_ ]?token|token|secret|password|credential)\s*[:=]\s*)(?:""[^""]*""|'[^']*'|[^\s,;]+)",
        RegexOptions.CultureInvariant)]
    private static partial Regex SecretAssignmentRegex();

    [GeneratedRegex(
        @"(?i)\b((?:telegram(?:[-_ ]?(?:user|chat))?[-_ ]?id|chat[-_ ]?id|user[-_ ]?id)\s*[:=]\s*)""?-?\d+""?",
        RegexOptions.CultureInvariant)]
    private static partial Regex TelegramIdentifierRegex();

    [GeneratedRegex(
        @"(?is)(""(?:prompt|messages?|memory|memories|content|context|readme|body|private_project_content)""\s*:\s*)(?:""(?:\\.|[^""])*""|\[[^\]]*\]|\{[^}]*\})",
        RegexOptions.CultureInvariant)]
    private static partial Regex PrivateJsonFieldRegex();

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
