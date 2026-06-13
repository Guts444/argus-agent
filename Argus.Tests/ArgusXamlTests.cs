using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Argus.Tests;

public sealed class ArgusXamlTests
{
    [Fact]
    public void ArgusStaticResourceReferencesResolve()
    {
        var repositoryRoot = FindRepositoryRoot();
        var appDirectory = Path.Combine(repositoryRoot, "Argus.App");
        var xamlFiles = GetSourceXamlFiles(appDirectory);

        var definitions = new HashSet<string>(StringComparer.Ordinal);
        var references = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var definitionPattern = new Regex("x:Key=\"([^\"]+)\"");
        var referencePattern = new Regex(@"\{StaticResource\s+([^},]+)");

        foreach (var xamlFile in xamlFiles)
        {
            var xaml = File.ReadAllText(xamlFile);
            foreach (Match match in definitionPattern.Matches(xaml))
            {
                definitions.Add(match.Groups[1].Value);
            }

            foreach (Match match in referencePattern.Matches(xaml))
            {
                var key = match.Groups[1].Value.Trim();
                if (!key.StartsWith("Argus", StringComparison.Ordinal))
                {
                    continue;
                }

                if (!references.TryGetValue(key, out var files))
                {
                    files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    references[key] = files;
                }

                files.Add(Path.GetRelativePath(repositoryRoot, xamlFile));
            }
        }

        var missing = references
            .Where(reference => !definitions.Contains(reference.Key))
            .OrderBy(reference => reference.Key)
            .Select(reference =>
                $"{reference.Key} ({string.Join(", ", reference.Value.OrderBy(path => path))})")
            .ToArray();

        Assert.True(
            missing.Length == 0,
            $"Undefined Argus StaticResource references: {string.Join("; ", missing)}");
    }

    [Fact]
    public void VisibilityConverterInversionUsesSupportedParameter()
    {
        var repositoryRoot = FindRepositoryRoot();
        var appDirectory = Path.Combine(repositoryRoot, "Argus.App");
        var xamlFiles = GetSourceXamlFiles(appDirectory);
        var invalidBindings = new List<string>();
        var bindingPattern = new Regex(
            @"\{Binding[^}]+Converter=\{StaticResource\s+(BoolToVisibilityConverter|NullToVisibilityConverter)\}[^}]*\}");

        foreach (var xamlFile in xamlFiles)
        {
            var xaml = File.ReadAllText(xamlFile);
            foreach (Match match in bindingPattern.Matches(xaml))
            {
                if (match.Value.Contains("ConverterParameter=!", StringComparison.Ordinal))
                {
                    invalidBindings.Add(
                        $"{Path.GetRelativePath(repositoryRoot, xamlFile)}: {match.Value}");
                }
            }
        }

        Assert.True(
            invalidBindings.Count == 0,
            $"Unsupported visibility inversion parameters: {string.Join("; ", invalidBindings)}");
    }

    [Fact]
    public void DashboardUsesDedicatedNextActionsWidget()
    {
        var repositoryRoot = FindRepositoryRoot();
        var mainPagePath = Path.Combine(repositoryRoot, "Argus.App", "MainPage.xaml");
        var document = XDocument.Load(mainPagePath);
        var textBlocks = document
            .Descendants()
            .Where(element => element.Name.LocalName == "TextBlock")
            .ToArray();

        Assert.DoesNotContain(
            textBlocks,
            element => string.Equals(
                (string?)element.Attribute("Text"),
                "COMMAND CENTER",
                StringComparison.Ordinal));

        var cockpitTitle = Assert.Single(
            textBlocks,
            element =>
                string.Equals(
                    (string?)element.Attribute("Text"),
                    "PROJECT COCKPIT",
                    StringComparison.Ordinal));
        var actionsTitle = Assert.Single(
            textBlocks,
            element =>
                string.Equals(
                    (string?)element.Attribute("Text"),
                    "NEXT ACTIONS",
                    StringComparison.Ordinal));

        var cockpitWidget = FindDashboardWidgetAncestor(cockpitTitle);
        var actionsWidget = FindDashboardWidgetAncestor(actionsTitle);

        Assert.NotNull(cockpitWidget);
        Assert.NotNull(actionsWidget);
        Assert.NotSame(cockpitWidget, actionsWidget);
    }

    private static XElement? FindDashboardWidgetAncestor(XElement element)
    {
        return element
            .Ancestors()
            .FirstOrDefault(ancestor =>
                ancestor.Name.LocalName == "Border" &&
                string.Equals(
                    (string?)ancestor.Attribute("Style"),
                    "{StaticResource DashboardWidgetStyle}",
                    StringComparison.Ordinal));
    }

    private static string[] GetSourceXamlFiles(string appDirectory)
    {
        var excludedSegments = new[]
        {
            $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
            $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"
        };

        return Directory
            .GetFiles(appDirectory, "*.xaml", SearchOption.AllDirectories)
            .Where(path => excludedSegments.All(segment =>
                !path.Contains(segment, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Argus.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not find the Argus repository root from {AppContext.BaseDirectory}.");
    }
}
