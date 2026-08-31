using SDM.Core.Downloads;

namespace SDM.Application.Downloads;

/// <summary>
/// Decides which folder under the download root a finished file belongs in. The engine
/// asks once the response headers have settled the name and type; the policy itself is
/// application configuration, not something the transfer code should know about.
/// </summary>
public interface IDownloadLayout
{
    string ResolveDirectory(string baseDirectory, string fileName, string? mediaType);
}
