namespace SDM.Application.Downloads;

public sealed class DownloadOptions
{
    public const string SectionName = "Downloads";

    /// <summary>
    /// Sort finished files into a sub-folder per category — Documents, Video, Programs
    /// and so on — instead of piling everything into one folder.
    /// </summary>
    public bool OrganizeIntoCategoryFolders { get; init; } = true;

    /// <summary>
    /// Ask where to save every download instead of deciding automatically. Off by
    /// default: a dialog per file is the right choice for some people and an
    /// interruption for everyone else.
    /// </summary>
    public bool AskWhereToSave { get; init; }

    /// <summary>How many transfers may run at once across the whole application.</summary>
    public int MaximumConcurrent { get; init; } = 3;

    /// <summary>
    /// How many transfers may run against a single host. Distinct from the connection
    /// budget below: one transfer can hold several connections when it is segmented.
    /// </summary>
    public int MaximumPerHost { get; init; } = 2;

    /// <summary>
    /// The total TCP connections allowed to one host, shared by every transfer aimed at
    /// it. This is the limit servers actually enforce, and exceeding it earns a 429
    /// rather than more speed.
    /// </summary>
    public int MaximumConnectionsPerHost { get; init; } = 6;

    /// <summary>Segments a single transfer may split into, subject to the connection budget.</summary>
    public int MaximumSegments { get; init; } = 4;

    /// <summary>
    /// Below this size, segmenting costs more in extra handshakes than it returns.
    /// </summary>
    public long SegmentThresholdBytes { get; init; } = 8 * 1024 * 1024;

    /// <summary>Total attempts, including the first. 1 disables retrying.</summary>
    public int MaximumAttempts { get; init; } = 4;

    /// <summary>
    /// Fail a transfer whose connections all go silent for this long. The HTTP client's
    /// own timeout is disabled because it spans the whole body and would kill large files.
    /// </summary>
    public int IdleTimeoutSeconds { get; init; } = 60;

    /// <summary>An upper bound on an honoured <c>Retry-After</c>; servers may ask for hours.</summary>
    public int MaximumRetryDelaySeconds { get; init; } = 60;
}
