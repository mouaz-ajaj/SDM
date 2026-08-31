using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace SDM.Desktop.Services;

public sealed class StorageProviderFolderPicker : IFolderPicker
{
    private TopLevel? _topLevel;

    public void Attach(TopLevel topLevel) => _topLevel = topLevel;

    public async Task<string?> PickAsync(string startingDirectory)
    {
        if (_topLevel is null)
        {
            return null;
        }

        IStorageFolder? start = null;

        if (!string.IsNullOrWhiteSpace(startingDirectory))
        {
            start = await _topLevel.StorageProvider.TryGetFolderFromPathAsync(startingDirectory);
        }

        IReadOnlyList<IStorageFolder> chosen = await _topLevel.StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions
            {
                Title = "Choose a folder",
                AllowMultiple = false,
                SuggestedStartLocation = start,
            });

        return chosen.Count == 0 ? null : chosen[0].Path.LocalPath;
    }
}
