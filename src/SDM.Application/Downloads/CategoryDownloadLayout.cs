using Microsoft.Extensions.Options;
using SDM.Core.Downloads;

namespace SDM.Application.Downloads;

/// <summary>
/// Sorts downloads into a sub-folder per category, the way IDM and Free Download Manager
/// do. Turning it off writes everything straight into the download folder.
/// </summary>
public sealed class CategoryDownloadLayout : IDownloadLayout
{
    private readonly DownloadOptions _options;

    public CategoryDownloadLayout(IOptions<DownloadOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
    }

    public string ResolveDirectory(string baseDirectory, string fileName, string? mediaType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);

        if (!_options.OrganizeIntoCategoryFolders)
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
