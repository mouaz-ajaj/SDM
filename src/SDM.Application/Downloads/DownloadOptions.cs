namespace SDM.Application.Downloads;

public sealed class DownloadOptions
{
    public const string SectionName = "Downloads";

    /// <summary>
    /// How many transfers may run at once across the whole application.
    /// </summary>
    public int MaximumConcurrent { get; init; } = 3;

    /// <summary>
    /// How many may run against a single host. Servers enforce their own per-client
    /// limits — exceeding them earns a 429 rather than more speed.
    /// </summary>
    public int MaximumPerHost { get; init; } = 2;

    /// <summary>Total attempts, including the first. 1 disables retrying.</summary>
    public int MaximumAttempts { get; init; } = 4;

    /// <summary>
    /// Fail a transfer whose connection goes silent for this long. The HTTP client's own
    /// timeout is disabled because it spans the whole body and would kill large files.
    /// </summary>
    public int IdleTimeoutSeconds { get; init; } = 60;

    /// <summary>An upper bound on an honoured <c>Retry-After</c>; servers may ask for hours.</summary>
    public int MaximumRetryDelaySeconds { get; init; } = 60;
}
