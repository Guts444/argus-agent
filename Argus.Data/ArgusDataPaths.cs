namespace Argus.Data;

public static class ArgusDataPaths
{
    public static string GetDefaultDatabasePath()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Argus");
        Directory.CreateDirectory(root);
        return Path.Combine(root, "argus.db");
    }
}
