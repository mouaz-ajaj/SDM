using Avalonia.Controls;
using Avalonia.Platform.Storage;
using SDM.Core.Downloads;

namespace SDM.Desktop.Services;

/// <summary>
/// The system save dialog, reached through Avalonia's storage provider. The window
/// attaches itself once it has loaded; before that there is no top level to host a
/// dialog and the picker simply reports that nothing was chosen.
/// </summary>
public sealed class StorageProviderSaveLocationPicker : ISaveLocationPicker
{
    private TopLevel? _topLevel;

    public void Attach(TopLevel topLevel) => _topLevel = topLevel;

    public async Task<DownloadDestination?> PickAsync(string suggestedFileName, string startingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(suggestedFileName);

        if (_topLevel is null)
        {
            return null;
        }

        IStorageFolder? start = null;

        if (!string.IsNullOrWhiteSpace(startingDirectory))
        {
            start = await _topLevel.StorageProvider.TryGetFolderFromPathAsync(startingDirectory);
        }

        IStorageFile? file = await _topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save download as",
            SuggestedFileName = suggestedFileName,
            SuggestedStartLocation = start,
            DefaultExtension = Path.GetExtension(suggestedFileName).TrimStart('.'),
            ShowOverwritePrompt = true,
        });

        if (file is null)
        {
            return null;
        }

        string path = file.Path.LocalPath;
        string? directory = Path.GetDirectoryName(path);

        return string.IsNullOrEmpty(directory)
            ? null
            : new DownloadDestination(directory, Path.GetFileName(path));
    }
}
