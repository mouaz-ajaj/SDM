using System.Text.Json;

namespace SDM.Infrastructure.Downloads;

/// <summary>How far one segment of a split transfer has got. <paramref name="End"/> is inclusive.</summary>
internal sealed record SegmentState(long Start, long End, long Completed)
{
    public long Length => End - Start + 1;

    public long Position => Start + Completed;

    public bool IsComplete => Completed >= Length;
}

/// <summary>
/// What a <c>.part</c> file belongs to. Resuming purely on a matching file name would be
/// dangerous: two different URLs can easily resolve to <c>setup.exe</c>, and appending
/// one server's bytes to another's produces a corrupt file that still looks complete.
/// This sidecar makes the owning URL explicit, and survives the process being killed.
/// </summary>
/// <param name="Segments">
/// Null for a single-stream transfer, where the file's own length is how far it got.
/// A split transfer writes into the middle of the file, so its progress cannot be read
/// back from the file size and has to be recorded per segment.
/// </param>
internal sealed record PartialFileMetadata(
    string Url,
    long? TotalBytes,
    string? Validator,
    SegmentState[]? Segments);

/// <summary>A partial file on disk that a new request is allowed to continue.</summary>
internal sealed record ResumablePartial(
    string DestinationPath,
    string PartialPath,
    long Length,
    PartialFileMetadata Metadata);

internal static class PartialFile
{
    public const string PartialSuffix = ".part";
    public const string MetadataSuffix = ".meta";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Finds a partial file in <paramref name="directory"/> that was created for exactly
    /// this URL, or null if there is nothing safe to continue.
    /// </summary>
    public static ResumablePartial? FindFor(string directory, Uri source)
    {
        if (!Directory.Exists(directory))
        {
            return null;
        }

        foreach (string metadataPath in EnumerateMetadata(directory))
        {
            PartialFileMetadata? metadata = TryRead(metadataPath);

            if (metadata is null
                || !string.Equals(metadata.Url, source.AbsoluteUri, StringComparison.Ordinal))
            {
                continue;
            }

            string partialPath = metadataPath[..^MetadataSuffix.Length];

            if (!File.Exists(partialPath))
            {
                continue;
            }

            long length = new FileInfo(partialPath).Length;

            if (metadata.Segments is { Length: > 0 } segments)
            {
                // A split file is pre-allocated at full size, so its length says nothing
                // about progress. It is resumable while any segment is unfinished.
                if (segments.All(segment => segment.IsComplete))
                {
                    continue;
                }
            }
            else
            {
                if (length <= 0)
                {
                    continue;
                }

                // A partial at or past the advertised size is not resumable; something is
                // stale, so let the caller start again rather than produce a short file.
                if (metadata.TotalBytes is { } total && length >= total)
                {
                    continue;
                }
            }

            return new ResumablePartial(
                partialPath[..^PartialSuffix.Length], partialPath, length, metadata);
        }

        return null;
    }

    public static void Write(string partialPath, PartialFileMetadata metadata)
    {
        try
        {
            File.WriteAllText(
                partialPath + MetadataSuffix,
                JsonSerializer.Serialize(metadata, SerializerOptions));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Losing the sidecar only costs the ability to resume; it must never fail the
            // transfer that is about to start.
        }
    }

    public static void Delete(string partialPath)
    {
        TryDelete(partialPath);
        TryDelete(partialPath + MetadataSuffix);
    }

    /// <summary>
    /// The download folder and one level below it. Sorting into category sub-folders
    /// means a partial file is rarely at the top level any more, and a resume that
    /// cannot find its own partial file silently starts the whole download again.
    /// </summary>
    private static IEnumerable<string> EnumerateMetadata(string directory)
    {
        string pattern = "*" + PartialSuffix + MetadataSuffix;

        try
        {
            return Directory.EnumerateFiles(directory, pattern, SearchOption.AllDirectories);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static PartialFileMetadata? TryRead(string metadataPath)
    {
        try
        {
            return JsonSerializer.Deserialize<PartialFileMetadata>(
                File.ReadAllText(metadataPath), SerializerOptions);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best effort: a leftover file is preferable to a crash while cleaning up.
        }
    }
}
