namespace SDM.Desktop.ViewModels;

/// <summary>
/// What removing a row should do to what it downloaded. Spelled out rather than passed as
/// a bool: this is the difference between tidying a list and destroying a file, and the
/// two must not be told apart by reading a true or false at the call site.
/// </summary>
public enum TransferRemoval
{
    /// <summary>Take the row away. Whatever is on disk stays there.</summary>
    KeepFile,

    /// <summary>Take the row away and delete the file, or the partial file, with it.</summary>
    DeleteFile,
}
