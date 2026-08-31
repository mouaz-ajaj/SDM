using Avalonia.Controls;
using Avalonia.Interactivity;
using SDM.Desktop.Services;
using SDM.Desktop.ViewModels;

namespace SDM.Desktop.Views;

public sealed partial class SettingsWindow : Window
{
    private readonly StorageProviderFolderPicker? _folderPicker;

    public SettingsWindow()
    {
        InitializeComponent();
    }

    public SettingsWindow(SettingsViewModel viewModel, StorageProviderFolderPicker folderPicker)
        : this()
    {
        DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _folderPicker = folderPicker ?? throw new ArgumentNullException(nameof(folderPicker));
    }

    /// <summary>
    /// The folder picker needs a window to hang a dialog on, and a settings window is a
    /// better host than the main one while it is in front.
    /// </summary>
    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        _folderPicker?.Attach(this);
    }
}
