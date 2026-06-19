using Argus.Core.Models;
using Argus.Core.Services;
using Argus.Data;
using Argus.Data.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Argus.Tests;

public sealed class ProjectInstructionServiceTests
{
    [Fact]
    public async Task InstructionsPersistLocallyByProjectAndSupportBatchRetrieval()
    {
        var path = CreateDatabasePath();
        var firstProjectId = Guid.NewGuid();
        var secondProjectId = Guid.NewGuid();

        using (var provider = CreateProvider(path))
        {
            await provider.GetRequiredService<ArgusDatabaseInitializer>()
                .InitializeAsync();
            var service =
                provider.GetRequiredService<IProjectInstructionService>();

            var saved = await service.SaveAsync(
                firstProjectId,
                "\r\n  Keep changes focused.\r\nUse existing patterns.  \r\n");
            await service.SaveAsync(secondProjectId, "Run targeted tests.");

            Assert.NotNull(saved);
            Assert.Equal(
                "Keep changes focused.\nUse existing patterns.",
                saved.Content);

            var batch = await service.GetManyAsync(
                [firstProjectId, secondProjectId, firstProjectId]);
            Assert.Equal(2, batch.Count);
            Assert.Equal(saved.Content, batch[firstProjectId].Content);
            Assert.Equal("Run targeted tests.", batch[secondProjectId].Content);

            var factory =
                provider.GetRequiredService<IDbContextFactory<ArgusDbContext>>();
            await using var db = await factory.CreateDbContextAsync();
            var stored = await db.AppSettings
                .Where(setting =>
                    setting.Key.StartsWith("ProjectInstruction:v1:"))
                .ToListAsync();
            Assert.Equal(2, stored.Count);
            Assert.Contains(
                stored,
                setting =>
                    setting.Key.EndsWith(firstProjectId.ToString("N")) &&
                    setting.Value == saved.Content);
        }

        using (var provider = CreateProvider(path))
        {
            var service =
                provider.GetRequiredService<IProjectInstructionService>();
            var restored = await service.GetAsync(firstProjectId);

            Assert.NotNull(restored);
            Assert.Equal(
                "Keep changes focused.\nUse existing patterns.",
                restored.Content);
        }
    }

    [Fact]
    public async Task EmptyContentClearsTheStoredInstruction()
    {
        using var provider = CreateProvider(CreateDatabasePath());
        await provider.GetRequiredService<ArgusDatabaseInitializer>()
            .InitializeAsync();
        var service = provider.GetRequiredService<IProjectInstructionService>();
        var projectId = Guid.NewGuid();

        await service.SaveAsync(projectId, "Keep this local.");
        var cleared = await service.SaveAsync(projectId, " \r\n\t ");

        Assert.Null(cleared);
        Assert.Null(await service.GetAsync(projectId));
        Assert.Empty(await service.GetManyAsync([projectId]));
    }

    [Fact]
    public void PolicyNormalizesAndEnforcesTheCharacterLimit()
    {
        Assert.Equal(
            "first\nsecond",
            ProjectInstructionPolicy.Normalize(" \r\nfirst\rsecond\r\n "));
        Assert.Equal(
            ProjectInstructionPolicy.MaxLength,
            ProjectInstructionPolicy.Normalize(
                new string('x', ProjectInstructionPolicy.MaxLength)).Length);

        var exception = Assert.Throws<ArgumentException>(
            () => ProjectInstructionPolicy.Normalize(
                new string('x', ProjectInstructionPolicy.MaxLength + 1)));
        Assert.DoesNotContain(new string('x', 20), exception.Message);

        var formattingException = Assert.Throws<ArgumentException>(
            () => ProjectInstructionPolicy.Normalize(
                "Keep changes focused.\u202Etxt.exe"));
        Assert.Contains(
            "unsupported control or formatting",
            formattingException.Message);
    }

    [Fact]
    public void OutboundPolicyRedactsSecretsAndWindowsPaths()
    {
        var outbound = ProjectInstructionPolicy.RedactForOutbound(
            """
            Keep responses concise.
            Open D:\Private\Cortex\README.md
            api_key=super-secret-value
            Authorization: Bearer private-bearer-value
            {"client_secret":"private-json-value"}
            """);

        Assert.Contains("Keep responses concise.", outbound);
        Assert.Contains("[local-path]", outbound);
        Assert.Contains("[redacted]", outbound);
        Assert.DoesNotContain(@"D:\Private", outbound);
        Assert.DoesNotContain("super-secret-value", outbound);
        Assert.DoesNotContain("private-bearer-value", outbound);
        Assert.DoesNotContain("private-json-value", outbound);
    }

    [Fact]
    public async Task OperationsHonorCancellationAndDiagnosticsExcludeContent()
    {
        var diagnostics = new CapturingDiagnosticLog();
        using var provider = CreateProvider(
            CreateDatabasePath(),
            diagnostics);
        await provider.GetRequiredService<ArgusDatabaseInitializer>()
            .InitializeAsync();
        var service = provider.GetRequiredService<IProjectInstructionService>();
        var projectId = Guid.NewGuid();
        const string privateInstruction =
            "DO_NOT_LOG_THIS_INSTRUCTION api_key=private-instruction-secret";

        await service.SaveAsync(projectId, privateInstruction);
        await service.GetAsync(projectId);
        await service.GetManyAsync([projectId]);
        await service.ClearAsync(projectId);

        var diagnosticText = string.Join("\n", diagnostics.Entries);
        Assert.DoesNotContain("DO_NOT_LOG_THIS_INSTRUCTION", diagnosticText);
        Assert.DoesNotContain("private-instruction-secret", diagnosticText);
        Assert.Contains($"project_id={projectId:N}", diagnosticText);
        Assert.Contains("content_length=", diagnosticText);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.GetManyAsync([projectId], cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.SaveAsync(
                projectId,
                "cancelled content",
                cancellation.Token));
    }

    private static ServiceProvider CreateProvider(
        string path,
        IDiagnosticLog? diagnosticLog = null)
    {
        var services = new ServiceCollection();
        if (diagnosticLog is not null)
        {
            services.AddSingleton(diagnosticLog);
        }

        services.AddArgusData(path);
        return services.BuildServiceProvider();
    }

    private static string CreateDatabasePath() =>
        Path.Combine(
            Path.GetTempPath(),
            "ArgusTests",
            $"{Guid.NewGuid():N}.db");

    private sealed class CapturingDiagnosticLog : IDiagnosticLog
    {
        public string DiagnosticsDirectory => string.Empty;
        public List<string> Entries { get; } = [];

        public void Write(
            DiagnosticSeverity severity,
            string component,
            string eventName,
            string? detail = null,
            Exception? exception = null)
        {
            Entries.Add(
                $"{severity}|{component}|{eventName}|{detail}|{exception?.Message}");
        }

        public IDisposable BeginOperation(string component, string operation) =>
            NoopDisposable.Instance;
    }

    private sealed class NoopDisposable : IDisposable
    {
        public static NoopDisposable Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
