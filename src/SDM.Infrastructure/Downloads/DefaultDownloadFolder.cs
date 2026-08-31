using Microsoft.Extensions.Options;
using SDM.Application.Downloads;

namespace SDM.Infrastructure.Downloads;

/// <summary>
/// The configured folder, or the user's Downloads folder when none is set, with the
/// executable's own directory as a last resort for environments with no user profile.
/// </summary>
public sealed class DefaultDownloadFolder : IDownloadFolder
{
    private readonly IOptionsMonitor<DownloadOptions> _options;

    public DefaultDownloadFolder(IOptionsMonitor<DownloadOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    public string GetPath()
    {
        // Read every time: a folder changed in settings should apply to the next
        // download, not to the next launch.
        string configured = _options.CurrentValue.Folder;

        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        return string.IsNullOrEmpty(profile)
            ? AppContext.BaseDirectory
            : Path.Combine(profile, "Downloads");
    }
}
