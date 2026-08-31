using SDM.Core.Downloads;

namespace SDM.Desktop.Services;

/// <summary>
/// Asks the user where to put a file. Behind an interface so the view model can be
/// reasoned about without a window, and so the picker can be absent entirely — which it
/// is until a window has loaded.
/// </summary>
public interface ISaveLocationPicker
{
    /// <summary>Null when the user dismissed the dialog, which means "do not download".</summary>
    Task<DownloadDestination?> PickAsync(string suggestedFileName, string startingDirectory);
}
