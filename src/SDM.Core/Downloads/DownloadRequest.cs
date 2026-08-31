namespace SDM.Core.Downloads;

public sealed record DownloadRequest
{
    public DownloadRequest(Uri source, string destinationPath)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (!source.IsAbsoluteUri || (source.Scheme != Uri.UriSchemeHttp && source.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("A download source must be an absolute HTTP or HTTPS URI.", nameof(source));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        Source = source;
        DestinationPath = destinationPath;
    }

    public Uri Source { get; }

    public string DestinationPath { get; }
}
