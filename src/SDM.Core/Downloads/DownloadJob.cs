namespace SDM.Core.Downloads;

/// <summary>
/// A transfer as it is remembered between runs. The byte counts are a snapshot for
/// display; the authoritative amount already on disk is the length of the partial file,
/// which the engine reads when it resumes.
/// </summary>
public sealed record DownloadJob
{
    public required Guid Id { get; init; }

    public required string Address { get; init; }

    /// <summary>Null until the response headers have settled the file name.</summary>
    public string? DestinationPath { get; init; }

    public long BytesReceived { get; init; }

    public long? TotalBytes { get; init; }

    public DownloadStatus Status { get; init; } = DownloadStatus.Pending;

    public string? Detail { get; init; }

    /// <summary>The server's Content-Type. Null for jobs saved before it was recorded.</summary>
    public string? MediaType { get; init; }

    public FileCategory Category { get; init; } = FileCategory.Other;

    /// <summary>
    /// The destination came from a save dialog rather than from SDM's own sorting.
    ///
    /// A restored row has to know the difference. Resuming reads the partial file from
    /// the folder the transfer was told to write into, so a transfer the user sent to
    /// another drive has to be told that folder again — and one SDM sorted into a
    /// category folder must not be, or the sorting and the "name (1)" that goes with it
    /// would be skipped on the second attempt.
    /// </summary>
    public bool ChosenByUser { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }
}
