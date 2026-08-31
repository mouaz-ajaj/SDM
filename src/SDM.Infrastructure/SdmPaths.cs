namespace SDM.Infrastructure;

/// <summary>
/// Where SDM keeps everything that belongs to the user rather than to the installation.
/// The install folder is read-only once a program is properly installed, and is replaced
/// wholesale by every update — nothing the user owns can live there.
/// </summary>
public static class SdmPaths
{
    public const string UserSettingsFileName = "settings.json";

    public static string UserDataDirectory
    {
        get
        {
            string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            return string.IsNullOrEmpty(root)
                ? AppContext.BaseDirectory
                : Path.Combine(root, "SDM");
        }
    }

    public static string UserSettingsPath => Path.Combine(UserDataDirectory, UserSettingsFileName);

    public static string EnsureUserDataDirectory()
    {
        string directory = UserDataDirectory;
        Directory.CreateDirectory(directory);
        return directory;
    }
}
