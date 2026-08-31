using SDM.Core.Downloads;

namespace SDM.Desktop.ViewModels;

public enum TransferFilter
{
    All,
    Downloading,
    Queued,
    Paused,
    Completed,
    Failed,
}

public static class TransferFilters
{
    public static string NameOf(TransferFilter filter) => filter switch
    {
        TransferFilter.Downloading => "Downloading",
        TransferFilter.Queued => "Queued",
        TransferFilter.Paused => "Paused",
        TransferFilter.Completed => "Completed",
        TransferFilter.Failed => "Failed",
        _ => "All",
    };

    /// <summary>
    /// Queued and Downloading are both "active" to the engine but mean different things
    /// to someone looking at the list, so the sidebar separates them.
    /// </summary>
    public static bool Matches(TransferFilter filter, DownloadItemViewModel item) => filter switch
    {
        TransferFilter.Downloading => item.Status is DownloadStatus.Running,
        TransferFilter.Queued => item.Status is DownloadStatus.Pending,
        TransferFilter.Paused => item.Status is DownloadStatus.Paused,
        TransferFilter.Completed => item.Status is DownloadStatus.Completed,
        TransferFilter.Failed => item.Status is DownloadStatus.Failed or DownloadStatus.Cancelled,
        _ => true,
    };
}
