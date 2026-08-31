namespace SDM.Infrastructure.Logging;

/// <summary>
/// Where SDM writes its diagnostics. Resolved without the dependency container, because
/// the most valuable log entry of all is the one explaining why the container could not
/// be built.
/// </summary>
public static class SdmLogPaths
{
    public static string ResolveDirectory(string? configured = null)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        return string.IsNullOrEmpty(root)
            ? Path.Combine(AppContext.BaseDirectory, "logs")
            : Path.Combine(root, "SDM", "logs");
    }
}
