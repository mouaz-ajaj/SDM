using System.Text;

namespace SDM.Core.Downloads;

/// <summary>
/// Turns an untrusted name — a server's <c>Content-Disposition</c> or a URL segment —
/// into a bare file name that cannot escape its destination directory.
/// </summary>
public static class SafeFileName
{
    public const string Fallback = "download";

    private const int MaximumLength = 150;

    private static readonly HashSet<char> InvalidCharacters = [.. Path.GetInvalidFileNameChars()];

    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// Prefers the name the server suggested, falling back to the URL's last segment.
    /// The result is always a single safe file name.
    /// </summary>
    public static string Resolve(string? serverSuggestedName, Uri source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return string.IsNullOrWhiteSpace(serverSuggestedName)
            ? FromUri(source)
            : Sanitize(serverSuggestedName);
    }

    public static string FromUri(Uri source)
    {
        ArgumentNullException.ThrowIfNull(source);

        string lastSegment = source.IsAbsoluteUri ? source.AbsolutePath : source.OriginalString;
        return Sanitize(Uri.UnescapeDataString(lastSegment));
    }

    public static string Sanitize(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return Fallback;
        }

        // Keep only the final segment. This is what defeats "../../etc/passwd",
        // "C:\Windows\System32\evil.dll", and every other traversal attempt: whatever
        // the server sent, only the part after the last separator can survive.
        string name = candidate.Replace('\\', '/').Trim().Trim('"');
        int lastSeparator = name.LastIndexOf('/');
        if (lastSeparator >= 0)
        {
            name = name[(lastSeparator + 1)..];
        }

        StringBuilder builder = new(name.Length);
        foreach (char character in name)
        {
            builder.Append(InvalidCharacters.Contains(character) || char.IsControl(character) ? '_' : character);
        }

        // Windows silently strips trailing dots and spaces, which would let "evil.exe. "
        // resolve to a different file than the one that was validated.
        string sanitized = builder.ToString().Trim().TrimEnd('.', ' ');

        if (sanitized.Length == 0 || sanitized is "." or "..")
        {
            return Fallback;
        }

        if (IsReservedDeviceName(sanitized))
        {
            sanitized = "_" + sanitized;
        }

        return Truncate(sanitized);
    }

    private static bool IsReservedDeviceName(string name)
    {
        int dot = name.IndexOf('.', StringComparison.Ordinal);
        string stem = dot < 0 ? name : name[..dot];
        return ReservedDeviceNames.Contains(stem);
    }

    private static string Truncate(string name)
    {
        if (name.Length <= MaximumLength)
        {
            return name;
        }

        string extension = Path.GetExtension(name);
        if (extension.Length >= MaximumLength)
        {
            return name[..MaximumLength];
        }

        return string.Concat(name.AsSpan(0, MaximumLength - extension.Length), extension);
    }
}
