namespace SDM.Application.Downloads;

public sealed class DownloadOptions
{
    public const string SectionName = "Downloads";

    /// <summary>
    /// How many transfers may run at once. Everything beyond this waits its turn, which
    /// is what keeps ten queued files from starving each other's bandwidth.
    /// </summary>
    public int MaximumConcurrent { get; init; } = 3;
}
