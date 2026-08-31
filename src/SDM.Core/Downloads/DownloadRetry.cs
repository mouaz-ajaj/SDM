namespace SDM.Core.Downloads;

/// <summary>Reported before each retry so the user sees a countdown rather than a stall.</summary>
public sealed record DownloadRetry(int Attempt, int MaximumAttempts, TimeSpan Delay, string Reason);
