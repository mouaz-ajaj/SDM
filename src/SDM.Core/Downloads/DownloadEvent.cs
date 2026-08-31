namespace SDM.Core.Downloads;

public enum DownloadEventKind
{
    Information,
    Warning,
    Success,
}

/// <summary>One line of a transfer's story, for the history tab.</summary>
public sealed record DownloadEvent(DateTimeOffset At, DownloadEventKind Kind, string Text);
