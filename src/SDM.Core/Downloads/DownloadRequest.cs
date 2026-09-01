namespace SDM.Core.Downloads;

/// <summary>
/// A validated transfer instruction. The caller chooses the folder; the final file name
/// is settled by the engine once the response headers arrive, because only then is the
/// server's suggested name known.
/// </summary>
public sealed record DownloadRequest
{
    public DownloadRequest(
        Uri source,
        string destinationDirectory,
        string? preferredFileName = null,
        bool chosenByUser = false,
        RequestContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (!source.IsAbsoluteUri || (source.Scheme != Uri.UriSchemeHttp && source.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("Only HTTP or HTTPS addresses can be downloaded.", nameof(source));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);

        Source = source;
        DestinationDirectory = Path.GetFullPath(destinationDirectory);
        PreferredFileName = preferredFileName is null ? null : SafeFileName.Sanitize(preferredFileName);
        ChosenByUser = chosenByUser;
        Context = context is null || context.IsEmpty ? null : context;
    }

    public Uri Source { get; }

    public string DestinationDirectory { get; }

    /// <summary>An explicit name chosen by the caller, already sanitized. Overrides the server's.</summary>
    public string? PreferredFileName { get; }

    /// <summary>
    /// True when the folder and name came from a save dialog. The system dialog has
    /// already asked about replacing an existing file, so the engine must write exactly
    /// where it was told rather than inventing "name (1)".
    /// </summary>
    public bool ChosenByUser { get; }

    /// <summary>
    /// The browser session this download belongs to, when it came from one. Null for a
    /// URL typed into SDM, which belongs to no session at all.
    /// </summary>
    public RequestContext? Context { get; }
}
