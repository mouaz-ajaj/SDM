using SDM.Application.Downloads;

namespace SDM.Infrastructure.Downloads;

/// <summary>
/// The user's Downloads folder, with the executable's own directory as a last resort
/// for environments where a user profile is not available.
/// </summary>
public sealed class DefaultDownloadFolder : IDownloadFolder
{
    public string GetPath()
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        return string.IsNullOrEmpty(profile)
            ? AppContext.BaseDirectory
            : Path.Combine(profile, "Downloads");
    }
}
