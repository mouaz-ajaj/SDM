using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using SDM.Core.Downloads;
using SDM.Desktop.ViewModels;
using SDM.Desktop.Views;

namespace SDM.Desktop.Services;

/// <summary>
/// Shows SDM's own save dialog rather than the system file picker. The system dialog can
/// only ask for a path; this one also shows what the server said the file is — its real
/// name, size, type and whether it can be resumed — which is what actually decides where
/// someone wants to put it.
/// </summary>
public sealed class DialogSaveLocationPicker : ISaveLocationPicker
{
    private readonly IServiceProvider _services;
    private readonly StorageProviderFolderPicker _folderPicker;

    private Window? _owner;

    public DialogSaveLocationPicker(IServiceProvider services, StorageProviderFolderPicker folderPicker)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(folderPicker);

        _services = services;
        _folderPicker = folderPicker;
    }

    public void Attach(Window owner) => _owner = owner;

    public async Task<DownloadDestination?> PickAsync(
        string address, DownloadProbe probe, string startingDirectory)
    {
        ArgumentNullException.ThrowIfNull(probe);

        if (_owner is null)
        {
            return null;
        }

        SaveDownloadViewModel viewModel = new(_folderPicker, address, probe, startingDirectory);
        SaveDownloadWindow window = new(viewModel);

        // The folder picker follows the dialog: it needs a window in front to hang on.
        _folderPicker.Attach(window);

        try
        {
            await window.ShowDialog(_owner);
        }
        finally
        {
            _folderPicker.Attach(_owner);
        }

        return viewModel.Result;
    }

    /// <summary>
    /// Asks before something that cannot be undone. False when there is no window to ask
    /// from — an unanswerable question is not consent.
    /// </summary>
    public async Task<bool> ConfirmAsync(string title, string message, string confirmLabel)
    {
        if (_owner is null)
        {
            return false;
        }

        ConfirmViewModel viewModel = new(title, message, confirmLabel);
        ConfirmWindow window = new(viewModel);

        await window.ShowDialog(_owner);

        return viewModel.Confirmed;
    }

    /// <summary>Opens the settings window over the main one.</summary>
    public async Task ShowSettingsAsync()
    {
        if (_owner is null)
        {
            return;
        }

        SettingsWindow window = _services.GetRequiredService<SettingsWindow>();

        try
        {
            await window.ShowDialog(_owner);
        }
        finally
        {
            _folderPicker.Attach(_owner);
        }
    }
}
