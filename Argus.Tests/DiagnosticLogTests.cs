using Argus.Core.Services;

namespace Argus.Tests;

public sealed class DiagnosticLogTests
{
    [Fact]
    public void RedactionRemovesCredentialsPathsTelegramIdsAndPrivateContent()
    {
        var raw =
            """
            Authorization: Bearer secret-bearer-value
            api_key=sk-private
            bot_token: 123456:telegram-secret
            chat_id=998877
            path=D:\Private\Argus\README.md
            {"prompt":"private prompt","memory":"private memory","content":"project source"}
            """;

        var redacted = LocalDiagnosticLog.Redact(raw);

        Assert.DoesNotContain("secret-bearer-value", redacted);
        Assert.DoesNotContain("sk-private", redacted);
        Assert.DoesNotContain("telegram-secret", redacted);
        Assert.DoesNotContain("998877", redacted);
        Assert.DoesNotContain(@"D:\Private", redacted);
        Assert.DoesNotContain("private prompt", redacted);
        Assert.DoesNotContain("private memory", redacted);
        Assert.DoesNotContain("project source", redacted);
        Assert.Contains("[redacted]", redacted);
        Assert.Contains("[local-path]", redacted);
    }

    [Fact]
    public void RollingLogRetainsBoundedFilesAndOperationTimings()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "ArgusTests",
            Guid.NewGuid().ToString("N"));
        var log = new LocalDiagnosticLog(
            directory,
            maxFileBytes: 256,
            retainedFiles: 3);

        for (var index = 0; index < 30; index++)
        {
            log.Write(
                DiagnosticSeverity.Information,
                "test",
                "rolling",
                $"index={index} payload={new string('x', 80)}");
        }

        using (log.BeginOperation("startup", "database_initialize"))
        {
            Thread.Sleep(2);
        }

        var files = Directory.GetFiles(directory, "argus*.log");
        Assert.InRange(files.Length, 2, 4);
        var combined = string.Join(
            Environment.NewLine,
            files.Select(File.ReadAllText));
        Assert.Contains("database_initialize.started", combined);
        Assert.Contains("duration_ms=", combined);
    }
}
