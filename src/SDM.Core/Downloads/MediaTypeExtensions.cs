namespace SDM.Core.Downloads;

/// <summary>
/// Suggests a file extension from the server's Content-Type, for the many URLs that end in
/// an opaque id — a Google image is <c>images?q=tbn:…</c>, and saving that as a file called
/// "images" leaves something Windows cannot open, preview or associate with a program,
/// even though the server said plainly that it was a JPEG.
///
/// Only ever consulted when the name has no extension of its own. A name that already
/// carries one is left alone: the file the user asked for is the file they get, and
/// servers mislabel types far more often than they mislabel names.
/// </summary>
public static class MediaTypeExtensions
{
    private static readonly Dictionary<string, string> ByMediaType =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // Where the subtype is not the extension anyone expects.
            ["image/jpeg"] = ".jpg",
            ["image/svg+xml"] = ".svg",
            ["image/x-icon"] = ".ico",
            ["image/vnd.microsoft.icon"] = ".ico",
            ["image/tiff"] = ".tif",

            ["audio/mpeg"] = ".mp3",
            ["audio/mp4"] = ".m4a",
            ["audio/x-ms-wma"] = ".wma",

            ["video/quicktime"] = ".mov",
            ["video/x-matroska"] = ".mkv",
            ["video/x-msvideo"] = ".avi",
            ["video/mpeg"] = ".mpg",

            ["text/plain"] = ".txt",
            ["text/html"] = ".html",
            ["text/markdown"] = ".md",
            ["text/csv"] = ".csv",
            ["text/xml"] = ".xml",

            ["application/pdf"] = ".pdf",
            ["application/zip"] = ".zip",
            ["application/gzip"] = ".gz",
            ["application/x-tar"] = ".tar",
            ["application/x-7z-compressed"] = ".7z",
            ["application/vnd.rar"] = ".rar",
            ["application/x-iso9660-image"] = ".iso",
            ["application/json"] = ".json",
            ["application/xml"] = ".xml",
            ["application/rtf"] = ".rtf",
            ["application/msword"] = ".doc",
            ["application/vnd.openxmlformats-officedocument.wordprocessingml.document"] = ".docx",
            ["application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"] = ".xlsx",
            ["application/vnd.openxmlformats-officedocument.presentationml.presentation"] = ".pptx",
            ["application/vnd.android.package-archive"] = ".apk",
            ["application/x-msdownload"] = ".exe",
            ["application/vnd.microsoft.portable-executable"] = ".exe",
            ["application/epub+zip"] = ".epub",
        };

    /// <summary>
    /// The extension for a media type, including the dot, or null when the type says
    /// nothing useful. <c>application/octet-stream</c> in particular means "bytes" — it is
    /// the type a server sends when it does not know either, and inventing an extension
    /// from it would be a guess dressed up as knowledge.
    /// </summary>
    public static string? ForMediaType(string? mediaType)
    {
        if (string.IsNullOrWhiteSpace(mediaType))
        {
            return null;
        }

        // Trim any "; charset=..." the server appended.
        int parameters = mediaType.IndexOf(';', StringComparison.Ordinal);
        string media = (parameters < 0 ? mediaType : mediaType[..parameters]).Trim();

        if (ByMediaType.TryGetValue(media, out string? known))
        {
            return known;
        }

        int slash = media.IndexOf('/', StringComparison.Ordinal);

        if (slash < 0 || slash == media.Length - 1)
        {
            return null;
        }

        string top = media[..slash];
        string subtype = media[(slash + 1)..];

        // Only the media families whose subtype really is the common extension: image/png,
        // video/webm, audio/flac. "application" is not one of them — application/x-foo-bar
        // says nothing about what to call the file.
        if (top is not ("image" or "video" or "audio"))
        {
            return null;
        }

        // image/svg+xml → svg, audio/x-flac → flac.
        int plus = subtype.IndexOf('+', StringComparison.Ordinal);
        if (plus > 0)
        {
            subtype = subtype[..plus];
        }

        if (subtype.StartsWith("x-", StringComparison.OrdinalIgnoreCase))
        {
            subtype = subtype[2..];
        }

        // Anything longer or stranger than a real extension is left alone rather than
        // turned into one.
        bool plausible = subtype.Length is >= 2 and <= 5
            && subtype.All(char.IsAsciiLetterOrDigit);

        return plausible ? "." + subtype.ToLowerInvariant() : null;
    }
}
