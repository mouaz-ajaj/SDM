namespace SDM.Desktop.Services;

/// <summary>
/// The one seam between a view model and the desktop environment: opening a file with
/// whatever program owns it, showing a file inside its folder, and the clipboard. Behind
/// an interface so a transfer row can offer all three without holding a window.
/// </summary>
public interface ISystemShell
{
    /// <summary>
    /// Opens the file with its default program. False when it is no longer on disk —
    /// a file can be moved or deleted long after the row that downloaded it was written.
    /// </summary>
    bool Open(string path);

    /// <summary>
    /// Shows the file in its folder, selected. A transfer that has not produced its final
    /// file yet opens the folder it is going to land in instead.
    /// </summary>
    bool Reveal(string path);

    Task CopyAsync(string text);
}
