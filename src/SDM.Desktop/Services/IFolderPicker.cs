namespace SDM.Desktop.Services;

/// <summary>Picks a folder. Null when the user dismissed the dialog.</summary>
public interface IFolderPicker
{
    Task<string?> PickAsync(string startingDirectory);
}
