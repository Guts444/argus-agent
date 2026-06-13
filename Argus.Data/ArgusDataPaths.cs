namespace Argus.Data;

public static class ArgusDataPaths
{
    private const string DatabasePathEnvironmentVariable = "ARGUS_DATABASE_PATH";

    public static string GetDefaultDatabasePath()
    {
        return GetDatabasePath("Argus");
    }

    public static string GetDevelopmentDatabasePath()
    {
        return GetDatabasePath(Path.Combine("Argus", "Development"));
    }

    private static string GetDatabasePath(string relativeDirectory)
    {
        var configuredPath = Environment.GetEnvironmentVariable(DatabasePathEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var fullPath = Path.GetFullPath(configuredPath.Trim());
            var configuredDirectory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(configuredDirectory))
            {
                Directory.CreateDirectory(configuredDirectory);
            }

            return fullPath;
        }

        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            relativeDirectory);
        Directory.CreateDirectory(root);
        return Path.Combine(root, "argus.db");
    }
}
