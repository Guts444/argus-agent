using Argus.Core.Services;

namespace Argus.App.Services;

internal static class PackagedSmokeTest
{
    private const string ScenarioVariable = "ARGUS_SMOKE_TEST_SCENARIO";
    private const string ResultPathVariable = "ARGUS_SMOKE_TEST_RESULT_PATH";

    public static string Scenario =>
        Environment.GetEnvironmentVariable(ScenarioVariable)?.Trim().ToLowerInvariant()
        ?? string.Empty;

    public static bool IsActive => Scenario is "fresh" or "existing";

    public static bool IsScenario(string scenario)
    {
        return string.Equals(Scenario, scenario, StringComparison.OrdinalIgnoreCase);
    }

    public static void Complete(string stage)
    {
        if (!IsActive)
        {
            return;
        }

        var resultPath = Environment.GetEnvironmentVariable(ResultPathVariable);
        if (string.IsNullOrWhiteSpace(resultPath))
        {
            throw new InvalidOperationException(
                "Packaged smoke test result path is not configured.");
        }

        var fullPath = Path.GetFullPath(resultPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath) ?? ".");
        File.WriteAllText(
            fullPath,
            $"{Scenario}:{stage}{Environment.NewLine}",
            System.Text.Encoding.UTF8);

        if (App.Services.GetService(typeof(IDiagnosticLog)) is IDiagnosticLog diagnostics)
        {
            diagnostics.Write(
                DiagnosticSeverity.Information,
                "packaged_smoke",
                "scenario.completed",
                $"scenario={Scenario} stage={stage}");
        }

        App.DispatcherQueue.TryEnqueue(() => App.Window.Close());
    }
}
