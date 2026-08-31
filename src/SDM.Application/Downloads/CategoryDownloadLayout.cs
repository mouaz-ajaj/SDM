using Microsoft.Extensions.Options;
using SDM.Core.Downloads;

namespace SDM.Application.Downloads;

/// <summary>
/// Sorts downloads into a sub-folder per category, the way IDM and Free Download Manager
/// do. Turning it off writes everything straight into the download folder.
/// </summary>
public sealed class CategoryDownloadLayout : IDownloadLayout
{
    private readonly IOptionsMonitor<DownloadOptions> _options;

    public CategoryDownloadLayout(IOptionsMonitor<DownloadOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    public string ResolveDirectory(string baseDirectory, string fileName, string? mediaType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);

        // Read per call so turning sorting off takes effect on the next download rather
        // than the next launch.
        if (!_options.CurrentValue.OrganizeIntoCategoryFolders)
        {
            return baseDirectory;
        }

        FileCategory category = FileCategories.Resolve(fileName, mediaType);

        // Anything unrecognised stays at the top rather than collecting in an "Other"
        // folder nobody ever opens.
        return category == FileCategory.Other
            ? baseDirectory
            : Path.Combine(baseDirectory, FileCategories.FolderNameFor(category));
    }
}
